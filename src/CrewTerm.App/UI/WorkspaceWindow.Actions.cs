using CrewTerm.App.Server;
using CrewTerm.Automation;
using CrewTerm.Core;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace CrewTerm.App.UI;

public sealed partial class WorkspaceWindow
{
    private TerminalPane ActiveTerminal => secondary.HasFocus ? secondary : primary;
    private string Root => projectRoot ?? throw new InvalidOperationException("Add or select a project first (F2).");

    private void AddProject()
    {
        var path = Dialogs.Prompt(application, "Add project", "Existing folder path", projectRoot ?? Environment.CurrentDirectory);
        if (string.IsNullOrWhiteSpace(path)) return;
        var project = projects.Add(path.Trim().Trim('"'));
        projects.Save(); SwitchProject(project);
    }

    private void NewAgent()
    {
        var root = Root;
        var available = agents.Discover();
        var labels = available.Select(a => a.Profile.Name + (a.IsAvailable ? "  ·  " + a.ExecutablePath : "  ·  not installed")).ToList();
        labels.Add("Custom command…");
        var choice = Dialogs.Choose(application, "New agent · " + Path.GetFileName(root), labels);
        if (choice < 0) return;
        string? command, name;
        if (choice == available.Count)
        {
            command = Dialogs.Prompt(application, "Custom terminal", "Command (agent CLI, shell or ssh)", "cmd.exe");
            if (string.IsNullOrWhiteSpace(command)) return;
            name = Dialogs.Prompt(application, "Terminal name", "Sidebar display name", command);
        }
        else
        {
            var item = available[choice];
            if (!item.IsAvailable) throw new InvalidOperationException("Install " + item.Profile.Name + " and ensure its executable is on PATH, or add a custom profile.");
            command = (item.Profile with { Executable = item.ExecutablePath! }).CommandLine;
            name = item.Profile.Name;
        }
        if (name is null) return;
        StartSession(command, root, name);
    }

    private void StartSession(string command, string root, string name)
    {
        if (!BindSessionProject(root)) return;
        var response = Call(new("create", Command: command, WorkingDirectory: root, Name: name));
        refreshGate.SelectionChanged();
        primary.SessionId = response.SessionId; primary.Update(null); primary.SetFocus();
        settings.LastSessionId = response.SessionId;
        status.Text = "Started " + name + " in " + root;
        BeginRefresh();
    }

    private DaemonResponse Call(DaemonRequest request)
    {
        // Terminal.Gui installs a UI synchronization context. Do not block that context
        // while pipe continuations attempt to return to it.
        var response = Task.Run(() => client.CallAsync(request, lifetime.Token)).GetAwaiter().GetResult();
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Server request failed");
        return response;
    }

    private void Palette()
    {
        var commands = new PaletteCommand[]
        {
            new("project.add", "Project: Add folder", AddProject),
            new("project.remove", "Project: Remove from sidebar", RemoveProject),
            new("agent.new", "Agent: New embedded terminal", NewAgent),
            new("agent.profile", "Agent: Save custom CLI / SSH profile", AddProfile),
            new("agent.profiles.remove", "Agent: Remove custom profile", RemoveProfile),
            new("agent.restore", "Agent: Relaunch a saved session", RestoreSession),
            new("agent.close", "Agent: Terminate selected session", CloseSession),
            new("agent.secondary", "Agent: Choose second split terminal", ChooseSecondary),
            new("terminal.split", "Terminal: Toggle split", ToggleSplit),
            new("terminal.history", "Terminal: View / search scrollback", ShowScrollback),
            new("terminal.copy", "Terminal: Copy visible screen", CopyScreen),
            new("terminal.paste", "Terminal: Paste clipboard", Paste),
            new("terminal.key", "Terminal: Send reserved key (F1–F12 / Tab / Esc)", SendReservedKey),
            new("file.open", "Files: Open path / file:line:column", OpenPath),
            new("file.search", "Files: Search project text", SearchFiles),
            new("file.refresh", "Files: Refresh directory", RefreshFiles),
            new("file.save", "Editor: Save file", SaveFile),
            new("file.reload", "Editor: Reload file from disk", ReloadFile),
            new("file.goto", "Editor: Go to line", GoToLine),
            new("file.find", "Editor: Find text", FindInFile),
            new("file.context", "Editor: Send selected code to an agent", SendContext),
            new("git", "Git: Changes / diff / stage / worktrees", GitMenu),
            new("tasks", "Tasks: Run / history / cancel / pipelines", TaskMenu),
            new("checkpoint", "Checkpoints: Capture / preview / restore", CheckpointMenu),
            new("settings", "Settings: Concurrency and state directory", SettingsMenu),
            new("help", "Help: Shortcuts and usage", ShowHelp),
            new("detach", "Workspace: Detach (keep all sessions running)", Detach),
            new("server.stop", "Workspace: Stop all sessions and background tasks", StopServer)
        };
        var choice = Dialogs.Choose(application, "COMMANDS · type to filter", commands.Select(c => c.Label).ToArray());
        if (choice >= 0) commands[choice].Execute();
    }

