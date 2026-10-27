using CrewTerm.App.UI.Controls;
using CrewTerm.Core.IO;
using CrewTerm.Workspace.Documents;
using CrewTerm.Workspace.Files;
using Terminal.Gui.Input;

namespace CrewTerm.App.UI.Windows;

/// <summary>
/// 工作台的文件查看：侧栏 <c>files</c> 分区浏览当前项目的目录，单击文件后在终端下方的代码查看器中打开（可编辑，F5 保存）。
/// </summary>
/// <remarks>
/// <para><c>files</c> 分区每行一个条目：<c>‹ ..</c> 返回上级，<c>▸ 名称/</c> 为目录，<c>· 名称</c> 为文件，代码查看器中打开的文件以粗体高亮；
/// 标题后面显示当前目录。单击或 Enter：目录则进入，文件则打开。Agent 在后台创建或删除文件时，列表每 <see cref="FileRefreshInterval"/> 在后台重新读取一次。</para>
/// <para>代码查看器默认隐藏，打开文件时出现在终端区下方；F4 显示或隐藏；在其中按 Esc 把焦点还给终端（不会落到 Terminal.Gui 默认的“退出”命令上导致分离）。</para>
/// </remarks>
public sealed partial class WorkspaceWindow
{
    /// <summary>
    /// 在后台重新读取当前目录的最短间隔。
    /// </summary>
    private static readonly TimeSpan FileRefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>..</c>（返回上级目录）行的键；项目相对路径不可能包含 NUL，因此不会与任何条目冲突。
    /// </summary>
    private const string ParentRowKey = "\0..";

    /// <summary>
    /// 目录行符号的颜色。
    /// </summary>
    private const int DirectoryColor = 0x89b4fa;

    /// <summary>
    /// 文件行与 <c>..</c> 行符号的暗色。
    /// </summary>
    private const int FileColor = 0x7f849c;

    /// <summary>
    /// 没有当前项目时 <c>files</c> 分区的占位提示；单击它添加项目。
    /// </summary>
    private const string NoProjectText = "+ add a project first  ·  F2";

    /// <summary>
    /// 左下方的 <c>files</c> 分区，每个条目一行，行的键为条目的项目相对路径（<c>..</c> 行为 <see cref="ParentRowKey"/>）。
    /// </summary>
    private readonly SidebarSection fileSection = new("files", NoProjectText) { ActionLabel = "" };

    /// <summary>
    /// <c>files</c> 分区当前显示的目录，项目相对路径；空字符串表示项目根目录。
    /// </summary>
    private string relativeDirectory = "";

    /// <summary>
    /// 当前目录下的条目（目录在前，按名称排序），与 <c>files</c> 分区中 <c>..</c> 之后的各行对应。
    /// </summary>
    private FileEntry[] files = [];

    /// <summary>
    /// 正在后台读取的目录（绝对路径）；只在 UI 线程访问。同一目录同时只有一次读取：读取卡住（例如网络路径无响应）时不会堆积线程，
    /// 也不会影响其他项目或目录的自动刷新。
    /// </summary>
    private readonly HashSet<string> fileReads = new(FileSystemPath.Comparer);

    /// <summary>
    /// 目录读取的版本号：每次同步读取（进入目录、切换项目、手动刷新、检查点恢复之后）加一；只在 UI 线程访问。
    /// 后台读取开始后版本号变了，说明已有更新的结果，后台结果被丢弃。
    /// </summary>
    private int fileReadGeneration;

    /// <summary>
    /// 最近一次读取目录（同步或后台）完成的时间；只在 UI 线程访问。
    /// </summary>
    private DateTime filesReadAt;

    /// <summary>
    /// 当前目录无法读取时的原因；可以读取时为 <see langword="null"/>。只在 UI 线程访问。
    /// </summary>
    private string? filesFailure;

    /// <summary>
    /// 连接 <c>files</c> 分区的单击、新建与 Esc 回调，以及代码查看器的 Esc。在构造窗口时调用一次。
    /// </summary>
    private void ConfigureFileSection()
    {
        fileSection.RowActivated = row => Guard(() => ActivateFileRow(row.Key));
        // 只有占位提示 “add a project first” 可以新建：有项目时 + 键与单击空目录的提示都不做任何事。
        fileSection.CreateRequested = () => { if (projectRoot is null) Guard(AddProject); };
        fileSection.FocusReturnRequested = FocusActiveTerminal;
        // 编辑器自己没有处理的 Esc 会落到 Terminal.Gui 默认的“退出”命令上（即分离工作台），改为把焦点还给终端。
        editor.View.KeyDownNotHandled += (_, key) =>
        {
            if (key != Key.Esc) return;
            key.Handled = true;
            FocusActiveTerminal();
        };
    }

