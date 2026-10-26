using System.Text.RegularExpressions;
using CrewTerm.Core.IO;
using CrewTerm.Core.Security;

namespace CrewTerm.Automation.Activity;

/// <summary>
/// 持久化 Agent 的语义活动状态、显式确认（通知已读、消息已确认）以及会话之间的定向邮箱。
/// </summary>
/// <remarks>
/// <para>全部状态保存在一个 <see cref="AgentActivitySnapshot"/> 中，每次修改都整体原子写入
/// <c>agent-activity.json</c>，并递增全局版本号 <see cref="AgentActivitySnapshot.Revision"/>。</para>
/// <para>线程安全：所有公开成员都在同一把进程内锁中读写内存快照。本类假定每个状态目录只有一个实例
/// （由 daemon 持有）负责写入；它在构造时读取文件一次，之后不会重新加载，也不使用跨进程互斥锁，
/// 因此其他进程同时写同一文件会被覆盖。</para>
/// <para>状态只来自显式证据：API 上报、Provider hook、用户确认，以及少数已知的审批提示文本；
/// 普通输出或长时间无输出都不会被当作“已完成”。</para>
/// </remarks>
public sealed partial class AgentActivityStore
{
    /// <summary>
    /// 保护 <see cref="data"/>、<see cref="recentOutput"/> 与 <see cref="changed"/> 的进程内锁。
    /// </summary>
    private readonly object gate = new();

    /// <summary>
    /// 状态文件 <c>agent-activity.json</c> 的绝对路径。
    /// </summary>
    private readonly string path;

    /// <summary>
    /// 保留的通知条数上限；每次提交时只保留最近的这么多条。
    /// </summary>
    private readonly int maximumNotifications;

    /// <summary>
    /// 保留的消息条数上限；每次提交时只保留最近的这么多条。
    /// </summary>
    private readonly int maximumMessages;

    /// <summary>
    /// 每个会话最近的终端输出（至多 4096 个字符，按会话标识序数比较），用于识别跨多个输出块的审批提示。
    /// </summary>
    /// <remarks>
    /// 只存在于内存中，不会持久化；收到显式上报或会话被忘记时清除。
    /// </remarks>
    private readonly Dictionary<string, string> recentOutput = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前的完整状态快照，与磁盘文件内容一致；每次提交整体替换。
    /// </summary>
    private AgentActivitySnapshot data;

    /// <summary>
    /// “状态已变化”的一次性信号：每次提交时完成当前信号并换上新的，唤醒所有正在 <see cref="WaitAsync"/> 中等待的调用方。
    /// </summary>
    private TaskCompletionSource changed = NewSignal();

