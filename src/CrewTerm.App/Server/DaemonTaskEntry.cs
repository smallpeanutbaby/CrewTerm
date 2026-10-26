using CrewTerm.App.Protocol;
using CrewTerm.Automation.Tasks;

namespace CrewTerm.App.Server;

/// <summary>
/// <see cref="DaemonTaskService"/> 中一个任务的内存状态；全部字段由服务的锁保护。
/// </summary>
/// <param name="info">初始快照。</param>
/// <param name="cancellation">取消源；从历史恢复的已完成任务为 <see langword="null"/>。</param>
internal sealed class DaemonTaskEntry(DaemonTaskInfo info, CancellationTokenSource? cancellation)
{
    /// <summary>
    /// 最新快照；运行期间其 <see cref="DaemonTaskInfo.Output"/> 不更新，读取时才从 <see cref="Output"/> 生成。
    /// </summary>
    public DaemonTaskInfo Info = info;

    /// <summary>
    /// 任务的取消源；从历史恢复的已完成任务为 <see langword="null"/>。任务从内存中淘汰或服务释放时被释放。
    /// </summary>
    public readonly CancellationTokenSource? Cancellation = cancellation;

    /// <summary>
    /// 运行期间累积的输出；任务结束后清空。
    /// </summary>
    public readonly BoundedTextBuffer Output = new(DaemonTaskService.OutputLimit);

    /// <summary>
    /// 任务执行（含写历史）的完成信号。
    /// </summary>
    public Task Completion = Task.CompletedTask;
}