    /// <summary>
    /// 激活 <c>files</c> 分区的一行：<c>..</c> 返回上级目录，目录则进入，文件则在代码查看器中打开。
    /// </summary>
    /// <param name="key">行的键；对应的条目已不在列表中时什么也不做。</param>
    private void ActivateFileRow(string key)
    {
        if (projectRoot is null) return;
        if (key == ParentRowKey) { EnterDirectory(Path.GetDirectoryName(relativeDirectory) ?? ""); return; }
        if (files.FirstOrDefault(f => f.RelativePath == key) is not { } entry) return;
        if (entry.IsDirectory) EnterDirectory(entry.RelativePath);
        else OpenFile(entry.RelativePath);
    }

    /// <summary>
    /// 进入另一个目录：重新读取并选中第一行、滚动到顶部。
    /// </summary>
    /// <param name="directory">项目相对路径；空字符串表示项目根目录。</param>
    private void EnterDirectory(string directory)
    {
        relativeDirectory = directory;
        ReadFiles(reveal: true);
    }

    /// <summary>
    /// 重新读取当前目录（命令面板 “Files: Refresh directory”、检查点恢复之后），尽量保持原选择与滚动位置。
    /// </summary>
    private void RefreshFiles() => ReadFiles(reveal: false);

    /// <summary>
    /// 在 UI 线程同步读取当前目录并重建 <c>files</c> 分区。
    /// </summary>
    /// <remarks>
    /// 没有当前项目时列表为空并提示添加项目。列表不包含 <c>.git</c> 与符号链接等重解析点。
    /// 项目目录可能已被删除、移走或无权访问（例如上次打开的项目）：工作台仍要能打开，列表显示为空并在状态栏说明原因。
    /// </remarks>
    /// <param name="reveal">换了目录时为 <see langword="true"/>：选中第一行并滚动到顶部；否则按键保持原选择与滚动位置。</param>
    private void ReadFiles(bool reveal)
    {
        fileReadGeneration++;
        filesFailure = null;
        try { files = projectRoot is null ? [] : FileWorkspace.ListDirectory(projectRoot, relativeDirectory).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            files = [];
            filesFailure = error.Message;
        }
        filesReadAt = DateTime.UtcNow;
        ShowFileRows(reveal);
        if (filesFailure is not null) status.Text = "Project folder unavailable: " + filesFailure;
    }

    /// <summary>
    /// 按 <see cref="files"/>、<see cref="relativeDirectory"/> 与 <see cref="filesFailure"/> 重建 <c>files</c> 分区的行、占位提示与标题后的目录。
    /// </summary>
    /// <param name="reveal">
    /// 为 <see langword="true"/> 时选中第一行并滚动到顶部（换了目录）；否则原地刷新，选中行与滚动位置按行的键保持（见 <see cref="SidebarSection.Refresh"/>）。
    /// </param>
    private void ShowFileRows(bool reveal)
    {
        var rows = new List<SidebarRow>(files.Length + 1);
        if (relativeDirectory.Length > 0) rows.Add(new(ParentRowKey, "‹", FileColor, "..", "", false));
        var open = editor.Path is null ? null : FileSystemPath.Normalize(editor.Path);
        foreach (var entry in files)
        {
            rows.Add(entry.IsDirectory
                ? new(entry.RelativePath, "▸", DirectoryColor, entry.Name + "/", "", false)
                : new(entry.RelativePath, "·", FileColor, entry.Name, "",
                    open is not null && projectRoot is not null && FileSystemPath.AreSame(FileSystemPath.Normalize(Path.Combine(projectRoot, entry.RelativePath)), open)));
        }
        if (reveal) fileSection.SetRows(rows, 0, reveal: true);
        else fileSection.Refresh(rows);
        fileSection.EmptyText = projectRoot is null ? NoProjectText : filesFailure is not null ? "folder unavailable" : "empty folder";
        fileSection.HeadingDetail = projectRoot is null ? "" : "/" + relativeDirectory.Replace('\\', '/');
    }

