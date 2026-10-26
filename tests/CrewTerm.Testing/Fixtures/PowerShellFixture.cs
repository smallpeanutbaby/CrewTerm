using System.Text;
using CrewTerm.Testing.Platform;

namespace CrewTerm.Testing.Fixtures;

/// <summary>
/// 测试中以真实进程运行 PowerShell 脚本的统一入口。
/// </summary>
/// <remarks>
/// <para>优先使用 PowerShell 7（<c>pwsh</c>，Linux / macOS 上必需，GitHub Actions 的各平台运行器均已预装）；
/// Windows 上未安装时退回系统自带的 Windows PowerShell 5.1，使测试不依赖额外安装的运行时。</para>
/// <para>为让两者行为一致：脚本以 UTF-8 BOM 保存（5.1 否则按 ANSI 代码页读取脚本），
/// 开头把控制台输入输出编码设为 UTF-8（5.1 默认使用 OEM 代码页），并以 <c>-ExecutionPolicy Bypass</c> 启动
/// （Windows 客户端版 5.1 默认禁止运行脚本）。</para>
/// </remarks>
public static class PowerShellFixture
{
    /// <summary>
    /// 写在每个脚本开头的编码设置；没有控制台时设置会失败，忽略即可。
    /// </summary>
    private const string Utf8Prelude =
        "try { [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { }\n";

    /// <summary>
    /// PowerShell 可执行文件的绝对路径；Linux / macOS 上未安装时为 <c>pwsh</c>（运行时报告找不到程序）。
    /// </summary>
    public static string Executable { get; } = TestShell.Find("pwsh")
        ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "pwsh");

    /// <summary>
    /// 以文件方式运行脚本的参数列表（不含可执行文件本身）。
    /// </summary>
    /// <param name="script">脚本绝对路径。</param>
    /// <returns>按顺序传递的参数。</returns>
    public static string[] FileArguments(string script)
        => ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];

    /// <summary>
    /// 运行脚本的完整命令行（可执行文件与脚本路径都带双引号），可直接写入 <c>.cmd</c>、<c>/bin/sh</c> 脚本或交给 Shell。
    /// </summary>
    /// <param name="script">脚本绝对路径。</param>
    /// <returns>命令行文本。</returns>
    public static string CommandLine(string script)
        => $"\"{Executable}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"";

    /// <summary>
    /// 以 UTF-8 BOM 写入脚本，并在开头加上统一的编码设置。
    /// </summary>
    /// <remarks>
    /// <c>param(...)</c> 必须是脚本的第一条语句，因此单行 <c>param</c> 块之后才插入编码设置。
    /// </remarks>
    /// <param name="path">脚本路径。</param>
    /// <param name="body">脚本正文。</param>
    public static void WriteScript(string path, string body)
    {
        var text = body.TrimStart();
        var script = Utf8Prelude + text;
        if (text.StartsWith("param(", StringComparison.OrdinalIgnoreCase))
        {
            var lineEnd = text.IndexOf('\n');
            script = lineEnd < 0 ? text + "\n" + Utf8Prelude : text[..(lineEnd + 1)] + Utf8Prelude + text[(lineEnd + 1)..];
        }
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
