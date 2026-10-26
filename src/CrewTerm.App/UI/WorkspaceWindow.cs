using System.Collections.ObjectModel;
using CrewTerm.App.Server;
using CrewTerm.Automation;
using CrewTerm.Core;
using CrewTerm.Workspace;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CrewTerm.App.UI;

public sealed partial class WorkspaceWindow : Window
{
    private readonly IApplication application;
    private readonly string stateDirectory;
    private readonly DaemonClient client;
    private readonly ProjectCatalog projects;
    private readonly WorkspaceSettingsStore settingsStore;
    private readonly WorkspaceSettings settings;
    private readonly AgentCatalog agents;
    private readonly GitService git = new();
    private readonly CheckpointStore checkpoints;
    private readonly EditorBinding editor = new();
    private readonly ListView projectList = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ListView sessionList = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ListView fileList = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly TerminalPane primary = new();
    private readonly TerminalPane secondary = new();
    private readonly FrameView terminalFrame = new() { Title = "TERMINAL" };
    private readonly FrameView secondFrame = new() { Title = "TERMINAL 2" };
    private readonly FrameView filesFrame = new() { Title = "FILES" };
    private readonly FrameView editorFrame = new() { Title = "CODE  ·  F5 Save" };
    private readonly Label status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SessionInputDispatcher inputDispatcher;
    public bool InputDrained { get; private set; } = true;
    private readonly object? refreshTimer;
    private bool polling, updatingLists, filesVisible = true, detached, exitApproved;
    private string? projectRoot;
    private string relativeDirectory = "";
    private FileEntry[] files = [];
    private SessionInfo[] sessions = [];
    private string sessionSignature = "";
    private readonly TerminalResizeCoordinator resizeCoordinator;
    private readonly SessionRefreshGate refreshGate = new();