    private void RemoveProject()
    {
        var items = projects.Projects.ToArray();
        var index = Dialogs.Choose(application, "Remove project from sidebar", items.Select(p => p.Name + "  ·  " + p.Path).ToArray());
        if (index < 0) return;
        projects.Remove(items[index].Id); projects.Save(); RefreshProjects();
        status.Text = "Removed sidebar entry; folder remains on disk.";
    }

    private void AddProfile()
    {
        var name = Dialogs.Prompt(application, "Agent profile", "Display name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var executable = Dialogs.Prompt(application, "Agent profile", "Executable name or full path", "ssh");
        if (string.IsNullOrWhiteSpace(executable)) return;
        var arguments = Dialogs.Prompt(application, "Agent profile", "Arguments (optional)");
        if (arguments is null) return;
        agents.Upsert(new(Guid.NewGuid().ToString("N"), name, executable.Trim('"'), arguments));
        status.Text = "Profile saved. Open F3 to launch.";
    }

    private void RemoveProfile()
    {
        var custom = agents.Profiles.Where(p => !p.IsBuiltIn).ToArray();
        var index = Dialogs.Choose(application, "Remove custom profile", custom.Select(p => p.Name).ToArray());
        if (index >= 0) agents.Remove(custom[index].Id);
    }

    private void RestoreSession()
    {
        var saved = AtomicJsonStore.Load<SessionInfo[]>(Path.Combine(stateDirectory, "saved-sessions.json")) ?? [];
        var index = Dialogs.Choose(application, "Relaunch saved command (new process)", saved.Select(s => s.Name + "  ·  " + s.WorkingDirectory).ToArray());
        if (index >= 0) StartSession(saved[index].Command, saved[index].WorkingDirectory, saved[index].Name);
    }

    private void CloseSession()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        if (!Dialogs.Confirm(application, "Terminate terminal", "Stop this session and its child processes?")) return;
        Call(new("close", pane.SessionId)); refreshGate.SelectionChanged(); pane.SessionId = null; pane.Update(null);
    }

    private void ChooseSecondary()
    {
        var candidates = sessions.Where(s => s.Id != primary.SessionId).ToArray();
        var index = Dialogs.Choose(application, "Second terminal", candidates.Select(s => s.Name + " · " + s.Id).ToArray());
        if (index < 0) return;
        refreshGate.SelectionChanged(); secondary.SessionId = candidates[index].Id; settings.SplitTerminals = true; ArrangePanels();
        secondary.Update(null); secondary.SetFocus();
    }

    private void Paste()
    {
        var pane = ActiveTerminal;
        if (application.Clipboard is null || !application.Clipboard.TryGetClipboardData(out var text) || string.IsNullOrEmpty(text)) return;
        if (text.Length > 60000) throw new InvalidOperationException("Paste is limited to 60,000 characters. Select a smaller range.");
        if (text.Contains('\n') && pane.Snapshot?.BracketedPaste != true &&
            !Dialogs.Confirm(application, "Multiline paste", "This terminal has not enabled bracketed paste. Newlines may execute commands. Paste anyway?")) return;
        QueueInput(pane, pane.Snapshot?.BracketedPaste == true ? "\u001b[200~" + text + "\u001b[201~" : text);
    }

