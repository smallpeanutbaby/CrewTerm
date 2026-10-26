namespace CrewTerm.Workspace.Git;

/// <summary>
/// 一次 Git 调用的结果。
/// </summary>
/// <param name="Output">标准输出全文。</param>
/// <param name="ExitCode">进程退出码。</param>
internal sealed record GitResult(string Output, int ExitCode);
