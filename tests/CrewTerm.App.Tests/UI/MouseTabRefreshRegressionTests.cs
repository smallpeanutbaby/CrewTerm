using System.Drawing;
using System.Reflection;
using CrewTerm.App.UI.Controls;
using CrewTerm.App.UI.Input;
using CrewTerm.App.UI.Windows;
using CrewTerm.Core.Layouts;
using CrewTerm.Core.Settings;
using CrewTerm.Terminal.Emulation;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 鼠标切换标签页与延迟屏幕刷新之间竞争的回归测试：在内存中创建真实的 <see cref="WorkspaceWindow"/>（不连接 daemon、不启动终端），
/// 通过反射取得它内部实际使用的 <see cref="SessionRefreshGate"/>，直接驱动标签栏的鼠标事件。
/// </summary>
public sealed class MouseTabRefreshRegressionTests
{
    /// <summary>
    /// 验证：预先保存含两个标签页的布局（活动标签绑定 <c>old-session</c>），用鼠标点击第二个标签切换后，切换前捕获的刷新代数随即失效：
    /// 旧面板延迟到达的屏幕更新被拒绝（旧面板快照保持不变），而用切换后重新捕获的代数仍能把屏幕应用到当前活动面板。
    /// </summary>
    [Fact]
    public void MouseTabSwitchRejectsThePreviousPanesDelayedRefresh()
    {
        var state = Path.Combine(Path.GetTempPath(), "CrewTerm mouse tab refresh " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            var layout = new TerminalLayout();
            var first = layout.AddTab("First", "old-session");
            var second = layout.AddTab("Second", "new-session");
            layout.ActiveTabId = first.Id;
            var store = new WorkspaceSettingsStore(state);
            var settings = store.Load();
            settings.ProjectLayouts["_none"] = layout;
            store.Save(settings);

            using var app = Application.Create();
            using var window = new WorkspaceWindow(app, CrewTermCommandLine.Parse(["--state-dir", state]));
            window.BeginInit(); window.EndInit(); window.Layout(new Size(120, 40));
            var view = window.SubViews.OfType<TerminalLayoutView>().Single();
            Assert.Equal("old-session", view.ActivePane.SessionId);
            var oldPane = view.ActivePane;
            var before = new TerminalScreen(10, 3).Snapshot();
            oldPane.Update(before);

            // 直接使用 WorkspaceWindow 实际持有并接线的刷新闸门（经反射读取私有字段 refreshGate），
            // 不为测试向产品代码引入额外的测试 API 或替换回调。
            var gate = Assert.IsType<SessionRefreshGate>(typeof(WorkspaceWindow)
                .GetField("refreshGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            var oldGeneration = gate.Capture();
            var tab = view.SubViews.First().SubViews.Single(child => Equals(child.Data, second.Id));
            tab.NewMouseEvent(new Mouse { Position = new(1, 0), Flags = MouseFlags.LeftButtonPressed });
            Assert.Equal("new-session", view.ActivePane.SessionId);

            var delayedScreen = new TerminalScreen(10, 3);
            delayedScreen.Feed("old reply");
            var applied = gate.TryApply(oldGeneration, () => oldPane.Update(delayedScreen.Snapshot()));
            Assert.False(applied);
            Assert.Same(before, oldPane.Snapshot);

            var currentScreen = new TerminalScreen(10, 3).Snapshot();
            Assert.True(gate.TryApply(gate.Capture(), () => view.ActivePane.Update(currentScreen)));
            Assert.Same(currentScreen, view.ActivePane.Snapshot);
        }
        finally { Directory.Delete(state, true); }
    }
}
