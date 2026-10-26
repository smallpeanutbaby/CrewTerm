using System.Text;
using CrewTerm.App.Server;
using CrewTerm.Automation;
using CrewTerm.Core;
using CrewTerm.Workspace;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CodeEditor = Terminal.Gui.Editor.Editor;

namespace CrewTerm.App.UI;

public sealed partial class WorkspaceWindow
{
    private void GitMenu()
    {
        var root = Root;
        var choice = Dialogs.Choose(application, "GIT", ["Review working changes", "Review staged changes", "Stage a file", "Worktrees: list / open", "Worktrees: create isolated branch", "Worktrees: remove clean checkout"]);
        if (choice is >= 0 and <= 2)
        {
            RunWork("Reading Git changes…", () => git.GetChangesAsync(root, lifetime.Token), changes =>
            {
                var filtered = changes.Where(c => choice != 1 || c.IsStaged).ToArray();
                var index = Dialogs.Choose(application, "CHANGES · " + root, filtered.Select(c => c.Status + "  " + c.RelativePath).ToArray());
                if (index < 0) return;
                var file = filtered[index];
                if (choice == 2)
                    RunWork("Staging " + file.RelativePath, async () => { await git.StageAsync(root, file.RelativePath, lifetime.Token); return file.RelativePath; }, path => status.Text = "Staged " + path);
                else RunWork("Reading diff…", () => git.GetDiffAsync(root, file.RelativePath, choice == 1, lifetime.Token), diff =>
                    Dialogs.Text(application, (choice == 1 ? "STAGED DIFF · " : "DIFF · ") + file.RelativePath, diff));
            });
        }
        else if (choice is 3 or 5)
        {
            RunWork("Reading worktrees…", () => git.ListWorktreesAsync(root, lifetime.Token), trees =>
            {
                var index = Dialogs.Choose(application, "WORKTREES", trees.Select(t => $"{t.Branch ?? "detached"}  ·  {t.Path}").ToArray());
                if (index < 0) return;
                var tree = trees[index];
                if (choice == 3)
                { var project = projects.Add(tree.Path); projects.Save(); SwitchProject(project); }
                else if (Dialogs.Confirm(application, "Remove worktree", "Remove clean worktree checkout at:\n" + tree.Path + "\nGit will refuse a dirty checkout."))
                    RunWork("Removing worktree…", async () => { await git.RemoveWorktreeAsync(root, tree.Path, lifetime.Token); return tree.Path; }, path => status.Text = "Removed " + path);
            });
        }
        else if (choice == 4)
        {
            var branch = Dialogs.Prompt(application, "New worktree", "New branch name", "crew/feature");
            if (string.IsNullOrWhiteSpace(branch)) return;
            var path = Dialogs.Prompt(application, "New worktree", "New checkout directory", root + "-worktree");
            if (string.IsNullOrWhiteSpace(path)) return;
            RunWork("Creating isolated worktree…", () => git.CreateWorktreeAsync(root, path, branch, lifetime.Token), tree =>
            { var project = projects.Add(tree.Path); projects.Save(); SwitchProject(project); });
        }
    }

