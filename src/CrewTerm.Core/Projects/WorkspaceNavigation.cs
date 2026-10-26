using CrewTerm.Core.IO;

namespace CrewTerm.Core.Projects;

/// <summary>
/// 与 UI 框架无关的导航状态模型：当前项目、当前会话，以及切换时对未保存编辑的保护规则。
/// </summary>
/// <remarks>
/// 规则：切换到<b>另一个项目</b>（按 <see cref="FileSystemPath"/> 规则比较）时，若编辑器有未保存内容，
/// 必须先得到用户“保存或放弃”的明确决定；同一项目内切换会话不影响编辑器。
/// 工作台窗口用它保存当前项目并判定是否需要询问用户，测试直接验证同一套规则。
/// 本类不是线程安全的，应只在 UI 线程使用。
/// </remarks>
public sealed class WorkspaceNavigation
{
    /// <summary>
    /// 当前项目根目录；尚未选择时为 <see langword="null"/>。
    /// </summary>
    public string? ActiveProjectPath { get; private set; }

    /// <summary>
    /// 当前终端会话标识；尚未选择时为 <see langword="null"/>。
    /// </summary>
    public string? ActiveSessionId { get; private set; }

    /// <summary>
    /// 编辑器是否有未保存内容，由 UI 层在每次切换前同步。
    /// </summary>
    public bool EditorDirty { get; set; }

    /// <summary>
    /// 判断切换到指定项目前是否必须先让用户处理未保存的编辑。
    /// </summary>
    /// <param name="projectPath">目标项目根目录。</param>
    /// <returns>编辑器有未保存内容且目标是另一个项目时为 <see langword="true"/>。</returns>
    public bool RequiresDiscard(string projectPath)
        => EditorDirty && !FileSystemPath.AreSame(ActiveProjectPath, projectPath);

    /// <summary>
    /// 尝试选择一个会话及其所属项目。
    /// </summary>
    /// <param name="sessionId">会话标识；<see langword="null"/> 表示只切换项目。</param>
    /// <param name="projectPath">会话绑定的项目根目录。</param>
    /// <param name="discardConfirmed">用户是否已确认保存或放弃未保存的编辑。</param>
    /// <returns>切换成功返回 <see langword="true"/>；因未保存编辑被阻止时返回 <see langword="false"/>，状态保持不变。</returns>
    public bool TrySelectSession(string? sessionId, string projectPath, bool discardConfirmed = false)
    {
        if (RequiresDiscard(projectPath) && !discardConfirmed) return false;
        if (!FileSystemPath.AreSame(ActiveProjectPath, projectPath)) EditorDirty = false;
        ActiveSessionId = sessionId;
        ActiveProjectPath = projectPath;
        return true;
    }
}
