using System.Drawing;
using CrewTerm.App.UI.Controls;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace CrewTerm.App.Tests.UI;

/// <summary>
/// 覆盖 <see cref="SidebarSection"/>：按显示宽度截断文本、鼠标单击新建按钮 / 占位提示 / 行、右键菜单与键盘选择。
/// 视图放在一个 30×8 的宿主中完成布局，事件以真实的视图鼠标与键盘事件投递。
/// </summary>
public sealed class SidebarSectionTests
{
    /// <summary>
    /// 验证：截断按终端列计算（中文占两列），超出时以 <c>…</c> 结尾且总宽度不超过限制；宽度足够时原样返回。
    /// </summary>
    [Fact]
    public void FitTruncatesByDisplayColumns()
    {
        Assert.Equal("CrewTerm", SidebarSection.Fit("CrewTerm", 8));
        Assert.Equal("Crew…", SidebarSection.Fit("CrewTerm", 5));
        Assert.Equal("代码…", SidebarSection.Fit("代码项目", 6));
        Assert.Equal("", SidebarSection.Fit("anything", 0));
    }

    /// <summary>
    /// 验证：没有任何行时，单击标题行右侧的 <c>+ new</c> 与单击占位提示都会请求新建，按 Enter 也会请求新建。
    /// </summary>
    [Fact]
    public void EmptySectionOffersCreationByClickAndEnter()
    {
        var (host, section) = Create();
        using var _ = host;
        var created = 0;
        section.CreateRequested = () => created++;
        Click(section, new(26, 0));
        Click(section, new(2, 1));
        section.NewKeyDownEvent(Key.Enter);
        Assert.Equal(3, created);
        Assert.Equal(-1, section.SelectedIndex);
    }

    /// <summary>
    /// 验证：左键单击一行会选中并激活该行，右键单击打开该行的菜单；方向键移动选择，Enter 激活选中行。
    /// </summary>
    [Fact]
    public void RowsActivateByClickAndKeyboardAndOpenMenusOnRightClick()
    {
        var (host, section) = Create();
        using var _ = host;
        var activated = new List<int>();
        var menus = new List<int>();
        section.RowActivated = activated.Add;
        section.ContextRequested = menus.Add;
        section.SetRows([Row("alpha"), Row("bravo"), Row("charlie")]);
        Click(section, new(5, 2));
        section.NewMouseEvent(new Mouse { View = section, Position = new(5, 3), ScreenPosition = new(5, 3), Flags = MouseFlags.RightButtonPressed });
        section.NewKeyDownEvent(Key.CursorUp);
        section.NewKeyDownEvent(Key.Enter);
        Assert.Equal([1, 1], activated);
        Assert.Equal([2], menus);
        Assert.Equal(1, section.SelectedIndex);
    }

    /// <summary>
    /// 验证：替换行时不指定选择会尽量保持原索引，超出新行数时取最后一行；指定选择时以指定为准。
    /// </summary>
    [Fact]
    public void SetRowsKeepsOrClampsTheSelection()
    {
        var (host, section) = Create();
        using var _ = host;
        section.SetRows([Row("a"), Row("b"), Row("c")], select: 2);
        section.SetRows([Row("a"), Row("b")]);
        Assert.Equal(1, section.SelectedIndex);
        section.SetRows([Row("x"), Row("y"), Row("z")], select: 0);
        Assert.Equal(0, section.SelectedIndex);
    }

    /// <summary>
    /// 在 30×8 的宿主中创建并布局一个侧栏分区。
    /// </summary>
    /// <returns>宿主（调用方负责释放）与分区。</returns>
    private static (View Host, SidebarSection Section) Create()
    {
        var host = new View { Width = 30, Height = 8, CanFocus = true };
        var section = new SidebarSection("spaces", "add a project") { Width = 30, Height = 8 };
        host.Add(section);
        host.Layout(new Size(30, 8));
        return (host, section);
    }

    /// <summary>
    /// 在分区的视口坐标处投递一次左键按下。
    /// </summary>
    /// <param name="section">分区。</param>
    /// <param name="point">视口坐标。</param>
    private static void Click(SidebarSection section, Point point) =>
        section.NewMouseEvent(new Mouse { View = section, Position = point, ScreenPosition = point, Flags = MouseFlags.LeftButtonPressed });

    /// <summary>
    /// 创建一个测试行。
    /// </summary>
    /// <param name="title">名称。</param>
    /// <returns>侧栏行。</returns>
    private static SidebarRow Row(string title) => new("○", 0x9399b2, title, "main", false);
}