    /// <summary>
    /// 打开状态目录中的活动存储并校验现有文件；文件损坏时保留原文件，保留的事件历史有条数上限。
    /// </summary>
    /// <param name="stateDirectory">状态目录，不存在时自动创建。</param>
    /// <param name="maximumNotifications">最多保留的通知条数，至少为 1。</param>
    /// <param name="maximumMessages">最多保留的会话间消息条数，至少为 1。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一上限小于 1（参数名统一报告为 <paramref name="maximumNotifications"/>）。</exception>
    /// <exception cref="InvalidDataException">现有文件不是有效 JSON，或含有空记录、空标识、未定义的状态值；原文件保持不变。</exception>
    public AgentActivityStore(string stateDirectory, int maximumNotifications = 200, int maximumMessages = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNotifications);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        StateFiles.EnsureDirectory(stateDirectory);
        path = StateFiles.PathOf(stateDirectory, StateFiles.AgentActivity);
        this.maximumNotifications = maximumNotifications;
        this.maximumMessages = maximumMessages;
        data = AtomicJsonStore.Read(path, () => new AgentActivitySnapshot(0, [], [], []));
        if (data.Activities is null || data.Notifications is null || data.Messages is null
            || data.Activities.Any(item => item is null || string.IsNullOrWhiteSpace(item.SessionId) || !Enum.IsDefined(item.State))
            || data.Notifications.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id))
            || data.Messages.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id)))
            throw new InvalidDataException("Invalid Agent activity data; original file preserved: " + path);
    }

    /// <summary>
    /// 登记一个已知会话；不会根据进程是否存活杜撰活动状态。
    /// </summary>
    /// <remarks>
    /// 会话已登记时原样返回现有观测，不提交任何修改；否则以 <see cref="AgentActivityState.Unknown"/>、
    /// 来源 <c>process</c> 新建观测并提交。
    /// </remarks>
    /// <param name="sessionId">CrewTerm 会话标识。</param>
    /// <returns>该会话当前（或新建）的观测。</returns>
    public AgentActivity Register(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            var existing = data.Activities.FirstOrDefault(item => item.SessionId == sessionId);
            if (existing is not null) return existing;
            var activity = new AgentActivity(sessionId, AgentActivityState.Unknown, "process",
                "Session registered; no explicit activity event received", DateTimeOffset.UtcNow, data.Revision + 1);
            Commit(data with { Revision = activity.Revision, Activities = [.. data.Activities, activity] });
            return activity;
        }
    }

    /// <summary>
    /// 获取一个已显式登记会话的最新观测。
    /// </summary>
    /// <param name="sessionId">CrewTerm 会话标识。</param>
    /// <returns>该会话的最新观测。</returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public AgentActivity Get(string sessionId)
    {
        lock (gate) return Find(sessionId);
    }

    /// <summary>
    /// 返回全部已登记会话的不可变观测（按登记顺序）；不会改变任何未读状态。
    /// </summary>
    /// <returns>观测数组的副本。</returns>
    public IReadOnlyList<AgentActivity> List() { lock (gate) return data.Activities.ToArray(); }

    /// <summary>
    /// 忘记一个被显式移除的会话，同时删除它的通知以及它发出或收到的所有消息。
    /// </summary>
    /// <param name="sessionId">CrewTerm 会话标识。</param>
    /// <returns>会话存在并已删除时返回 <see langword="true"/>；未登记时返回 <see langword="false"/> 且不修改文件。</returns>
    public bool Forget(string sessionId)
    {
        lock (gate)
        {
            if (!data.Activities.Any(item => item.SessionId == sessionId)) return false;
            Commit(data with
            {
                Revision = data.Revision + 1,
                Activities = data.Activities.Where(item => item.SessionId != sessionId).ToArray(),
                Notifications = data.Notifications.Where(item => item.SessionId != sessionId).ToArray(),
                Messages = data.Messages.Where(item => item.FromSessionId != sessionId && item.ToSessionId != sessionId).ToArray()
            });
            recentOutput.Remove(sessionId);
            return true;
        }
    }

    /// <summary>
    /// 记录一个显式事件，附带可读的来源与原因。
    /// </summary>
    /// <remarks>
    /// 会清空该会话缓存的近期输出，使之前的审批提示文本不会在之后被再次识别。
    /// 状态、来源、原因都与当前观测相同时不提交修改，直接返回当前观测（版本号不变）。
    /// 状态变为 <see cref="AgentActivityState.Blocked"/> 或 <see cref="AgentActivityState.Done"/> 时会生成一条未读通知。
    /// </remarks>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <param name="state">新的语义状态，必须是已定义的枚举值。</param>
    /// <param name="source">证据来源标签，非空且最长 64 个字符。</param>
    /// <param name="reason">面向用户的原因说明，非空且最长 4096 个字符。</param>
    /// <returns>记录后的当前观测。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> 不是已定义的枚举值。</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> 或 <paramref name="reason"/> 为空白、超长或含有控制字符（<paramref name="reason"/> 允许换行与制表符）。</exception>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public AgentActivity Report(string sessionId, AgentActivityState state, string source, string reason)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (source.Length > 64 || reason.Length > 4096) throw new ArgumentException("Activity source or reason is too long");
        // 来源与原因会显示在工作台状态栏和通知列表中，拒绝可能被宿主终端执行的控制字符。
        if (DisplayText.ContainsControl(source, allowLineBreaks: false) || DisplayText.ContainsControl(reason, allowLineBreaks: true))
            throw new ArgumentException("Activity source or reason must not contain control characters");
        lock (gate)
        {
            recentOutput.Remove(sessionId);
            return Transition(sessionId, state, source, reason);
        }
    }

    /// <summary>
    /// 观察终端输出，只识别已知的审批提示；普通文本和长时间无输出都不会被当作完成。
    /// </summary>
    /// <remarks>
    /// <para>把输出追加到该会话的近期输出缓存（只保留末尾 4096 个字符），按 <see cref="PlainText"/> 去掉终端控制序列后，
    /// 按行首匹配 <c>Do you want to proceed?</c>、<c>Would you like to proceed?</c>、<c>Allow this command?</c>
    /// （不区分大小写）；匹配时把状态转为 <see cref="AgentActivityState.Blocked"/>，来源为 <c>screen</c>。</para>
    /// <para>会话已退出，或当前观测来自 <c>api</c> / <c>hook</c> 这类更可靠的显式来源时，直接忽略输出，
    /// 避免屏幕上的旧文本覆盖显式状态。</para>
    /// </remarks>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <param name="output">新到达的一段终端输出（可能含 ANSI 控制序列）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public void ObserveOutput(string sessionId, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (gate)
        {
            var current = Find(sessionId);
            if (current.State == AgentActivityState.Exited || current.Source is "api" or "hook") return;
            var text = recentOutput.GetValueOrDefault(sessionId, "") + output;
            if (text.Length > 4096) text = text[^4096..];
            recentOutput[sessionId] = text;
            if (ApprovalPrompt().IsMatch(PlainText(text)))
                Transition(sessionId, AgentActivityState.Blocked, "screen", "Recognized approval prompt; input requires a user decision");
        }
    }

    /// <summary>
    /// 记录终端进程退出；它与语义上的任务完成相互独立，不会生成“已完成”通知。
    /// </summary>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <param name="exitCode">进程退出码；未知时为 <see langword="null"/>，原因文本中显示为 <c>unknown</c>。</param>
    /// <returns>状态为 <see cref="AgentActivityState.Exited"/>、来源为 <c>process</c> 的观测。</returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    public AgentActivity MarkExited(string sessionId, int? exitCode)
        => Report(sessionId, AgentActivityState.Exited, "process", "Terminal process exited with code " + (exitCode?.ToString() ?? "unknown"));

    /// <summary>
    /// 异步等待会话进入目标状态之一（可选地要求版本号比已知版本更新），等待时间有上限。
    /// </summary>
    /// <remarks>
    /// 不轮询：每次检查不满足时，等待下一次提交触发的变化信号再重新检查。
    /// 若当前观测已满足条件则立即返回。
    /// </remarks>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <param name="states">可接受的目标状态，至少一个且都必须是已定义的枚举值。</param>
    /// <param name="afterRevision">调用方已知的版本号；提供时只接受 <see cref="AgentActivity.Revision"/> 大于它的观测，
    /// 为 <see langword="null"/> 时不限制版本。</param>
    /// <param name="timeout">最长等待时间，必须大于零且不超过 60 秒。</param>
    /// <param name="cancellationToken">调用方取消；取消时抛出 <see cref="OperationCanceledException"/> 而不是超时异常。</param>
    /// <returns>满足条件的观测。</returns>
    /// <exception cref="ArgumentException"><paramref name="states"/> 为空或含未定义的状态。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> 不在 (0, 60] 秒范围内。</exception>
    /// <exception cref="TimeoutException">超时前未达到目标状态。</exception>
    /// <exception cref="KeyNotFoundException">会话尚未登记，或在等待期间被忘记。</exception>
    public async Task<AgentActivity> WaitAsync(string sessionId, IReadOnlyCollection<AgentActivityState> states,
        long? afterRevision, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (states.Count == 0 || states.Any(state => !Enum.IsDefined(state))) throw new ArgumentException("At least one valid target state is required", nameof(states));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(timeout), "Wait timeout must be greater than zero and at most 60 seconds");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                Task next;
                lock (gate)
                {
                    var current = Find(sessionId);
                    if (states.Contains(current.State) && (!afterRevision.HasValue || current.Revision > afterRevision)) return current;
                    next = changed.Task;
                }
                await next.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Timed out waiting for Agent state: " + sessionId); }
    }

    /// <summary>
    /// 在当前快照中查找会话的观测；调用方必须已持有 <see cref="gate"/>。
    /// </summary>
    /// <param name="sessionId">CrewTerm 会话标识（序数比较）。</param>
    /// <returns>该会话的最新观测。</returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    private AgentActivity Find(string sessionId) => data.Activities.FirstOrDefault(item => item.SessionId == sessionId)
        ?? throw new KeyNotFoundException("Agent session not found: " + sessionId);

    /// <summary>
    /// 把会话切换到新的观测并提交；调用方必须已持有 <see cref="gate"/>。
    /// </summary>
    /// <remarks>
    /// 状态、来源、原因都与当前观测相同时视为重复事件，不提交也不递增版本号。
    /// 只有状态真正变为 <see cref="AgentActivityState.Blocked"/>（标题 <c>Needs input</c>）或
    /// <see cref="AgentActivityState.Done"/>（标题 <c>Completed</c>）时才追加一条未读通知；
    /// 同一状态下仅原因变化不会重复通知。
    /// </remarks>
    /// <param name="sessionId">已登记的 CrewTerm 会话标识。</param>
    /// <param name="state">新的语义状态。</param>
    /// <param name="source">证据来源标签。</param>
    /// <param name="reason">面向用户的原因说明。</param>
    /// <returns>切换后的观测；重复事件时为原观测。</returns>
    /// <exception cref="KeyNotFoundException">会话尚未登记。</exception>
    private AgentActivity Transition(string sessionId, AgentActivityState state, string source, string reason)
    {
        var previous = Find(sessionId);
        if (previous.State == state && previous.Source == source && previous.Reason == reason) return previous;
        var current = new AgentActivity(sessionId, state, source, reason, DateTimeOffset.UtcNow, data.Revision + 1);
        var notifications = data.Notifications;
        if (previous.State != state && state is AgentActivityState.Blocked or AgentActivityState.Done)
            notifications = [.. notifications, new(Guid.NewGuid().ToString("N"), sessionId, state,
                state == AgentActivityState.Blocked ? "Needs input" : "Completed", reason, current.ChangedAt, false, source)];
        Commit(data with { Revision = current.Revision, Activities = data.Activities.Select(item => item.SessionId == sessionId ? current : item).ToArray(), Notifications = notifications });
        return current;
    }

    /// <summary>
    /// 提交新快照：裁剪通知与消息到上限、原子写入文件、替换内存快照，然后唤醒所有等待者。
    /// 调用方必须已持有 <see cref="gate"/>。
    /// </summary>
    /// <remarks>
    /// 先写文件再替换内存状态：写入失败（抛出异常）时内存快照与等待者都保持不变。
    /// 信号以 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> 创建，
    /// 等待者的后续代码不会在持锁的提交线程上同步执行。
    /// </remarks>
    /// <param name="next">要提交的完整快照，版本号由调用方设置。</param>
    private void Commit(AgentActivitySnapshot next)
    {
        next = next with { Notifications = next.Notifications.TakeLast(maximumNotifications).ToArray(), Messages = next.Messages.TakeLast(maximumMessages).ToArray() };
        AtomicJsonStore.Save(path, next);
        data = next;
        var signal = changed;
        changed = NewSignal();
        signal.TrySetResult();
    }

    /// <summary>
    /// 创建一个新的“状态已变化”信号，其后续代码异步执行。
    /// </summary>
    /// <returns>尚未完成的信号。</returns>
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 识别已知审批提示的正则：多行、不区分大小写，要求提示位于行首（允许前导空白）。
    /// </summary>
    /// <returns>由源生成器生成的正则实例。</returns>
    [GeneratedRegex(@"(?im)^\s*(?:do you want to proceed\?|would you like to proceed\?|allow this command\?)")]
    private static partial Regex ApprovalPrompt();

    /// <summary>
    /// 去掉终端输出中的控制序列，得到用于识别提示的纯文本：文字在屏幕上从第几列开始，纯文本中就尽量从同样的位置开始。
    /// </summary>
    /// <remarks>
    /// <para>Windows ConPTY 输出的不是程序写出的原始字节，而是它对屏幕变化的重绘，同一段文字在不同版本、不同时机下的编码不同：</para>
    /// <list type="bullet">
    /// <item><description>把光标换行或定位到某行的 CSI 序列（<c>CUP H</c>、<c>HVP f</c>、<c>VPA d</c>、<c>CNL E</c>、<c>CPL F</c>）
    /// 以及回到第 1 列的 <c>CHA G</c> 换成换行；直接删除会把新的一行接到上一行末尾，使行首的提示无法识别。</description></item>
    /// <item><description>光标右移 <c>CUF C</c> 换成同样数量的空格：重绘时连续的空格常被编码为右移，直接删除会把
    /// <c>Do you want</c> 变成 <c>Doyouwant</c>。</description></item>
    /// <item><description>OSC 序列（例如设置窗口标题的 <c>ESC ] 0 ; … BEL</c>）与其他 <c>ESC</c> 开头的短序列整段删除；
    /// 标题变化可能恰好输出在提示文字所在行的开头。</description></item>
    /// <item><description>后面不是换行符的回车 <c>CR</c> 换成换行：回车之后的文字从第 1 列开始显示。</description></item>
    /// </list>
    /// <para>其余 CSI 序列（颜色、清除、光标显示等）直接删除。</para>
    /// </remarks>
    /// <param name="text">终端输出。</param>
    /// <returns>纯文本。</returns>
    private static string PlainText(string text) => TerminalControl().Replace(text, match =>
    {
        var value = match.ValueSpan;
        if (value[0] == '\r') return "\n";
        if (value.Length < 2 || value[1] != '[') return "";
        var parameter = value[2..^1];
        return value[^1] switch
        {
            'H' or 'f' or 'd' or 'E' or 'F' => "\n",
            'G' => parameter.IsEmpty || parameter is "1" ? "\n" : "",
            'C' => new string(' ', int.TryParse(parameter, out var count) ? Math.Clamp(count, 1, 256) : 1),
            _ => ""
        };
    });

    /// <summary>
    /// 匹配需要在识别提示前处理的终端控制内容：后面不是换行符的回车、OSC 序列（以 <c>BEL</c> 或 <c>ESC \</c> 结束）、
    /// CSI 序列（<c>ESC [</c> 参数字节、中间字节、结束字节），以及其他 <c>ESC</c> 开头的短序列。
    /// </summary>
    /// <returns>由源生成器生成的正则实例。</returns>
    [GeneratedRegex("\r(?!\n)|\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)|\u001b\\[[0-?]*[ -/]*[@-~]|\u001b[ -/]*[0-~]")]
    private static partial Regex TerminalControl();
}
