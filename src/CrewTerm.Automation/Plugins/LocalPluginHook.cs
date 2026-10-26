namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 事件钩子：指定事件发生时运行插件中已声明的某条命令。
/// </summary>
/// <remarks>
/// 只有已启用插件的钩子会被 <see cref="LocalPluginRunner.RunEventAsync"/> 执行；事件名按序数精确匹配（区分大小写）。
/// </remarks>
/// <param name="Event">事件名，例如 daemon 发出的 <c>agent.state</c>、<c>session.exited</c>；
/// 以字母或数字开头，只含字母、数字、<c>.</c>、<c>_</c>、<c>-</c>，最长 80 个字符。</param>
/// <param name="CommandId">要运行的命令标识，必须是同一清单中已声明的命令（不区分大小写）。</param>
public sealed record LocalPluginHook(string Event, string CommandId);
