using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CrewTerm.App.Client.Native;

namespace CrewTerm.App.Client;

/// <summary>
/// 以完全脱离调用方的方式启动 daemon 进程。
/// </summary>
/// <remarks>
/// 直接调用 <c>CreateProcessW</c> 而不是 <see cref="System.Diagnostics.Process"/>：
/// 不继承任何句柄（调用方被重定向的 stdin/stdout/stderr 不会因 daemon 而一直保持打开，
/// 否则脚本中 <c>crewterm sessions | ConvertFrom-Json</c> 会等不到 EOF），也不创建控制台窗口。
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
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = StartUseStdHandles };
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
        // 工作目录使用程序所在目录，而不是调用方的当前目录：长期运行的 daemon
        // 若持有用户项目目录的句柄，会导致该目录在 daemon 退出前无法删除或重命名。
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? AppContext.BaseDirectory;
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
