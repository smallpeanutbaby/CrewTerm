using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Input;

/// <summary>
/// herdr / tmux 风格前缀键（<c>Ctrl+b</c>）的状态机：判断一次按键是开始前缀、前缀之后的命令，还是与前缀无关。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>前缀在 <see cref="Timeout"/> 内有效，过期后自动取消，避免一次误按的 <c>Ctrl+b</c> 让很久之后键入终端的 <c>x</c>、<c>q</c> 变成关闭面板或分离。</description></item>
/// <item><description>等待前缀之后的键时忽略只有修饰键的按下（支持 kitty 键盘协议的终端会单独报告 Shift、Ctrl 等）、
/// 按键重复与释放事件，因此 <c>Ctrl+b</c> 之后按 Shift+N 能得到 <c>N</c>。</description></item>
/// </list>
/// 本类不依赖界面，时间由调用方传入，便于测试。
/// </remarks>
public sealed class PrefixKeyState
{
    /// <summary>
    /// 前缀键按下后等待下一个键的最长时间。
    /// </summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 前缀键按下的时间；没有等待中的前缀时为 <see langword="null"/>。
    /// </summary>
    private DateTime? pendingSince;

    /// <summary>
    /// 在指定时刻是否正在等待前缀之后的键。
    /// </summary>
    /// <param name="now">当前时间。</param>
    /// <returns>前缀已按下且尚未过期时为 <see langword="true"/>。</returns>
    public bool IsPending(DateTime now) => pendingSince is { } since && now - since < Timeout;

    /// <summary>
    /// 处理一次按键。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>
    /// <c>Outcome</c> 为处理结果；为 <see cref="PrefixKeyOutcome.Command"/> 时 <c>Character</c> 是前缀之后按键对应的字符（区分大小写），否则为 0。
    /// </returns>
    public (PrefixKeyOutcome Outcome, int Character) Process(Key key, DateTime now)
    {
        if (!IsPending(now))
        {
            pendingSince = null;
            if (key == Key.B.WithCtrl && key.EventType != KeyEventType.Release)
            {
                pendingSince = now;
                return (PrefixKeyOutcome.Started, 0);
            }
            return (PrefixKeyOutcome.NotPrefix, 0);
        }
        if (key.EventType != KeyEventType.Press || key.IsModifierOnly || key.KeyCode == KeyCode.Null) return (PrefixKeyOutcome.Ignored, 0);
        pendingSince = null;
        if (key == Key.Esc) return (PrefixKeyOutcome.Cancelled, 0);
        if (key == Key.B.WithCtrl) return (PrefixKeyOutcome.SendPrefix, 0);
        var character = key.AsRune.Value;
        return character == 0 ? (PrefixKeyOutcome.Unknown, 0) : (PrefixKeyOutcome.Command, character);
    }

    /// <summary>
    /// 取消等待中的前缀（例如用户改用鼠标或打开了对话框）。
    /// </summary>
    public void Cancel() => pendingSince = null;
}