    private void TaskMenu()
    {
        var choice = Dialogs.Choose(application, "TASKS", ["Run command", "Live tasks / history / cancel", "Run saved command", "Save command template", "Run pipeline", "Create pipeline template", "Remove command template", "Remove pipeline template"]);
        switch (choice)
        {
            case 0:
                var root = Root;
                var command = Dialogs.Prompt(application, "Run background command", "Command in " + root, "dotnet test");
                if (!string.IsNullOrWhiteSpace(command))
                {
                    var result = Call(new("task-start", Command: command, WorkingDirectory: root, Name: command));
                    ShowTask(result.SessionId!);
                }
                break;
            case 1:
                var tasks = Call(new("task-list")).Tasks ?? [];
                var index = Dialogs.Choose(application, "TASK HISTORY", tasks.Select(t => $"{(t.Completed ? t.Cancelled ? "CANCELLED" : "EXIT " + t.ExitCode : "RUNNING")}  {t.Name}  ·  {t.StartedAt.ToLocalTime():MM-dd HH:mm}").ToArray());
                if (index >= 0) ShowTask(tasks[index].Id);
                break;
            case 2:
                var templates = settings.Templates.ToArray();
                var selected = Dialogs.Choose(application, "COMMAND TEMPLATES", templates.Select(t => t.Name + " · " + t.Command).ToArray());
                if (selected >= 0)
                {
                    var template = templates[selected];
                    ShowTask(Call(new("task-start", Command: template.Command, WorkingDirectory: Root, Name: template.Name)).SessionId!);
                }
                break;
            case 3: SaveCommandTemplate(); break;
            case 4: RunPipelineTemplate(); break;
            case 5: CreatePipelineTemplate(); break;
            case 6:
                var removed = Dialogs.Choose(application, "Remove command template", settings.Templates.Select(t => t.Name).ToArray());
                if (removed >= 0) { settings.Templates.RemoveAt(removed); settingsStore.Save(settings); }
                break;
            case 7:
                var store = new PipelineTemplateStore(stateDirectory);
                var pipelines = store.List();
                var remove = Dialogs.Choose(application, "Remove pipeline template", pipelines.Select(t => t.Name).ToArray());
                if (remove >= 0) store.Remove(pipelines[remove].Id);
                break;
        }
    }

    private void SaveCommandTemplate()
    {
        var name = Dialogs.Prompt(application, "Command template", "Name", "Test");
        if (string.IsNullOrWhiteSpace(name)) return;
        var command = Dialogs.Prompt(application, "Command template", "Command (runs in selected project)", "dotnet test");
        if (string.IsNullOrWhiteSpace(command)) return;
        settings.Templates.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        settings.Templates.Add(new(name, command, Root)); settingsStore.Save(settings);
        status.Text = "Command template saved.";
    }

    private void CreatePipelineTemplate()
    {
        var name = Dialogs.Prompt(application, "Pipeline template", "Name", "Build and test");
        if (string.IsNullOrWhiteSpace(name)) return;
        var steps = new List<PipelineStepTemplate>();
        while (steps.Count < 20)
        {
            var command = Dialogs.Prompt(application, $"Pipeline · step {steps.Count + 1}", "Command; leave empty to finish", steps.Count == 0 ? "dotnet build" : "");
            if (command is null) return;
            if (string.IsNullOrWhiteSpace(command)) break;
            steps.Add(new("Step " + (steps.Count + 1), command));
        }
        if (steps.Count == 0) return;
        new PipelineTemplateStore(stateDirectory).Upsert(new(Guid.NewGuid().ToString("N"), name, steps));
        status.Text = "Pipeline saved. Steps run sequentially and stop on failure.";
    }

    private void RunPipelineTemplate()
    {
        var root = Root;
        var templates = new PipelineTemplateStore(stateDirectory).List();
        var index = Dialogs.Choose(application, "PIPELINES · stop on first failure", templates.Select(t => t.Name + " · " + t.Steps.Count + " steps").ToArray());
        if (index < 0) return;
        var template = templates[index];
        var result = Call(new("pipeline-start", Name: template.Name, Steps: template.ForProject(root).ToArray()));
        ShowTask(result.SessionId!);
    }

