namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 一次插件命令或事件钩子的执行结果（输出有上限），执行失败也以结果而不是异常的形式返回。
/// </summary>
/// <remarks>
/// <see cref="LocalPluginRunner"/> 把插件未登记、已禁用、命令未声明、进程无法启动等错误都转换为
/// <see cref="LocalPluginResult.Success"/> 为 <see langword="false"/> 的结果，调用方无需捕获异常。
/// </remarks>
/// <param name="PluginId">插件标识；目录读取失败时为 <c>catalog</c>。</param>
/// <param name="CommandId">命令标识；目录读取失败时为事件名。</param>
/// <param name="Success">仅当未取消、未超时且进程以退出码 0 结束时为 <see langword="true"/>。</param>
/// <param name="ExitCode">进程退出码；进程未启动或未能退出时为 <see langword="null"/>。</param>
/// <param name="Output">标准输出与标准错误按到达顺序合并后的文本，只保留末尾至多
/// <see cref="LocalPluginCommand.MaxOutputCharacters"/> 个字符。</param>
/// <param name="Error">失败说明；成功时为 <see langword="null"/>。</param>
/// <param name="TimedOut">是否因超过 <see cref="LocalPluginCommand.TimeoutSeconds"/> 而终止。</param>
/// <param name="OutputTruncated">输出是否超过保留上限而丢弃了开头部分。</param>
/// <param name="Cancelled">是否由调用方取消（与超时互斥）。</param>
public sealed record LocalPluginResult(string PluginId, string CommandId, bool Success, int? ExitCode,
    string Output, string? Error, bool TimedOut = false, bool OutputTruncated = false, bool Cancelled = false);
