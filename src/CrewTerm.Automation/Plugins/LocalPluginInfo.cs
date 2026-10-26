namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 一个已登记的本地插件：源目录、登记时的清单快照以及当前启用状态。
/// </summary>
/// <remarks>
/// 保存在 <c>local-plugins.json</c> 中。清单在登记时复制一份快照，之后修改源目录中的清单文件不会自动生效，
/// 需要移除后重新登记。
/// </remarks>
/// <param name="Id">插件标识，必须与 <see cref="LocalPluginManifest.Id"/> 完全相同。</param>
/// <param name="Name">显示名称（来自清单）。</param>
/// <param name="Version">版本标签（来自清单，仅用于显示）。</param>
/// <param name="SourceDirectory">原始本地插件目录的完整路径；移除登记时绝不会删除该目录。</param>
/// <param name="Enabled">是否允许执行它的命令和事件钩子。</param>
/// <param name="Manifest">已校验的清单快照。</param>
public sealed record LocalPluginInfo(string Id, string Name, string Version, string SourceDirectory,
    bool Enabled, LocalPluginManifest Manifest);
