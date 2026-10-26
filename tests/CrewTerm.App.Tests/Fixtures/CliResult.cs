namespace CrewTerm.App.Tests.Fixtures;

/// <summary>
/// 以真实进程运行 <c>crewterm</c> 命令行得到的结果。
/// </summary>
/// <param name="ExitCode">进程退出码。</param>
/// <param name="Output">标准输出全文。</param>
/// <param name="Error">标准错误全文。</param>
internal sealed record CliResult(int ExitCode, string Output, string Error);
