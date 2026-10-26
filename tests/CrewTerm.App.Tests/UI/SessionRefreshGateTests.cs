using CrewTerm.App.UI.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// <see cref="SessionRefreshGate"/>（以代数号阻止过期的异步刷新覆盖用户新选择的闸门）的单元测试。
/// </summary>
public sealed class SessionRefreshGateTests
{
    /// <summary>
    /// 验证：在异步请求发出前捕获代数，请求返回前用户改选了会话并调用 <c>SelectionChanged</c>，则请求返回后以旧代数调用 <c>TryApply</c> 不会把选择改回旧值；
    /// 以新捕获的代数调用 <c>TryApply</c> 则成功应用。
    /// </summary>
    [Fact]
    public async Task DelayedRefreshCannotUndoNewUserSessionSelection()
    {
        var gate = new SessionRefreshGate();
        var selected = "old-agent";
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = gate.Capture();
        // 模拟用户改选之前发出的刷新请求：等响应到达后，尝试以旧代数把选择恢复为 old-agent。
        async Task CompleteOldRequest()
        {
            await response.Task;
            gate.TryApply(version, () => selected = "old-agent");
        }
        var request = CompleteOldRequest();
        selected = "new-agent";
        gate.SelectionChanged();
        response.SetResult();
        await request;
        Assert.Equal("new-agent", selected);
        Assert.True(gate.TryApply(gate.Capture(), () => selected = "current-agent"));
        Assert.Equal("current-agent", selected);
    }
}
