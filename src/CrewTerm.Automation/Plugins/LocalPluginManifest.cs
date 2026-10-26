namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 从用户选择的本地目录中读取的插件清单（<see cref="LocalPluginCatalog.ManifestFileName"/>）。
/// </summary>
/// <remarks>
/// 反序列化时属性名不区分大小写，出现未知属性即拒绝；文件不得超过 128 KiB。
/// </remarks>
/// <param name="Id">稳定的插件标识，以字母或数字开头，只含字母、数字、<c>.</c>、<c>_</c>、<c>-</c>，最长 80 个字符。</param>
/// <param name="Name">显示名称，必填。</param>
/// <param name="Version">版本标签，必填，仅用于显示。</param>
/// <param name="Commands">声明的本地进程命令，1–64 条，命令标识不区分大小写唯一。</param>
/// <param name="Hooks">事件到命令的映射，至多 64 条；为 <see langword="null"/> 表示没有事件钩子。</param>
public sealed record LocalPluginManifest(string Id, string Name, string Version,
    IReadOnlyList<LocalPluginCommand> Commands, IReadOnlyList<LocalPluginHook>? Hooks = null);