    /// <summary>
    /// 每 <see cref="FileRefreshInterval"/> 在后台重新读取一次当前目录，有变化时更新 <c>files</c> 分区（Agent 创建或删除了文件）。
    /// </summary>
    /// <remarks>
    /// <para>由 160 毫秒的刷新计时器在 UI 线程调用；侧栏隐藏、没有当前项目、该目录的上一次读取尚未结束或窗口已分离时直接返回。</para>
    /// <para>目录读取在线程池上进行。当前目录已被删除（例如 Agent 删掉了 <c>build/</c>）时改为显示最近的仍然存在的上级目录，并在状态栏说明；
    /// 连项目根目录也无法读取时列表清空并显示 <c>folder unavailable</c>。结果回到 UI 线程后，只要期间发生过同步读取（版本号变了）就丢弃；
    /// 内容没有变化时不重绘。</para>
    /// </remarks>
    private void BeginFileRefresh()
    {
        if (detached || !sidebarVisible || projectRoot is not { } root || DateTime.UtcNow - filesReadAt < FileRefreshInterval) return;
        var directory = relativeDirectory;
        var path = Path.Combine(root, directory);
        if (!fileReads.Add(path)) return;
        var generation = fileReadGeneration;
        _ = Task.Run(() =>
        {
            var (shown, entries, failure) = ListNearestDirectory(root, directory);
            if (!detached) application.Invoke(() =>
            {
                fileReads.Remove(path);
                if (detached || generation != fileReadGeneration) return;
                filesReadAt = DateTime.UtcNow;
                var moved = shown != relativeDirectory;
                if (!moved && failure == filesFailure && entries.SequenceEqual(files)) return;
                if (failure is not null && failure != filesFailure) status.Text = "Project folder unavailable: " + failure;
                else if (moved) status.Text = $"/{relativeDirectory.Replace('\\', '/')} no longer exists; showing /{shown.Replace('\\', '/')}";
                relativeDirectory = shown; files = entries; filesFailure = failure;
                Guard(() => ShowFileRows(reveal: moved));
            });
        });
    }

    /// <summary>
    /// 读取一个目录；目录无法读取时依次改读它的上级目录，直到项目根目录。在后台线程调用。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="directory">要读取的目录，项目相对路径。</param>
    /// <returns>
    /// 实际读取的目录（项目相对路径）、其中的条目，以及失败原因：成功时为 <see langword="null"/>；
    /// 连项目根目录也无法读取时目录为空字符串、条目为空、原因为最后一次的错误信息。
    /// </returns>
    private static (string Directory, FileEntry[] Entries, string? Failure) ListNearestDirectory(string root, string directory)
    {
        while (true)
        {
            try { return (directory, FileWorkspace.ListDirectory(root, directory).ToArray(), null); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (directory.Length == 0) return ("", [], error.Message);
                directory = Path.GetDirectoryName(directory) ?? "";
            }
        }
    }

    /// <summary>
    /// 在代码查看器中打开当前项目内的文件并跳到指定行，然后显示查看器、聚焦编辑器，并在 <c>files</c> 分区中高亮该文件。
    /// </summary>
    /// <remarks>
    /// 没有当前项目，或用户在处理未保存修改时选择取消，则什么也不做。
    /// </remarks>
    /// <param name="relativePath">项目相对路径。</param>
    /// <param name="line">要跳到的行号，从 1 开始；超出范围时取最近的有效行。</param>
    private void OpenFile(string relativePath, int line = 1)
    {
        if (projectRoot is null || !CanLeaveEditor()) return;
        editor.Open(projectRoot, relativePath, true); editor.GoToLine(line);
        ShowEditor(); UpdateEditorTitle(); ShowFileRows(reveal: false); editor.View.SetFocus();
    }

    /// <summary>
    /// 保存代码查看器中的文件（F5），并更新标题与状态栏。
    /// </summary>
    /// <exception cref="FileConflictException">文件在打开后已被外部修改或删除，内容未写入。</exception>
    private void SaveFile()
    {
        if (editor.Path is null) { status.Text = "No file is open. Click a file under files in the sidebar first."; return; }
        editor.Save(); UpdateEditorTitle(); status.Text = "Saved " + editor.Path;
    }

    /// <summary>
    /// 按当前文件与修改状态更新代码查看器的外框标题：项目相对路径（有未保存修改时前缀 <c>*</c>）与可用按键；
    /// 没有打开文件时提示在侧栏中单击文件。
    /// </summary>
    private void UpdateEditorTitle() => editorFrame.Title = (editor.IsDirty ? "* " : "") +
        (editor.Path is null || projectRoot is null ? "CODE  ·  click a file under files"
            : Path.GetRelativePath(projectRoot, editor.Path).Replace('\\', '/')) + "  ·  F5 Save  ·  Esc Terminal  ·  F4 Hide";

    /// <summary>
    /// 显示或隐藏终端下方的代码查看器（F4、<c>Ctrl+b e</c>）。
    /// </summary>
    /// <remarks>
    /// 隐藏时把焦点交给活动终端；显示时聚焦编辑器，还没有打开文件时聚焦侧栏的 <c>files</c> 分区以便选择文件。
    /// </remarks>
    private void ToggleEditor()
    {
        editorVisible = !editorVisible;
        ArrangePanels();
        if (!editorVisible) FocusActiveTerminal();
        else if (editor.Path is not null) editor.View.SetFocus();
        else FocusSidebar(fileSection);
    }

    /// <summary>
    /// 显示终端下方的代码查看器，不改变焦点。
    /// </summary>
    private void ShowEditor() { editorVisible = true; ArrangePanels(); }
}
