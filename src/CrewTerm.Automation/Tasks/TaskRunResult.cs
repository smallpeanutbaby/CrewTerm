namespace CrewTerm.Automation.Tasks;

/// <summary>
/// 一次命令执行的真实结果。
/// </summary>
/// <param name="Id">运行标识。</param>
/// <param name="Name">任务名称。</param>
/// <param name="Command">执行的命令文本。</param>
/// <param name="WorkingDirectory">工作目录绝对路径。</param>
/// <param name="StartedAt">开始时间（UTC）。</param>
/// <param name="FinishedAt">结束时间（UTC）。</param>
/// <param name="ExitCode">进程退出码；进程未能启动或在排队时被取消则为 <see langword="null"/>。</param>
/// <param name="Output">标准输出与标准错误交错合并后的文本（只保留末尾部分）。</param>
/// <param name="Cancelled">是否被用户或服务停止取消。</param>
/// <param name="OutputTruncated">输出是否因超过上限而丢弃了开头部分。</param>
public sealed record TaskRunResult(
    string Id,
    string Name,
    string Command,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? ExitCode,
    string Output,
    bool Cancelled,
    bool OutputTruncated = false)
{
    /// <summary>
    /// 未取消且退出码为 0。这是判断任务成功的唯一依据，与输出中的文字无关。
    /// </summary>
    public bool Succeeded => !Cancelled && ExitCode == 0;

    /// <summary>
    /// 实际运行时长。
    /// </summary>
    public TimeSpan Duration => FinishedAt - StartedAt;
}
