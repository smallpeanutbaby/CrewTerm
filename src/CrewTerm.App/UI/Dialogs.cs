using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CodeEditor = Terminal.Gui.Editor.Editor;

namespace CrewTerm.App.UI;

internal static class Dialogs
{
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

    public static int Choose(IApplication app, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { MessageBox.Query(app, title, "No entries.", ["OK"]); return -1; }
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(88), Height = Dim.Percent(80) };
        var filter = new TextField { X = 1, Y = 0, Width = Dim.Fill(1), Text = "" };
        var list = new ListView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2) };
        var indices = Enumerable.Range(0, items.Count).ToArray();
        void Refresh()
        {
            indices = Enumerable.Range(0, items.Count).Where(i => items[i].Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new ObservableCollection<string>(indices.Select(i => items[i])));
            if (indices.Length > 0) list.SelectedItem = 0;
        }
        filter.TextChanged += (_, _) => Refresh();
        var result = -1;
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
        return dialog.Result == 0 && list.SelectedItem is int index && index < indices.Length ? indices[index] : -1;
    }

    public static void Text(IApplication app, string title, string text)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(95), Height = Dim.Percent(92) };
        var editor = new CodeEditor { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true,
            Document = new TextDocument(text), WordWrap = false, ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        dialog.Add(editor);
        dialog.AddButton(new Button { Text = "Close" });
        editor.SetFocus();
        app.Run(dialog);
    }

    public static bool Confirm(IApplication app, string title, string text) =>
        MessageBox.Query(app, title, text, ["Cancel", "Confirm"]) == 1;
}
