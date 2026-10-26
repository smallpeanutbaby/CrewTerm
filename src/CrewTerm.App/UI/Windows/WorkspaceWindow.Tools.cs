using System.Text;
using CrewTerm.App.Protocol;
using CrewTerm.App.UI.Controls;
using CrewTerm.Automation.Pipelines;
using CrewTerm.Core.Security;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using CodeEditor = Terminal.Gui.Editor.Editor;

namespace CrewTerm.App.UI.Windows;

/// <remarks>
/// 本分部文件：开发工具菜单——Git 变更、暂存与 worktree，后台命令任务、命令模板与流水线，以及项目文件检查点。
/// 耗时操作都通过 <see cref="RunWork{T}"/> 在后台执行。
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 打开 Git 菜单（F6），对当前项目执行所选操作。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>查看工作区变更 / 查看已暂存变更：列出变更文件（后者只列已暂存的），选中后显示对应的 diff。</description></item>
    /// <item><description>暂存文件：列出全部变更文件，暂存所选文件。</description></item>
    /// <item><description>列出 / 打开 worktree：选中的 worktree 目录加入项目列表并切换过去。</description></item>
    /// <item><description>创建隔离分支：询问新分支名（默认 <c>crew/feature</c>）与检出目录（默认为项目目录加 <c>-worktree</c>），创建后切换过去。</description></item>
    /// <item><description>移除干净的 worktree：确认后移除所选检出目录；有未提交修改时 Git 会拒绝。</description></item>
    /// </list>
    /// 所有 Git 调用都在后台执行，失败以消息框报告。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目。</exception>
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
                var index = Dialogs.Choose(application, "WORKTREES", trees.Select(t => $"{(t.IsDetached || t.Branch.Length == 0 ? "detached " + t.Head[..Math.Min(8, t.Head.Length)] : t.Branch)}  ·  {t.Path}").ToArray());
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

    /// <summary>
    /// 打开后台任务菜单（F7）：运行命令、查看任务历史、运行或保存命令模板、运行或创建流水线，以及删除模板。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>运行命令：询问命令（默认 <c>dotnet test</c>），交给 daemon 在当前项目根目录中作为后台任务运行，并打开输出窗口。</description></item>
    /// <item><description>任务列表：显示运行中、已退出或已取消的任务，选中后打开输出窗口（可在其中取消）。</description></item>
    /// <item><description>运行已保存的命令：始终在当前选中的项目中运行，而不是保存模板时的项目。</description></item>
    /// <item><description>命令模板保存在工作台设置中，删除后立即写盘；流水线模板保存在状态目录中独立的模板存储里。</description></item>
    /// </list>
    /// 任务由 daemon 持有，关闭输出窗口或分离工作台都不会结束任务。
    /// </remarks>
    /// <exception cref="InvalidOperationException">需要项目的操作在没有当前项目时执行，或 daemon 调用失败。</exception>
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

    /// <summary>
    /// 询问名称（默认 “Test”）与命令（默认 <c>dotnet test</c>）并保存为命令模板，同名（不区分大小写）的旧模板被替换；设置立即写盘。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有当前项目。</exception>
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

    /// <summary>
    /// 交互式创建流水线模板：询问名称后逐步输入命令（第一步默认 <c>dotnet build</c>），留空结束，最多 20 步。
    /// </summary>
    /// <remarks>
    /// 输入任何一步时取消会放弃整个模板；没有任何步骤时不保存。运行时各步骤按顺序执行，遇到失败即停止。
    /// 模板不绑定项目，运行时使用当前项目目录。
    /// </remarks>
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

    /// <summary>
    /// 选择一个流水线模板，在当前项目目录中交给 daemon 运行，并打开输出窗口。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有当前项目，或 daemon 拒绝启动流水线。</exception>
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

    /// <summary>
    /// 以模态对话框显示后台任务的实时输出，直到用户关闭；关闭对话框不会结束任务。
    /// </summary>
    /// <remarks>
    /// 每 350 毫秒在后台读取一次任务状态（上一次读取未完成时跳过），回到 UI 线程更新状态行与输出：
    /// 光标原本在末尾时继续跟随新输出，否则保持原位置。任务结束后 “Cancel task” 按钮变为不可用。
    /// 对话框关闭或窗口分离后，迟到的结果不再更新界面，计时器也会被移除。
    /// </remarks>
    /// <param name="id">任务标识。</param>
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
        // 这两个标志由 UI 线程与后台刷新任务共同读写：closed 用 Volatile 读写，pending 用 Interlocked 抢占。
        var closed = false;
        var pending = 0;
        string previous = "";
        // 在后台读取一次任务状态并回到 UI 线程刷新对话框；上一次读取尚未结束或对话框已关闭时直接返回。
        void Refresh()
        {
            if (Volatile.Read(ref closed) || Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await client.CallAsync(new("task-get", id), lifetime.Token);
                    if (Volatile.Read(ref closed) || detached) return;
                    application.Invoke(() =>
                    {
                        if (closed || detached) return;
                        if (!response.Ok || response.Task is not DaemonTaskInfo task) { info.Text = response.Error ?? "Task unavailable"; return; }
                        info.Text = DisplayText.Sanitize($"{task.Name} · {(task.Completed ? task.Cancelled ? "CANCELLED" : "EXIT " + task.ExitCode : "RUNNING")} · {task.WorkingDirectory}");
                        // 命令输出常带颜色等 ANSI 序列，显示前清理，既可读也不会把控制序列送到宿主终端。
                        var text = DisplayText.Sanitize(task.Output + (task.Error is null ? "" : "\nERROR: " + task.Error));
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
                { if (!Volatile.Read(ref closed) && !detached) application.Invoke(() => { if (!closed) info.Text = error.Message; }); }
                finally { Volatile.Write(ref pending, 0); }
            });
        }
        var timer = application.AddTimeout(TimeSpan.FromMilliseconds(350), () => { Refresh(); return !Volatile.Read(ref closed); });
        try { Refresh(); output.SetFocus(); application.Run(dialog); }
        finally { Volatile.Write(ref closed, true); if (timer is not null) application.RemoveTimeout(timer); }
    }

    /// <summary>
    /// 打开检查点菜单：捕获当前项目文件，或预览并恢复一个已有检查点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 捕获前先处理编辑器中的未保存修改，然后询问名称（默认 “Before agent changes”），在后台捕获，完成后在状态栏显示文件数与大小（KiB）。
    /// </para>
    /// <para>
    /// 恢复时先在后台计算预览，显示每个变更的类型与路径；在预览文本不超过 200,000 个字符之前还附带当前内容与检查点内容。
    /// 有变更时再处理未保存修改并请求确认，然后在后台恢复：检查点之后新增的文件（Added）会被删除，缺失的文件（Missing）会被重新创建。
    /// 恢复完成后，若编辑器仍显示恢复前的同一文档且没有新的修改，则重新加载该文件；否则保留编辑器内容并提示手动重新加载。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有当前项目。</exception>
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
