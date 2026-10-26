using CrewTerm.App.UI.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 覆盖 <see cref="PrefixKeyState"/>：<c>Ctrl+b</c> 之后的命令键、连按两次、Esc 取消、超时，以及支持 kitty 键盘协议的终端单独报告的修饰键与重复、释放事件。
/// </summary>
public sealed class PrefixKeyStateTests
{
    /// <summary>
    /// 测试使用的固定起始时间。
    /// </summary>
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 验证：没有前缀时普通按键与前缀无关；按下 <c>Ctrl+b</c> 后下一个字符键成为命令（区分大小写），命令之后前缀结束。
    /// </summary>
    [Fact]
    public void CharacterAfterPrefixIsACommandAndEndsThePrefix()
    {
        var state = new PrefixKeyState();
        Assert.Equal((PrefixKeyOutcome.NotPrefix, 0), state.Process(Key.V, Start));
        Assert.Equal((PrefixKeyOutcome.Started, 0), state.Process(Key.B.WithCtrl, Start));
        Assert.True(state.IsPending(Start));
        Assert.Equal((PrefixKeyOutcome.Command, (int)'v'), state.Process(Key.V, Start.AddMilliseconds(300)));
        Assert.False(state.IsPending(Start.AddMilliseconds(300)));
        Assert.Equal((PrefixKeyOutcome.NotPrefix, 0), state.Process(Key.X, Start.AddMilliseconds(400)));
    }

    /// <summary>
    /// 验证：kitty 键盘协议下 <c>Ctrl+b</c> 之后单独报告的 Shift 按下、按键重复与释放事件都被忽略，随后的 Shift+N 得到大写 <c>N</c>；
    /// 两次 <c>Ctrl+b</c> 之间单独报告的 Ctrl 按下同样被忽略，结果是把 <c>Ctrl+b</c> 本身发给终端。
    /// </summary>
    [Fact]
    public void ModifierOnlyRepeatAndReleaseEventsDoNotConsumeThePrefix()
    {
        var state = new PrefixKeyState();
        state.Process(Key.B.WithCtrl, Start);
        Assert.Equal(PrefixKeyOutcome.Ignored, state.Process(new Key(Key.B.WithCtrl) { EventType = KeyEventType.Release }, Start).Outcome);
        Assert.Equal(PrefixKeyOutcome.Ignored, state.Process(new Key(KeyCode.ShiftMask) { ModifierKey = ModifierKey.LeftShift }, Start).Outcome);
        Assert.Equal(PrefixKeyOutcome.Ignored, state.Process(new Key(Key.B.WithCtrl) { EventType = KeyEventType.Repeat }, Start).Outcome);
        Assert.Equal((PrefixKeyOutcome.Command, (int)'N'), state.Process(Key.N.WithShift, Start.AddMilliseconds(100)));

        state.Process(Key.B.WithCtrl, Start);
        Assert.Equal(PrefixKeyOutcome.Ignored, state.Process(new Key(KeyCode.CtrlMask) { ModifierKey = ModifierKey.LeftCtrl }, Start).Outcome);
        Assert.Equal(PrefixKeyOutcome.SendPrefix, state.Process(Key.B.WithCtrl, Start.AddMilliseconds(100)).Outcome);
        Assert.False(state.IsPending(Start.AddMilliseconds(100)));
    }

    /// <summary>
    /// 验证：前缀在 <see cref="PrefixKeyState.Timeout"/> 后过期，过期后键入的 <c>x</c> 只是普通按键（不会关闭面板）；
    /// Esc 取消前缀；功能键等不对应字符的键消耗前缀但不产生命令；<see cref="PrefixKeyState.Cancel"/> 立即取消。
    /// </summary>
    [Fact]
    public void PrefixExpiresAndCanBeCancelled()
    {
        var state = new PrefixKeyState();
        state.Process(Key.B.WithCtrl, Start);
        Assert.False(state.IsPending(Start + PrefixKeyState.Timeout));
        Assert.Equal((PrefixKeyOutcome.NotPrefix, 0), state.Process(Key.X, Start + PrefixKeyState.Timeout));

        state.Process(Key.B.WithCtrl, Start);
        Assert.Equal(PrefixKeyOutcome.Cancelled, state.Process(Key.Esc, Start).Outcome);
        Assert.Equal(PrefixKeyOutcome.NotPrefix, state.Process(Key.X, Start).Outcome);

        state.Process(Key.B.WithCtrl, Start);
        Assert.Equal(PrefixKeyOutcome.Unknown, state.Process(Key.F5, Start).Outcome);
        Assert.Equal(PrefixKeyOutcome.NotPrefix, state.Process(Key.X, Start).Outcome);

        state.Process(Key.B.WithCtrl, Start);
        state.Cancel();
        Assert.Equal(PrefixKeyOutcome.NotPrefix, state.Process(Key.X, Start).Outcome);
    }
}
