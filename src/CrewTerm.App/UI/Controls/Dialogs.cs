using System.Collections.ObjectModel;
using CrewTerm.Core.Security;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CodeEditor = Terminal.Gui.Editor.Editor;

namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 工作台使用的模态对话框：输入、可过滤列表选择、只读文本查看与确认。
/// </summary>
/// <remarks>
/// 所有方法都在 UI 线程同步运行嵌套的消息循环，直到对话框关闭才返回。
/// </remarks>
internal static class Dialogs
{
    /// <summary>
    /// 显示单行输入框。在输入框内按 Enter 等同于点击 OK。
    /// </summary>
    /// <param name="app">Terminal.Gui 应用实例。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="label">输入框上方的说明。</param>
    /// <param name="initial">初始文本。</param>
    /// <returns>确认时返回输入文本（可能为空字符串）；取消时返回 <see langword="null"/>。</returns>
    public static string? Prompt(IApplication app, string title, string label, string initial = "")
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = 9 };
        var field = new TextField { X = 1, Y = 2, Width = Dim.Fill(1), Text = initial };
        dialog.Add(new Label { X = 1, Text = label, Width = Dim.Fill(1) }, field);
        var accept = new Button { Text = "_OK" };
        dialog.AddButton(accept);
        dialog.AddButton(new Button { Text = "_Cancel" });
        dialog.DefaultAcceptView = accept;
        var acceptedFromField = false;
        // TextField 默认会吞掉 Enter，这里显式把它当作“确认”。
        field.KeyDown += (_, key) =>
        {
            if (key != global::Terminal.Gui.Input.Key.Enter) return;
            key.Handled = true;
            acceptedFromField = true;
            dialog.Result = 0;
            app.RequestStop(dialog);
        };
        field.SetFocus();
        app.Run(dialog);
        return acceptedFromField || dialog.Result == 0 ? field.Text : null;
    }

    /// <summary>
    /// 显示带过滤框的列表。直接输入文字过滤，↓ 进入列表，Enter 选择。
    /// </summary>
    /// <remarks>
    /// 过滤为不区分大小写的子串匹配，保持原有顺序，每次过滤后默认选中第一项。
    /// 列表为空时只弹出 “No entries.” 提示框，不显示选择对话框。
    /// </remarks>
    /// <param name="app">Terminal.Gui 应用实例。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="items">候选项文本。</param>
    /// <returns>所选项在 <paramref name="items"/> 中的原始索引；取消或列表为空时为 -1。</returns>
    public static int Choose(IApplication app, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { MessageBox.Query(app, title, "No entries.", ["OK"]); return -1; }
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(88), Height = Dim.Percent(80) };
        var filter = new TextField { X = 1, Y = 0, Width = Dim.Fill(1), Text = "" };
        var list = new ListView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2) };
        // 过滤后列表行号 → 原始索引的映射。
        var indices = Enumerable.Range(0, items.Count).ToArray();
        // 按过滤框内容重建列表，并默认选中第一项。
        void Refresh()
        {
            indices = Enumerable.Range(0, items.Count).Where(i => items[i].Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            // 候选项可能来自会话名、消息或插件清单等外部文本，显示前清理控制字符。
            list.SetSource(new ObservableCollection<string>(indices.Select(i => DisplayText.Sanitize(items[i]))));
            if (indices.Length > 0) list.SelectedItem = 0;
        }
        filter.TextChanged += (_, _) => Refresh();
        var result = -1;
        // 记录当前选中项并关闭对话框。
        void SelectCurrent()
        {
            if (list.SelectedItem is int selected && selected < indices.Length)
            { result = indices[selected]; app.RequestStop(dialog); }
        }
        filter.KeyDown += (_, key) =>
        {
            if (key == global::Terminal.Gui.Input.Key.Enter) { key.Handled = true; SelectCurrent(); }
            else if (key == global::Terminal.Gui.Input.Key.CursorDown) { key.Handled = true; list.SetFocus(); }
        };
        list.Accepting += (_, args) =>
        {
            args.Handled = true;
            SelectCurrent();
        };
        dialog.Add(filter, list);
        dialog.AddButton(new Button { Text = "Select" });
        dialog.AddButton(new Button { Text = "Cancel" });
        Refresh();
        filter.SetFocus();
        app.Run(dialog);
        if (result >= 0) return result;
        // 点击 “Select” 按钮（Result == 0）时取列表当前项。
        return dialog.Result == 0 && list.SelectedItem is int index && index < indices.Length ? indices[index] : -1;
    }

    /// <summary>
    /// 以只读代码编辑器显示长文本（diff、滚动历史、帮助等），支持滚动与 Ctrl+F 查找。
    /// </summary>
    /// <remarks>
    /// 文本可能来自命令或插件输出，显示前经 <see cref="DisplayText.Sanitize"/> 去掉 ANSI 序列与控制字符。
    /// </remarks>
    /// <param name="app">Terminal.Gui 应用实例。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="text">要显示的文本。</param>
    public static void Text(IApplication app, string title, string text)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(95), Height = Dim.Percent(92) };
        var editor = new CodeEditor { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true,
            Document = new TextDocument(DisplayText.Sanitize(text)), WordWrap = false, ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        // 编辑器控件对 Ctrl+F 只发出 FindRequested 事件，由使用方提供查找框。
        editor.FindRequested += (_, _) => Find(app, editor, "Find in text");
        dialog.Add(editor);
        dialog.AddButton(new Button { Text = "Close" });
        editor.SetFocus();
        app.Run(dialog);
    }

    /// <summary>
    /// 询问要查找的文本，在编辑器中从光标处向后查找下一处（不区分大小写）并选中，然后把焦点还给编辑器。
    /// </summary>
    /// <remarks>
    /// 只读文本查看器与工作台的代码编辑器在收到 Ctrl+F（<see cref="CodeEditor.FindRequested"/>）时共用本方法。
    /// 输入为空或取消时什么也不做。
    /// </remarks>
    /// <param name="app">Terminal.Gui 应用实例。</param>
    /// <param name="editor">要在其中查找的编辑器控件。</param>
    /// <param name="title">查找框的标题。</param>
    public static void Find(IApplication app, CodeEditor editor, string title)
    {
        var query = Prompt(app, title, "Text");
        if (!string.IsNullOrEmpty(query)) editor.FindNext(query, false, false);
        editor.SetFocus();
    }

    /// <summary>
    /// 显示确认框；默认按钮是 Cancel，防止误按 Enter 执行危险操作。
    /// </summary>
    /// <remarks>
    /// <see cref="MessageBox"/> 总是把最后一个按钮作为默认按钮（直接按 Enter 时触发），因此 Cancel 必须放在最后。
    /// 确认需要明确的动作：按 Alt+O 或点击 Confirm；直接按 Enter、Esc 或 Alt+C 都是取消。
    /// Alt+C 与 <c>Unsaved changes</c> 对话框中的含义相同（取消），避免习惯性按键误执行危险操作。
    /// </remarks>
    /// <param name="app">Terminal.Gui 应用实例。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="text">说明文字。</param>
    /// <returns>用户选择 Confirm 时为 <see langword="true"/>。</returns>
    public static bool Confirm(IApplication app, string title, string text) =>
        MessageBox.Query(app, title, text, ["C_onfirm", "_Cancel"]) == 0;
}
