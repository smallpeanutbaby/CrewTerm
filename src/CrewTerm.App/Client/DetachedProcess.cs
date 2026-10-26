using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CrewTerm.App.Client.Native;

namespace CrewTerm.App.Client;

/// <summary>
/// 以完全脱离调用方的方式启动 daemon 进程。
/// </summary>
/// <remarks>
/// <para>daemon 绝不能继承调用方的标准输入输出：否则脚本中 <c>crewterm sessions | ConvertFrom-Json</c> 之类的管道会一直等不到 EOF。</para>
/// <para>Windows：直接调用 <c>CreateProcessW</c>，不继承任何句柄，也不创建控制台窗口。</para>
/// <para>Linux / macOS：标准输入输出重定向到新管道后立即关闭本端；daemon 启动后调用
/// <see cref="CrewTerm.Terminal.Pty.TerminalSession.DetachCurrentProcess"/> 创建新会话并改接到 <c>/dev/null</c>。</para>
/// </remarks>
internal static class DetachedProcess
{
    /// <summary>
    /// <c>STARTF_USESTDHANDLES</c>：配合空句柄，使子进程不获得任何标准句柄。
    /// </summary>
    private const int StartUseStdHandles = 0x100;

    /// <summary>
    /// <c>CREATE_NO_WINDOW</c>：不为子进程创建控制台窗口。
    /// </summary>
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>
    /// 启动进程后立即关闭其句柄，不等待、不跟踪。
    /// </summary>
    /// <param name="executable">可执行文件绝对路径。</param>
    /// <param name="arguments">参数列表，会按 Windows CRT 规则逐个加引号转义。</param>
    /// <exception cref="Win32Exception">进程创建失败。</exception>
    internal static void Start(string executable, IReadOnlyList<string> arguments)
    {
        // 工作目录使用程序所在目录，而不是调用方的当前目录：长期运行的 daemon
        // 若持有用户项目目录的句柄，会导致该目录在 daemon 退出前无法删除或重命名（Windows），或无法卸载所在卷（Unix）。
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows()) StartWindows(executable, arguments, directory);
        else StartUnix(executable, arguments, directory);
    }

    /// <summary>
    /// Linux / macOS：启动进程，并立即关闭与它相连的标准输入输出管道。
    /// </summary>
    /// <param name="executable">可执行文件绝对路径。</param>
    /// <param name="arguments">参数列表，逐个原样传递。</param>
    /// <param name="directory">工作目录。</param>
    /// <exception cref="Win32Exception">进程创建失败。</exception>
    private static void StartUnix(string executable, IReadOnlyList<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Win32Exception("Cannot start the CrewTerm daemon");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    /// <summary>
    /// Windows：以 <c>CreateProcessW</c> 启动进程，不继承句柄、不创建窗口，启动后立即关闭进程与线程句柄。
    /// </summary>
    /// <param name="executable">可执行文件绝对路径。</param>
    /// <param name="arguments">参数列表，会按 Windows CRT 规则逐个加引号转义。</param>
    /// <param name="directory">工作目录。</param>
    /// <exception cref="Win32Exception">进程创建失败。</exception>
    private static void StartWindows(string executable, IReadOnlyList<string> arguments, string directory)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = StartUseStdHandles };
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
        // 不继承句柄：daemon 绝不能让调用方被重定向的 stdin/stdout/stderr 保持打开。
        if (!CreateProcess(executable, command, 0, 0, false, CreateNoWindow, 0,
                directory, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
    }

    /// <summary>
    /// 按 <c>CommandLineToArgvW</c> / MSVC CRT 规则为单个参数加引号：
    /// 引号前的反斜杠加倍并转义引号，结尾的反斜杠加倍。
    /// </summary>
    /// <param name="value">原始参数。</param>
    /// <returns>带引号的参数文本。</returns>
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    /// <summary>
    /// 创建进程（<c>CreateProcessW</c>）。
    /// </summary>
    /// <param name="applicationName">可执行文件路径。</param>
    /// <param name="commandLine">完整命令行（可被系统修改，因此使用可写缓冲）。</param>
    /// <param name="processAttributes">进程安全属性。</param>
    /// <param name="threadAttributes">线程安全属性。</param>
    /// <param name="inheritHandles">是否继承句柄。</param>
    /// <param name="flags">创建标志。</param>
    /// <param name="environment">环境块；零表示继承。</param>
    /// <param name="directory">工作目录。</param>
    /// <param name="startupInfo">启动信息。</param>
    /// <param name="processInformation">返回的进程与线程句柄。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    /// <summary>
    /// 关闭内核对象句柄。
    /// </summary>
    /// <param name="handle">要关闭的句柄。</param>
    /// <returns>成功时为 <see langword="true"/>。</returns>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
