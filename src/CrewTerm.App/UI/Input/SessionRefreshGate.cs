namespace CrewTerm.App.UI.Input;

/// <summary>
/// 会话刷新的版本闸门（在 UI 线程使用）：用户改变选择之前发起的刷新结果不能覆盖新的选择。
/// </summary>
/// <remarks>
/// 后台刷新开始前调用 <see cref="Capture"/> 记下版本号；用户切换会话时调用 <see cref="SelectionChanged"/>
/// 使版本号递增。刷新结果回到 UI 线程后通过 <see cref="TryApply"/> 应用，版本已过期则丢弃，
/// 避免慢响应把界面切回用户刚离开的会话。
/// </remarks>
public sealed class SessionRefreshGate
{
    /// <summary>
    /// 当前的选择版本号，从 0 开始，每次 <see cref="SelectionChanged"/> 加一。
    /// </summary>
    /// <remarks>
    /// 通过 <see cref="Interlocked"/> 读写，即使在其他线程调用 <see cref="Capture"/> 也能读到完整的 64 位值。
    /// </remarks>
    private long version;

    /// <summary>
    /// 记录当前选择版本，供刷新结果返回时与最新版本比较。
    /// </summary>
    /// <returns>当前版本号。</returns>
    public long Capture() => Interlocked.Read(ref version);

    /// <summary>
    /// 用户改变了选择，使之前捕获的版本全部失效。
    /// </summary>
    public void SelectionChanged() => Interlocked.Increment(ref version);

    /// <summary>
    /// 仅当捕获的版本仍是最新时同步执行更新；版本已过期则什么也不做。
    /// </summary>
    /// <remarks>
    /// 应在 UI 线程调用：版本比较与 <paramref name="update"/> 之间没有加锁，
    /// 只有在 <see cref="SelectionChanged"/> 也只在 UI 线程调用时，两者之间才不会插入新的选择变化。
    /// <paramref name="update"/> 抛出的异常会原样传给调用方。
    /// </remarks>
    /// <param name="capturedVersion">刷新开始时 <see cref="Capture"/> 返回的版本。</param>
    /// <param name="update">要执行的界面更新。</param>
    /// <returns>执行了更新时为 <see langword="true"/>；版本已过期、更新被丢弃时为 <see langword="false"/>。</returns>
    public bool TryApply(long capturedVersion, Action update)
    {
        if (capturedVersion != Capture()) return false;
        update();
        return true;
    }
}
