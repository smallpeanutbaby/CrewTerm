namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 插件命令在某个操作系统上的替代调用：只替换给出的字段，其余沿用命令的默认值。
/// </summary>
/// <remarks>
/// 写在命令的 <c>windows</c>、<c>linux</c> 或 <c>osx</c> 字段中，例如默认使用 <c>pwsh</c>，
/// 在 Windows 上改用系统自带的 <c>powershell</c>。校验规则与默认调用相同。
/// </remarks>
/// <param name="Executable">替代的可执行文件；为 <see langword="null"/> 时沿用默认值。</param>
/// <param name="Arguments">替代的参数列表；为 <see langword="null"/> 时沿用默认值。</param>
public sealed record LocalPluginPlatformCommand(string? Executable = null, string[]? Arguments = null);
