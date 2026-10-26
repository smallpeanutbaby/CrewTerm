using System.Text.Json.Serialization;

namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 插件清单中声明的一条进程调用：参数按字面传递，执行时间与保留的输出都有上限。
/// </summary>
/// <remarks>
/// 直接启动进程（不经过 Shell），因此参数中的 <c>&amp;</c>、<c>|</c>、<c>%VAR%</c>、<c>$VAR</c> 等不会被解释。
/// 可以用 <see cref="Windows"/>、<see cref="Linux"/>、<see cref="Osx"/> 为不同操作系统替换可执行文件或参数。
/// 取值范围由 <see cref="LocalPluginCatalog"/> 在登记时校验。
/// </remarks>
/// <param name="Id">插件内的命令标识（不区分大小写唯一），以字母或数字开头，只含字母、数字、<c>.</c>、<c>_</c>、<c>-</c>，最长 80 个字符。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Executable">默认的本机可执行文件名（按 PATH 查找）、绝对路径，或相对插件目录的路径（含 <c>/</c> 或 <c>\</c>，不得逃出插件目录）；
/// 不能带引号、换行或 NUL。</param>
/// <param name="Arguments">默认的字面参数，至多 128 个，不做任何变量插值；为 <see langword="null"/> 时不传参数。</param>
/// <param name="TimeoutSeconds">总超时秒数（1–300），包括等待执行槽位的时间。</param>
/// <param name="MaxOutputCharacters">保留的标准输出与标准错误合并字符数上限（1–1048576），超出时只保留末尾。</param>
/// <param name="WorkingDirectory">相对插件目录的工作目录（不得逃出插件目录）；为 <see langword="null"/> 时使用插件目录本身。</param>
/// <param name="Windows">在 Windows 上替换的调用；为 <see langword="null"/> 时使用默认调用。</param>
/// <param name="Linux">在 Linux 上替换的调用；为 <see langword="null"/> 时使用默认调用。</param>
/// <param name="Osx">在 macOS 上替换的调用；为 <see langword="null"/> 时使用默认调用。</param>
public sealed record LocalPluginCommand(string Id, string Name, string Executable, string[]? Arguments = null,
    int TimeoutSeconds = 30, int MaxOutputCharacters = 65536, string? WorkingDirectory = null,
    LocalPluginPlatformCommand? Windows = null, LocalPluginPlatformCommand? Linux = null, LocalPluginPlatformCommand? Osx = null)
{
    /// <summary>
    /// 当前操作系统对应的替代调用；没有声明时为 <see langword="null"/>。
    /// </summary>
    private LocalPluginPlatformCommand? CurrentPlatform =>
        OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? Osx : OperatingSystem.IsLinux() ? Linux : null;

    /// <summary>
    /// 当前操作系统上实际启动的可执行文件：有替代且给出了可执行文件时用替代值，否则用默认值。
    /// </summary>
    [JsonIgnore]
    public string EffectiveExecutable => CurrentPlatform?.Executable ?? Executable;

    /// <summary>
    /// 当前操作系统上实际传递的参数：有替代且给出了参数时用替代值，否则用默认值。
    /// </summary>
    [JsonIgnore]
    public string[]? EffectiveArguments => CurrentPlatform?.Arguments ?? Arguments;
}