    private void ShowTask(string id)
    {
        using var dialog = new Dialog { Title = "TASK OUTPUT · " + id, Width = Dim.Percent(95), Height = Dim.Percent(92) };
        var info = new Label { Width = Dim.Fill(), Height = 1 };
        var output = new CodeEditor { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true,
            Document = new TextDocument("Connecting…"), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var cancel = new Button { Text = "Cancel task", Y = Pos.AnchorEnd(1) };
        cancel.Accepting += (_, e) => { e.Handled = true; Guard(() => Call(new("task-cancel", id))); };
        dialog.Add(info, output, cancel);
        dialog.AddButton(new Button { Text = "Close (keep running)" });
        var closed = false;
        var pending = false;
        string previous = "";
        void Refresh()
        {
            if (pending || closed) return;
            pending = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await client.CallAsync(new("task-get", id), lifetime.Token);
                    if (closed || detached) return;
                    application.Invoke(() =>
                    {
                        if (closed || detached) return;
                        if (!response.Ok || response.Task is not DaemonTaskInfo task) { info.Text = response.Error ?? "Task unavailable"; return; }
                        info.Text = $"{task.Name} · {(task.Completed ? task.Cancelled ? "CANCELLED" : "EXIT " + task.ExitCode : "RUNNING")} · {task.WorkingDirectory}";
                        var text = task.Output + (task.Error is null ? "" : "\nERROR: " + task.Error);
                        if (previous != text)
                        {
                            var follow = output.CaretOffset >= (output.Document?.TextLength ?? 0) - 1;
                            var oldOffset = output.CaretOffset;
                            output.Document = new TextDocument(text);
                            output.CaretOffset = follow ? text.Length : Math.Min(oldOffset, text.Length);
                            previous = text;
                        }
                        cancel.Enabled = !task.Completed;
                    });
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { if (!closed && !detached) application.Invoke(() => { if (!closed) info.Text = error.Message; }); }
                finally { pending = false; }
            });
        }
        var timer = application.AddTimeout(TimeSpan.FromMilliseconds(350), () => { Refresh(); return !closed; });
        try { Refresh(); output.SetFocus(); application.Run(dialog); }
        finally { closed = true; if (timer is not null) application.RemoveTimeout(timer); }
    }

    private void CheckpointMenu()
    {
        var root = Root;
        var choice = Dialogs.Choose(application, "CHECKPOINTS", ["Capture current project files", "Preview / restore checkpoint"]);
        if (choice == 0)
        {
            if (!CanLeaveEditor()) return;
            var label = Dialogs.Prompt(application, "Capture checkpoint", "Name", "Before agent changes");
            if (string.IsNullOrWhiteSpace(label)) return;
            RunWork("Capturing files…", () => checkpoints.CaptureAsync(root, label, lifetime.Token), result =>
                status.Text = $"Checkpoint saved · {result.FileCount} files · {result.TotalBytes / 1024} KiB");
        }
        else if (choice == 1)
        {
            var list = checkpoints.List(root);
            var index = Dialogs.Choose(application, "CHECKPOINTS", list.Select(c => $"{c.Label} · {c.CreatedAt.ToLocalTime():MM-dd HH:mm} · {c.FileCount} files").ToArray());
            if (index < 0) return;
            RunWork("Comparing checkpoint…", () => checkpoints.PreviewAsync(root, list[index].Id, lifetime.Token), preview =>
            {
                var text = new StringBuilder("Restore preview. Added = current new file will be removed; Missing = file will be recreated.\n\n");
                foreach (var change in preview.Changes)
                {
                    text.AppendLine($"{change.Kind}: {change.RelativePath}");
                    if (text.Length < 200000)
                        text.AppendLine("--- Current\n" + change.BeforeText + "\n+++ Checkpoint\n" + change.AfterText + "\n");
                }
                Dialogs.Text(application, "RESTORE PREVIEW · " + preview.Changes.Count + " changes", text.ToString());
                if (preview.Changes.Count == 0 || !CanLeaveEditor()) return;
                if (!Dialogs.Confirm(application, "Restore checkpoint", "Apply the previewed changes? New files shown as Added will be removed. Stop agents editing these files before restoring.")) return;
                var expectedDocument = editor.View.Document;
                RunWork("Restoring checkpoint…", async () => { await checkpoints.RestoreAsync(preview, lifetime.Token); return true; }, _ =>
                {
                    var reloaded = editor.TryReloadUnchanged(expectedDocument);
                    RefreshFiles(); UpdateEditorTitle();
                    status.Text = reloaded ? "Checkpoint restored; editor refreshed." :
                        "Checkpoint restored on disk. Current editor contents kept; reload to review.";
                });
            });
        }
    }
}
