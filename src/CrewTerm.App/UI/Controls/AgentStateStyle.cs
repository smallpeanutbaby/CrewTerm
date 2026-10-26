using CrewTerm.App.Protocol;
using CrewTerm.Automation.Activity;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 把会话的语义状态映射为侧栏与面板标题使用的外观，并按需要关注的程度汇总一个空间的状态。
/// </summary>
/// <remarks>
/// 外观与 herdr 一致：<c>◉</c> 等待决定（红）、<c>●</c> 工作中（黄）、<c>●</c> 已完成且未查看（绿）、<c>○</c> 空闲（灰）。
/// 已完成但通知都已读的会话按空闲显示——两者都表示可以接受新的输入。颜色取自 Catppuccin Mocha 调色板。
/// </remarks>
public static class AgentStateStyle
{
    /// <summary>
    /// 等待用户决定（审批、确认或输入）。
    /// </summary>
    public static AgentBadge Blocked { get; } = new("◉", 0xf38ba8, "blocked", 4);

    /// <summary>
    /// 正在处理任务。
    /// </summary>
    public static AgentBadge Working { get; } = new("●", 0xf9e2af, "working", 3);

    /// <summary>
    /// 已完成且有尚未查看的通知。
    /// </summary>
    public static AgentBadge Done { get; } = new("●", 0xa6e3a1, "done", 2);

    /// <summary>
    /// 空闲，可以接受新的输入。
    /// </summary>
    public static AgentBadge Idle { get; } = new("○", 0x9399b2, "idle", 1);

    /// <summary>
    /// 进程在运行，但没有足够依据判断语义状态（例如普通 Shell）。
    /// </summary>
    public static AgentBadge Unknown { get; } = new("○", 0x6c7086, "unknown", 0);

    /// <summary>
    /// 终端进程已经退出。
    /// </summary>
    public static AgentBadge Exited { get; } = new("✕", 0x6c7086, "exited", 0);

    /// <summary>
    /// 空间中没有任何 Agent。
    /// </summary>
    public static AgentBadge None { get; } = new("·", 0x45475a, "", -1);

    /// <summary>
    /// 计算一个会话的外观。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <returns>对应的外观；进程已退出时总是 <see cref="Exited"/>。</returns>
    public static AgentBadge For(SessionInfo session) => session.HasExited ? Exited : session.Activity?.State switch
    {
        AgentActivityState.Blocked => Blocked,
        AgentActivityState.Working => Working,
        AgentActivityState.Done => session.UnreadCount > 0 ? Done : Idle,
        AgentActivityState.Idle => Idle,
        AgentActivityState.Exited => Exited,
        _ => Unknown
    };

    /// <summary>
    /// 汇总一组会话的外观：取最需要关注的状态（等待决定 &gt; 工作中 &gt; 已完成未查看 &gt; 空闲 &gt; 其他）。
    /// </summary>
    /// <param name="sessions">同一空间中的会话。</param>
    /// <returns>汇总后的外观；没有会话时为 <see cref="None"/>。</returns>
    public static AgentBadge RollUp(IEnumerable<SessionInfo> sessions) =>
        sessions.Select(For).DefaultIfEmpty(None).MaxBy(badge => badge.Rank);

    /// <summary>
    /// 会话运行的程序名：已识别的 Provider（如 <c>claude</c>、<c>codex</c>），否则为命令中程序文件名去掉扩展名后的小写形式。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <returns>程序名；命令为空时为 <c>shell</c>。</returns>
    public static string Program(SessionInfo session)
    {
        if (!string.IsNullOrWhiteSpace(session.Provider)) return session.Provider;
        var command = session.Command.TrimStart();
        var quoted = command.StartsWith('"');
        var end = quoted ? command.IndexOf('"', 1) : command.IndexOfAny([' ', '\t']);
        var program = quoted ? command[1..(end < 0 ? command.Length : end)] : command[..(end < 0 ? command.Length : end)];
        // 命令可能来自其他平台（例如 Windows 路径在 Linux 上显示），两种分隔符都按目录处理。
        var name = Path.GetFileNameWithoutExtension(program.Replace('\\', '/').Split('/')[^1]);
        return name.Length > 0 ? name.ToLowerInvariant() : "shell";
    }

    /// <summary>
    /// 侧栏 Agent 行右侧的说明：<c>状态 · 程序</c>，例如 <c>blocked · claude</c>。
    /// </summary>
    /// <param name="session">会话。</param>
    /// <returns>说明文本。</returns>
    public static string Describe(SessionInfo session) => For(session).Word + " · " + Program(session);
}
