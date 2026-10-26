namespace CrewTerm.App.Protocol;

/// <summary>
/// 后台任务的对外快照（通过 daemon 协议返回给 CLI 与 UI）。
/// </summary>
/// <param name="Id">任务标识。</param>
/// <param name="Name">任务名称。</param>
/// <param name="Command">命令文本；流水线为每行一个 “步骤: 命令”。</param>
/// <param name="WorkingDirectory">工作目录（流水线为第一步的目录）。</param>
/// <param name="StartedAt">提交时间（UTC）。</param>
/// <param name="Completed">是否已结束（成功、失败或取消）。</param>
/// <param name="ExitCode">真实退出码；未结束、未能启动或排队时被取消为 <see langword="null"/>。</param>
/// <param name="Cancelled">是否被取消。</param>
/// <param name="Output">合并输出的末尾部分；列表接口中为空字符串。</param>
/// <param name="Error">CrewTerm 自身的错误说明（如无法启动、历史无法保存）；没有时为 <see langword="null"/>。</param>
public sealed record DaemonTaskInfo(string Id, string Name, string Command, string WorkingDirectory,
    DateTimeOffset StartedAt, bool Completed, int? ExitCode, bool Cancelled, string Output, string? Error);
