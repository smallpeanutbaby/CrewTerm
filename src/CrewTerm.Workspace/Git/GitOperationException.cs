namespace CrewTerm.Workspace.Git;

/// <summary>
/// Git 命令失败或无法启动。
/// </summary>
/// <param name="message">包含 Git 标准错误输出的说明。</param>
/// <param name="exitCode">Git 的退出码；无法启动时为 -1。</param>
public sealed class GitOperationException(string message, int exitCode = -1) : IOException(message)
{
    /// <summary>
    /// Git 的退出码；无法启动时为 -1。
    /// </summary>
    public int ExitCode { get; } = exitCode;
}