    public WorkspaceWindow(IApplication application, CrewTermCommandLine options)
    {
        this.application = application;
        stateDirectory = options.StateDirectory;
        client = new(stateDirectory);
        resizeCoordinator = new(async (id, columns, rows) =>
        {
            var response = await client.CallAsync(new("resize", id, Columns: columns, Rows: rows), lifetime.Token);
            if (!response.Ok) throw new IOException(response.Error ?? "Resize failed");
        });
        projects = new(stateDirectory);
        settingsStore = new(stateDirectory);
        settings = settingsStore.Load();
        agents = new(stateDirectory);
        checkpoints = new(stateDirectory);
        Title = "CrewTerm  ·  MULTI-AGENT WORKSPACE";
        Width = Dim.Fill(); Height = Dim.Fill();
        SetScheme(new Scheme
        {
            Normal = new(new Color(0xc6d1df), new Color(0x10151d)),
            Focus = new(new Color(0xffffff), new Color(0x25425c)),
            HotNormal = new(new Color(0x68dbbf), new Color(0x10151d))
        });
        var spaces = new FrameView { Title = "SPACES  ·  F2", Width = 28, Height = Dim.Percent(32) };
        var crew = new FrameView { Title = "AGENTS  ·  F3", Width = 28, Y = Pos.Bottom(spaces), Height = Dim.Fill(2) };
        spaces.Add(projectList); crew.Add(sessionList);
        terminalFrame.X = 28; terminalFrame.Y = 0;
        terminalFrame.Add(primary); secondFrame.Add(secondary);
        filesFrame.Add(fileList); editorFrame.Add(editor.View);
        status.Y = Pos.AnchorEnd(2);
        var shortcuts = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
            Text = "F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git  F7 Tasks  F8 Split  F9 Commands  F10 Detach" };
        Add(spaces, crew, terminalFrame, secondFrame, filesFrame, editorFrame, status, shortcuts);
        ArrangePanels();
        projectList.Accepting += (_, e) => { e.Handled = true; Guard(SelectProject); };
        sessionList.Accepting += (_, e) => { e.Handled = true; Guard(SelectSession); };
        fileList.Accepting += (_, e) => { e.Handled = true; Guard(OpenFileEntry); };
        primary.SendInput = text => QueueInput(primary, text);
        secondary.SendInput = text => QueueInput(secondary, text);
        application.Keyboard.KeyDown += GlobalKey;
        IsRunningChanging += (_, args) =>
        {
            if (args.NewValue || exitApproved) return;
            args.Cancel = true;
            Guard(() =>
            {
                if (!CanLeaveEditor()) return;
                if (!SaveLayoutForExit()) return;
                exitApproved = true;
                args.Cancel = false;
            });
        };
        editor.View.ModifiedChanged += (_, _) => UpdateEditorTitle();
        if (options.Get("project") is string selected)
        {
            var project = projects.Add(selected); projects.Save(); settings.LastProjectId = project.Id;
        }
        var initial = projects.Projects.FirstOrDefault(p => p.Id == settings.LastProjectId) ?? projects.Projects.FirstOrDefault();
        if (initial is not null) projectRoot = initial.Path;
        RefreshProjects(); RefreshFiles();
        primary.SessionId = options.Get("project") is null ? settings.LastSessionId : null;
        inputDispatcher = new(async (id, text, cancellation) =>
        {
            var response = await client.CallAsync(new("input", id, Text: text), cancellation);
            if (!response.Ok) throw new IOException(response.Error ?? "Input failed");
        }, error => { if (!detached) application.Invoke(() => status.Text = "Input: " + error.Message); });
        refreshTimer = application.AddTimeout(TimeSpan.FromMilliseconds(160), () => { BeginRefresh(); return !detached; });
        status.Text = projectRoot ?? "F2 Add a project, then F3 start an agent. F9 lists all commands.";
        primary.SetFocus();
    }

    private void ArrangePanels()
    {
        terminalFrame.Width = settings.SplitTerminals ? Dim.Percent(50, DimPercentMode.Position) : Dim.Fill();
        terminalFrame.Height = filesVisible ? Dim.Percent(53) : Dim.Fill(2);
        secondFrame.X = Pos.Right(terminalFrame); secondFrame.Y = 0;
        secondFrame.Width = Dim.Fill(); secondFrame.Height = terminalFrame.Height;
        secondFrame.Visible = settings.SplitTerminals;
        filesFrame.X = 28; filesFrame.Y = Pos.Bottom(terminalFrame);
        filesFrame.Width = 30; filesFrame.Height = Dim.Fill(2); filesFrame.Visible = filesVisible;
        editorFrame.X = Pos.Right(filesFrame); editorFrame.Y = filesFrame.Y;
        editorFrame.Width = Dim.Fill(); editorFrame.Height = Dim.Fill(2); editorFrame.Visible = filesVisible;
    }

    private void GlobalKey(object? sender, Key key)
    {
        if (application.TopRunnable != this || detached) return;
        Action? action = key.KeyCode switch
        {
            KeyCode.F1 => ShowHelp,
            KeyCode.F2 => AddProject,
            KeyCode.F3 => NewAgent,
            KeyCode.F4 => ToggleFiles,
            KeyCode.F5 => SaveFile,
            KeyCode.F6 => GitMenu,
            KeyCode.F7 => TaskMenu,
            KeyCode.F8 => ToggleSplit,
            KeyCode.F9 => Palette,
            KeyCode.F10 => Detach,
            _ => null
        };
        if (key == Key.P.WithCtrl.WithShift) action = Palette;
        if (key == Key.V.WithCtrl.WithShift) action = Paste;
        if (key == Key.C.WithCtrl.WithShift) action = CopyScreen;
        if (key == Key.D1.WithAlt) action = () => primary.SetFocus();
        if (key == Key.D2.WithAlt) action = () => { if (settings.SplitTerminals) secondary.SetFocus(); };
        if (key == Key.D3.WithAlt) action = () => { ShowFiles(); editor.View.SetFocus(); };
        if (key == Key.D4.WithAlt) action = () => { ShowFiles(); fileList.SetFocus(); };
        if (key == Key.D5.WithAlt) action = () => sessionList.SetFocus();
        if (action is not null) { key.Handled = true; Guard(action); return; }
        // The terminal owns Ctrl+C, Ctrl+Q, Esc and Tab while it has focus.
        var terminal = secondary.HasFocus ? secondary : primary.HasFocus ? primary : null;
        if (terminal is not null && TerminalInput.Encode(key, terminal.Snapshot?.ApplicationCursorKeys ?? false) is string text)
        { key.Handled = true; QueueInput(terminal, text); }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { MessageBox.Query(application, "CrewTerm", error.Message, ["OK"]); }
    }

    private void BeginRefresh()
    {
        if (polling || detached) return;
        polling = true;
        var generation = refreshGate.Capture();
        var selectedRoot = projectRoot;
        var selectedPrimary = primary.SessionId;
        var selectedSecondary = secondary.SessionId;
        var split = settings.SplitTerminals;
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await client.CallAsync(new("list"), lifetime.Token);
                var fresh = list.Sessions ?? [];
                var a = selectedPrimary;
                if (a is null || !fresh.Any(s => s.Id == a))
                    a = fresh.FirstOrDefault(s => selectedRoot is null || string.Equals(s.WorkingDirectory, selectedRoot, StringComparison.OrdinalIgnoreCase))?.Id;
                var b = selectedSecondary;
                if (split && (b is null || b == a || !fresh.Any(s => s.Id == b)))
                    b = fresh.FirstOrDefault(s => s.Id != a)?.Id;
                if (!split) b = null;
                var screenA = a is null ? null : (await client.CallAsync(new("screen", a), lifetime.Token)).Screen;
                var screenB = b is null ? null : (await client.CallAsync(new("screen", b), lifetime.Token)).Screen;
                if (!detached) application.Invoke(() => refreshGate.TryApply(generation, () =>
                {
                    if (detached) return;
                    sessions = fresh;
                    RefreshSessionList();
                    primary.SessionId = a; primary.Update(screenA);
                    secondary.SessionId = b; secondary.Update(screenB);
                    terminalFrame.Title = SessionTitle(primary);
                    secondFrame.Title = SessionTitle(secondary);
                    ResizePane(primary);
                    if (settings.SplitTerminals) ResizePane(secondary);
                }));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!detached) application.Invoke(() => status.Text = "Server: " + error.Message); }
            finally { polling = false; }
        });
    }

    private string SessionTitle(TerminalPane pane)
    {
        var session = sessions.FirstOrDefault(s => s.Id == pane.SessionId);
        return session is null ? "TERMINAL  ·  F3 New" : $"{session.Name}  ·  {(session.HasExited ? "EXIT " + session.ExitCode : "LIVE")}  ·  {session.Id[..6]}";
    }

    private void ResizePane(TerminalPane pane)
    {
        if (pane.SessionId is not string id || pane.Viewport.Width < 2 || pane.Viewport.Height < 2) return;
        var size = (Columns: Math.Clamp(pane.Viewport.Width, 2, 300), Rows: Math.Clamp(pane.Viewport.Height, 2, 120));
        var resize = resizeCoordinator.RequestAsync(id, size.Columns, size.Rows);
        _ = Task.Run(async () =>
        {
            try { await resize; }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
        });
    }

    private void QueueInput(TerminalPane pane, string text)
    {
        if (pane.SessionId is null) { status.Text = "F3 starts a terminal."; return; }
        if (!inputDispatcher.TryWrite(pane.SessionId, text)) status.Text = "Input queue full; input was not sent. Wait and retry.";
    }

    private void RefreshProjects()
    {
        projectList.SetSource(new ObservableCollection<string>(projects.Projects.Select(p => (p.Path == projectRoot ? "● " : "  ") + p.Name)));
        if (projects.Projects.Count > 0) projectList.SelectedItem = Math.Max(0, projects.Projects.ToList().FindIndex(p => p.Path == projectRoot));
    }

    private void RefreshSessionList()
    {
        var signature = string.Join('|', sessions.Select(s => $"{s.Id}:{s.HasExited}:{s.ExitCode}"));
        if (sessionSignature == signature) return;
        sessionSignature = signature;
        updatingLists = true;
        var selected = sessionList.SelectedItem;
        sessionList.SetSource(new ObservableCollection<string>(sessions.Select(s => $"{(s.HasExited ? "○" : "●")} {s.Name} [{s.Id[..4]}]")));
        if (sessions.Length > 0) sessionList.SelectedItem = Math.Clamp(selected ?? 0, 0, sessions.Length - 1);
        updatingLists = false;
    }

    private void RefreshFiles()
    {
        files = projectRoot is null ? [] : FileWorkspace.ListDirectory(projectRoot, relativeDirectory).ToArray();
        var labels = new List<string>();
        if (relativeDirectory.Length > 0) labels.Add("../");
        labels.AddRange(files.Select(f => (f.IsDirectory ? "▸ " : "  ") + f.Name));
        fileList.SetSource(new ObservableCollection<string>(labels));
        if (labels.Count > 0) fileList.SelectedItem = 0;
        filesFrame.Title = "FILES  ·  " + (relativeDirectory.Length == 0 ? "/" : relativeDirectory);
    }

    private void UpdateEditorTitle() => editorFrame.Title = (editor.IsDirty ? "* " : "") +
        (editor.Path is null ? "CODE  ·  Enter a file to edit" : Path.GetFileName(editor.Path)) + "  ·  F5 Save";

    private bool CanLeaveEditor()
    {
        if (!editor.IsDirty) return true;
        var answer = MessageBox.Query(application, "Unsaved changes", "Save edits before leaving this file?", ["_Save", "_Discard", "_Cancel"]);
        if (answer == 0) { editor.Save(); return true; }
        return answer == 1;
    }

    private void SwitchProject(ProjectEntry project)
    {
        if (!CanLeaveEditor()) return;
        refreshGate.SelectionChanged();
        editor.Close(true); projectRoot = project.Path; relativeDirectory = "";
        settings.LastProjectId = project.Id;
        primary.SessionId = sessions.FirstOrDefault(s => string.Equals(s.WorkingDirectory, projectRoot, StringComparison.OrdinalIgnoreCase))?.Id;
        primary.Update(null); RefreshProjects(); RefreshFiles(); UpdateEditorTitle();
        status.Text = projectRoot;
    }

    private void SelectProject()
    {
        if (projectList.SelectedItem is int index && index < projects.Projects.Count) SwitchProject(projects.Projects[index]);
    }

    private void SelectSession()
    {
        if (updatingLists || sessionList.SelectedItem is not int index || index >= sessions.Length) return;
        var session = sessions[index];
        if (!BindSessionProject(session.WorkingDirectory)) return;
        refreshGate.SelectionChanged();
        primary.SessionId = session.Id; primary.Update(null); primary.SetFocus();
        settings.LastSessionId = session.Id;
        status.Text = session.WorkingDirectory;
    }

    private bool BindSessionProject(string root)
    {
        var different = !string.Equals(projectRoot, root, StringComparison.OrdinalIgnoreCase);
        if (different && !CanLeaveEditor()) return false;
        var project = projects.Projects.FirstOrDefault(p => string.Equals(p.Path, root, StringComparison.OrdinalIgnoreCase));
        if (project is null) { project = projects.Add(root); projects.Save(); }
        settings.LastProjectId = project.Id;
        if (different)
        {
            editor.Close(true); projectRoot = project.Path; relativeDirectory = "";
            RefreshProjects(); RefreshFiles(); UpdateEditorTitle();
        }
        return true;
    }

    private void OpenFileEntry()
    {
        if (fileList.SelectedItem is not int index || projectRoot is null) return;
        if (relativeDirectory.Length > 0)
        {
            if (index == 0) { relativeDirectory = Path.GetDirectoryName(relativeDirectory) ?? ""; RefreshFiles(); return; }
            index--;
        }
        if (index >= files.Length) return;
        var file = files[index];
        if (file.IsDirectory) { relativeDirectory = file.RelativePath; RefreshFiles(); }
        else OpenFile(file.RelativePath);
    }

    private void OpenFile(string relativePath, int line = 1)
    {
        if (projectRoot is null || !CanLeaveEditor()) return;
        editor.Open(projectRoot, relativePath, true); editor.GoToLine(line);
        ShowFiles(); UpdateEditorTitle(); editor.View.SetFocus();
    }

    private void SaveFile() { editor.Save(); UpdateEditorTitle(); status.Text = "Saved " + editor.Path; }
    private void ToggleFiles() { filesVisible = !filesVisible; ArrangePanels(); primary.SetFocus(); }
    private void ShowFiles() { filesVisible = true; ArrangePanels(); }
    private void ToggleSplit() { refreshGate.SelectionChanged(); settings.SplitTerminals = !settings.SplitTerminals; ArrangePanels(); }
    private void ShowHelp() => Dialogs.Text(application, "SHORTCUTS · CrewTerm", Program.Help + "\n\nEnter selects an item. F9 opens the searchable command palette.\nUse Alt+1/2/3/4/5 to move focus between terminals, editor, files and agents.\nThe embedded CLI receives ordinary keys, Ctrl+C, Esc and Tab.\nUse F9 → Send key for function keys reserved by CrewTerm.\nFile saves detect external changes made by agents and refuse to overwrite them.");

    private void Detach()
    {
        application.RequestStop(this);
    }

    private bool SaveLayoutForExit()
    {
        settings.LastSessionId = primary.SessionId;
        try { settingsStore.Save(settings); return true; }
        catch (IOException error)
        {
            return Dialogs.Confirm(application, "Layout could not be saved",
                error.Message + "\n\nDetach without saving this layout? Your source-file saves are unaffected.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !detached)
        {
            detached = true;
            application.Keyboard.KeyDown -= GlobalKey;
            if (refreshTimer is not null) application.RemoveTimeout(refreshTimer);
            InputDrained = inputDispatcher.CompleteAndDrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            inputDispatcher.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
