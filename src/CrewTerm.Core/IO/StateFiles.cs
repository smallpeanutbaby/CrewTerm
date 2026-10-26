using CrewTerm.Core.Projects;
using CrewTerm.Core.Settings;

namespace CrewTerm.Core.IO;

/// <summary>
/// 状态目录（默认 <c>%LOCALAPPDATA%\CrewTerm</c>）中各文件的统一命名。
/// </summary>
/// <remarks>
/// UI 客户端、后台 daemon 和各业务层共享同一个状态目录。文件名集中定义在这里，
/// 防止同一文件名在多个程序集里以字符串字面量重复出现、改名时漏改。
/// 使用 <c>--state-dir</c> 可以创建互相独立的工作台实例。
/// </remarks>
public static class StateFiles
{
    /// <summary>
    /// 侧边栏项目列表（<see cref="ProjectCatalog"/>）。
    /// </summary>
    public const string Projects = "projects.json";

    /// <summary>
    /// 布局、上次选择、并发上限与命令模板（<see cref="WorkspaceSettingsStore"/>）。
    /// </summary>
    public const string Workspace = "workspace.json";

    /// <summary>
    /// 用户自定义的 Agent 配置（内置配置不落盘）。
    /// </summary>
    public const string AgentProfiles = "agent-profiles.json";

    /// <summary>
    /// 流水线模板。
    /// </summary>
    public const string PipelineTemplates = "pipeline-templates.json";

    /// <summary>
    /// 已完成后台任务的历史记录。
    /// </summary>
    public const string TaskHistory = "task-history.json";

    /// <summary>
    /// daemon 在会话变化时写入的实时会话列表（旧版本的会话记录格式）；
    /// 只在 <see cref="NativeSessions"/> 尚不存在时被读取一次，用于迁移到原生会话目录。
    /// </summary>
    public const string SavedSessions = "saved-sessions.json";

    /// <summary>
    /// 终端会话的持久化目录（启动命令、Provider 与原生会话标识），会话进程退出后仍保留。
    /// </summary>
    public const string NativeSessions = "native-sessions.json";

    /// <summary>
    /// Agent 语义状态、通知与会话间消息。
    /// </summary>
    public const string AgentActivity = "agent-activity.json";

    /// <summary>
    /// 已登记的本地插件及其启用状态。
    /// </summary>
    public const string LocalPlugins = "local-plugins.json";

    /// <summary>
    /// 为单次 Provider 启动生成的 hook 配置目录，每个会话一个 <c>&lt;会话标识&gt;.json</c>。
    /// </summary>
    public const string ProviderHooks = "provider-hooks";

    /// <summary>
    /// 文件检查点目录，每个项目一个以路径哈希命名的子目录。
    /// </summary>
    public const string Checkpoints = "checkpoints";

    /// <summary>
    /// daemon 独占锁文件，保证同一状态目录只运行一个 daemon。
    /// </summary>
    public const string ServerLock = "server.lock";

    /// <summary>
    /// Linux / macOS 上 daemon 监听的 Unix 域套接字（Windows 使用命名管道，没有这个文件）。
    /// </summary>
    public const string ServerSocket = "server.sock";

    /// <summary>
    /// daemon 启动或运行失败时追加写入的错误日志。
    /// </summary>
    public const string ServerErrorLog = "server-error.log";

    /// <summary>
    /// 未指定 <c>--state-dir</c> 时使用的默认状态目录。
    /// </summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrewTerm");

    /// <summary>
    /// 确保状态目录存在；Linux / macOS 上新建的目录只允许当前用户访问（<c>0700</c>）。
    /// </summary>
    /// <remarks>
    /// 状态目录中有命令输出、源码检查点与会话记录。Windows 的 <c>%LOCALAPPDATA%</c> 已由用户级 ACL 保护；
    /// Unix 上按 umask 新建的目录通常是 <c>0755</c>，其他本地用户可以读取，因此显式收紧。已存在的目录保持原有权限。
    /// </remarks>
    /// <param name="stateDirectory">状态目录，可以是相对路径。</param>
    public static void EnsureDirectory(string stateDirectory)
    {
        var path = Path.GetFullPath(stateDirectory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// 组合状态目录与状态文件名，返回绝对路径。
    /// </summary>
    /// <param name="stateDirectory">状态目录，可以是相对路径。</param>
    /// <param name="fileName">本类中定义的文件名常量。</param>
    /// <returns>状态文件的绝对路径。</returns>
    public static string PathOf(string stateDirectory, string fileName) =>
        Path.Combine(Path.GetFullPath(stateDirectory), fileName);
}
