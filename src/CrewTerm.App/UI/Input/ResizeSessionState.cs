namespace CrewTerm.App.UI.Input;

/// <summary>
/// <see cref="TerminalResizeCoordinator"/> 中单个会话的 resize 状态，全部属性由 <see cref="Gate"/> 保护。
/// </summary>
internal sealed class ResizeSessionState
{
    /// <summary>
    /// 保护本对象其余属性的锁对象，只用于 <see langword="lock"/>。
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// 最近一次请求的目标尺寸；在途请求完成时若它已变化，会再发送一次。
    /// </summary>
    public TerminalSize Latest { get; set; }

    /// <summary>
    /// daemon 已确认的尺寸；最近一次发送失败时为 <see langword="null"/>（远端尺寸未知）。
    /// </summary>
    public TerminalSize? Confirmed { get; set; }

    /// <summary>
    /// 当前批次的完成源；没有在途请求时为 <see langword="null"/>。
    /// </summary>
    public TaskCompletionSource? Completion { get; set; }
}