    private void CopyScreen()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        if (application.Clipboard is null) throw new InvalidOperationException("Clipboard unavailable in this terminal.");
        application.Clipboard.SetClipboardData(Call(new("screen", pane.SessionId)).Text ?? "");
        status.Text = "Terminal screen copied.";
    }

    private void ShowScrollback()
    {
        var pane = ActiveTerminal;
        if (pane.SessionId is null) return;
        var response = Call(new("screen", pane.SessionId));
        Dialogs.Text(application, "SCROLLBACK · Ctrl+F to find", string.Join('\n', response.Screen?.History ?? []) + "\n" + response.Text);
    }

    private void SendReservedKey()
    {
        var pane = ActiveTerminal;
        Key[] keys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12, Key.Tab, Key.Esc];
        var index = Dialogs.Choose(application, "Send key to terminal", keys.Select(k => k.ToString()).ToArray());
        if (index >= 0) QueueInput(pane, TerminalInput.Encode(keys[index])!);
    }

    private void OpenPath()
    {
        var path = Dialogs.Prompt(application, "Open file", "Relative or absolute file path, optionally :line:column");
        if (string.IsNullOrWhiteSpace(path)) return;
        var location = FileReferenceParser.Parse(path, Root);
        if (location is not null) OpenFile(Path.GetRelativePath(Root, location.Path), location.Line);
        else OpenFile(Path.IsPathRooted(path) ? Path.GetRelativePath(Root, path) : path);
    }

    private void ReloadFile()
    {
        if (editor.Path is null) return;
        OpenFile(Path.GetRelativePath(Root, editor.Path));
    }

    private void GoToLine()
    {
        var line = Dialogs.Prompt(application, "Go to line", "Line number", "1");
        if (int.TryParse(line, out var value)) { editor.GoToLine(value); ShowFiles(); editor.View.SetFocus(); }
    }

    private void FindInFile()
    {
        var query = Dialogs.Prompt(application, "Find in file", "Text");
        if (string.IsNullOrEmpty(query)) return;
        editor.View.FindNext(query, false, false); ShowFiles(); editor.View.SetFocus();
    }

    private void SearchFiles()
    {
        var root = Root;
        var query = Dialogs.Prompt(application, "Search project", "Text (literal, case insensitive)");
        if (string.IsNullOrEmpty(query)) return;
        RunWork("Searching…", () => CrewTerm.Workspace.FileWorkspace.SearchAsync(root, query, lifetime.Token), results =>
        {
            var index = Dialogs.Choose(application, $"SEARCH · {results.Count} matches", results.Select(r => $"{r.RelativePath}:{r.LineNumber}  {r.Text}").ToArray());
            if (index >= 0 && projectRoot == root) OpenFile(results[index].RelativePath, results[index].LineNumber);
        });
    }

    private void SendContext()
    {
        if (editor.Path is null || editor.View.Document is null) throw new InvalidOperationException("Open a file and select code first.");
        var doc = editor.View.Document;
        var start = editor.View.HasSelection ? Math.Min(editor.View.SelectionStart, editor.View.SelectionEnd) : editor.View.CaretOffset;
        var end = editor.View.HasSelection ? Math.Max(editor.View.SelectionStart, editor.View.SelectionEnd) - 1 : start;
        var first = doc.GetLocation(start).Line;
        var last = doc.GetLocation(Math.Max(start, end)).Line;
        var instruction = Dialogs.Prompt(application, "Send code context", $"Lines {first}–{last}: instruction", "Review this code");
        if (instruction is null) return;
        var targets = sessions.Where(s => !s.HasExited).ToArray();
        var index = Dialogs.Choose(application, "Send selected code to agent", targets.Select(s => s.Name + " · " + s.Id).ToArray());
        if (index < 0) return;
        var id = targets[index].Id;
        var screen = Call(new("screen", id)).Screen;
        if (screen?.BracketedPaste != true) throw new InvalidOperationException("Target CLI must enable bracketed paste before sending code. This avoids executing code as shell commands.");
        Call(new("input", id, Text: "\u001b[200~" + editor.CreateContext(instruction, first, last) + "\u001b[201~"));
        status.Text = "Code inserted into " + targets[index].Name + ". Review and press Enter in that terminal to submit.";
    }

    private void SettingsMenu()
    {
        var value = Dialogs.Prompt(application, "Settings", "Maximum concurrent background tasks (1–32; applied after server restart)", settings.MaximumConcurrentTasks.ToString());
        if (value is null) return;
        if (!int.TryParse(value, out var count) || count is < 1 or > 32) throw new ArgumentException("Enter a number between 1 and 32.");
        settings.MaximumConcurrentTasks = count; settingsStore.Save(settings);
        Dialogs.Text(application, "Settings saved", "State directory:\n" + stateDirectory + "\n\nUse 'Workspace: Stop all' and reopen to apply concurrency. Existing terminals will stop only when explicitly requested.");
    }

    private void StopServer()
    {
        if (!CanLeaveEditor()) return;
        if (!Dialogs.Confirm(application, "Stop workspace server", "Terminate ALL embedded terminals and cancel ALL running tasks?")) return;
        if (!SaveLayoutForExit()) return;
        Call(new("stop")); exitApproved = true; application.RequestStop(this);
    }

    private void RunWork<T>(string label, Func<Task<T>> work, Action<T> completed)
    {
        status.Text = label;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work();
                if (!detached) application.Invoke(() => { if (!detached) Guard(() => { status.Text = "Ready"; completed(result); }); });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!detached) application.Invoke(() => { if (!detached) Guard(() => throw new IOException(label + " " + error.Message, error)); }); }
        });
    }
}
