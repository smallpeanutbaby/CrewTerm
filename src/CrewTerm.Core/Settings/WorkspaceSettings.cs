using CrewTerm.Core.IO;
using CrewTerm.Core.Layouts;

namespace CrewTerm.Core.Settings;

/// <summary>
/// 工作区布局与偏好设置，保存在 <see cref="StateFiles.Workspace"/>。
/// </summary>
public sealed class WorkspaceSettings
{
    /// <summary>
    /// 上次选中的项目标识。
    /// </summary>
    public string? LastProjectId { get; set; }

    /// <summary>
    /// 上次左侧终端显示的会话标识，重新连接时恢复。
    /// </summary>
    public string? LastSessionId { get; set; }

    /// <summary>
    /// 是否开启双终端分屏。
    /// </summary>
    public bool SplitTerminals { get; set; }

    /// <summary>
    /// daemon 同时执行的后台任务上限（1–32），daemon 重启后生效。
    /// </summary>
    public int MaximumConcurrentTasks { get; set; } = 4;

    /// <summary>
    /// 用户保存的命令模板。
    /// </summary>
    public List<CommandTemplate> Templates { get; set; } = [];
    /// <summary>
    /// 持久化的终端标签页与分屏树，按项目标识索引；未选择项目时 UI 使用键 <c>_none</c>。
    /// </summary>
    /// <remarks>
    /// 加载与保存时每个值都会经过 <see cref="TerminalLayout.Validate"/> 校验；值不能为 <see langword="null"/>。
    /// </remarks>
    public Dictionary<string, TerminalLayout> ProjectLayouts { get; set; } = [];
}
