using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrewTerm.Core.IO;
using CrewTerm.Core.Security;

namespace CrewTerm.Automation.Plugins;

/// <summary>
/// 本地插件登记表：登记用户选择的本地插件目录中的清单；绝不下载插件，也绝不删除源目录。
/// </summary>
/// <remarks>
/// 登记信息保存在 <c>local-plugins.json</c>（<see cref="StateFiles.LocalPlugins"/>）中，
/// 修改在跨进程互斥锁内完成“读取 → 校验 → 修改 → 写回”。每次读取都会重新校验全部登记，
/// 文件损坏或内容无效时抛出 <see cref="InvalidDataException"/> 并保留原文件。
/// 新登记的插件默认处于禁用状态。
/// </remarks>
public sealed class LocalPluginCatalog
{
    /// <summary>
    /// 插件目录中必须存在的清单文件名。
    /// </summary>
    public const string ManifestFileName = "crewterm-plugin.json";

    /// <summary>
    /// 登记文件 <c>local-plugins.json</c> 的绝对路径。
    /// </summary>
    private readonly string path;

    /// <summary>
    /// 读取清单的 JSON 选项：属性名不区分大小写；出现未知属性即拒绝，避免拼错的字段被静默忽略。
    /// </summary>
    private static readonly JsonSerializerOptions ManifestJson = new()
        { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// 打开本地插件登记表，并立即校验现有登记文件。
    /// </summary>
    /// <param name="stateDirectory">CrewTerm 状态目录。</param>
    /// <exception cref="InvalidDataException">现有登记文件损坏或内容无效。</exception>
    public LocalPluginCatalog(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        path = StateFiles.PathOf(stateDirectory, StateFiles.LocalPlugins);
        _ = List();
    }

    /// <summary>
    /// 列出已登记的插件（按登记顺序）及其当前启用状态。
    /// </summary>
    /// <returns>登记信息数组。</returns>
    /// <exception cref="InvalidDataException">登记文件损坏或内容无效。</exception>
    public IReadOnlyList<LocalPluginInfo> List()
    {
        var plugins = AtomicJsonStore.Read(path, () => new List<LocalPluginInfo>());
        ValidateAll(plugins);
        return plugins.ToArray();
    }

    /// <summary>
    /// 查找一个已登记的插件。
    /// </summary>
    /// <param name="id">插件标识，不区分大小写比较。</param>
    /// <returns>找到的登记信息；未登记时为 <see langword="null"/>。</returns>
    /// <exception cref="InvalidDataException">登记文件损坏或内容无效。</exception>
    public LocalPluginInfo? Get(string id) => List().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 读取并校验本地目录中的清单，然后登记该插件；标识重复（不区分大小写）时拒绝。
    /// </summary>
    /// <remarks>
    /// 只记录源目录路径和清单快照，不复制、不移动插件文件，也不执行任何插件代码。
    /// </remarks>
    /// <param name="sourceDirectory">包含 <see cref="ManifestFileName"/> 的现有源目录，会被转换为绝对路径。</param>
    /// <param name="enabled">登记后是否立即启用；默认禁用。</param>
    /// <returns>新登记的插件信息。</returns>
    /// <exception cref="DirectoryNotFoundException">源目录不存在。</exception>
    /// <exception cref="FileNotFoundException">源目录中没有清单文件。</exception>
    /// <exception cref="InvalidDataException">清单超过 128 KiB、不是有效 JSON、含未知属性或内容不合规，或现有登记文件无效。</exception>
    /// <exception cref="InvalidOperationException">已经登记过相同标识的插件。</exception>
    public LocalPluginInfo Install(string sourceDirectory, bool enabled = false)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        var manifestPath = Path.Combine(source, ManifestFileName);
        if (new FileInfo(manifestPath).Length > 131072) throw new InvalidDataException("Plugin manifest exceeds 128 KiB.");
        LocalPluginManifest manifest;
        try { manifest = JsonSerializer.Deserialize<LocalPluginManifest>(File.ReadAllText(manifestPath), ManifestJson) ?? throw new InvalidDataException("Plugin manifest is null."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid plugin manifest JSON.", error); }
        ValidateManifest(manifest, source);
        var plugin = new LocalPluginInfo(manifest.Id, manifest.Name, manifest.Version, source, enabled, manifest);
        AtomicJsonStore.Update(path, () => new List<LocalPluginInfo>(), plugins =>
        {
            ValidateAll(plugins);
            if (plugins.Any(p => p.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Plugin ID is already installed: " + plugin.Id);
            plugins.Add(plugin);
            return true;
        });
        return plugin;
    }

    /// <summary>
    /// 启用或禁用一个已登记的插件；禁用后它的命令和事件钩子都不会再执行。
    /// </summary>
    /// <param name="id">插件标识，不区分大小写比较。</param>
    /// <param name="enabled">期望的启用状态。</param>
    /// <returns>更新后的登记信息（即使状态未变也会写回文件）。</returns>
    /// <exception cref="KeyNotFoundException">插件未登记。</exception>
    /// <exception cref="InvalidDataException">登记文件损坏或内容无效。</exception>
    public LocalPluginInfo SetEnabled(string id, bool enabled)
    {
        LocalPluginInfo? changed = null;
        AtomicJsonStore.Update(path, () => new List<LocalPluginInfo>(), plugins =>
        {
            ValidateAll(plugins);
            var index = plugins.FindIndex(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException("Plugin not installed: " + id);
            changed = plugins[index] with { Enabled = enabled };
            plugins[index] = changed;
            return true;
        });
        return changed!;
    }

    /// <summary>
    /// 只删除登记信息，保留本地插件目录中的全部文件。
    /// </summary>
    /// <param name="id">插件标识，不区分大小写比较。</param>
    /// <returns>找到并删除时返回 <see langword="true"/>；未登记时返回 <see langword="false"/> 且不修改文件。</returns>
    /// <exception cref="InvalidDataException">登记文件损坏或内容无效。</exception>
    public bool Remove(string id) => AtomicJsonStore.Update(path, () => new List<LocalPluginInfo>(), plugins =>
    {
        ValidateAll(plugins);
        return plugins.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
    });

    /// <summary>
    /// 把插件内的相对路径解析为绝对路径，并确保结果仍位于插件目录之内。
    /// </summary>
    /// <remarks>
    /// 只做文本层面的路径比较（<see cref="FileSystemPath.IsWithin"/>），不解析符号链接或目录联接；
    /// 插件目录内指向外部的链接不会被发现。
    /// </remarks>
    /// <param name="source">插件源目录。</param>
    /// <param name="relative">相对插件目录的路径，不能是带根的路径。</param>
    /// <returns>解析后的绝对路径。</returns>
    /// <exception cref="InvalidDataException">路径带根，或经 <c>..</c> 等解析后逃出插件目录。</exception>
    internal static string ResolveRelative(string source, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Plugin working directories and relative program paths must stay inside the plugin directory.");
        var root = FileSystemPath.Normalize(source);
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!FileSystemPath.IsWithin(root, resolved))
            throw new InvalidDataException("Plugin relative path escapes its source directory.");
        return resolved;
    }

    /// <summary>
    /// 校验从登记文件读到的全部登记：记录与清单非空、源目录为完整路径、清单合规、登记标识与清单标识一致、标识不重复。
    /// </summary>
    /// <param name="plugins">从文件读取的登记列表。</param>
    /// <exception cref="InvalidDataException">任一登记无效；原文件保持不变。</exception>
    private static void ValidateAll(List<LocalPluginInfo> plugins)
    {
        foreach (var plugin in plugins)
        {
            if (plugin is null || plugin.Manifest is null || !Path.IsPathFullyQualified(plugin.SourceDirectory))
                throw new InvalidDataException("Invalid local plugin registry; original bytes preserved.");
            ValidateManifest(plugin.Manifest, plugin.SourceDirectory);
            if (plugin.Id != plugin.Manifest.Id) throw new InvalidDataException("Plugin registry ID differs from manifest ID.");
        }
        if (plugins.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plugins.Count)
            throw new InvalidDataException("Duplicate plugin registrations.");
    }

    /// <summary>
    /// 校验一份清单的全部约束。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>插件、命令、事件的标识都必须符合 <see cref="Identifier"/> 的规则；名称与版本必填。</description></item>
    /// <item><description>命令 1–64 条，标识不区分大小写唯一；可执行文件不能含 NUL、换行或引号。</description></item>
    /// <item><description>超时 1–300 秒，保留输出 1–1048576 个字符，参数至多 128 个且不含 NUL。</description></item>
    /// <item><description>工作目录以及含目录分隔符的相对可执行文件路径必须位于插件目录之内；
    /// <c>windows</c>、<c>linux</c>、<c>osx</c> 替代调用与默认调用遵循相同的规则。</description></item>
    /// <item><description>事件钩子至多 64 条，且只能引用已声明的命令。</description></item>
    /// </list>
    /// </remarks>
    /// <param name="manifest">要校验的清单。</param>
    /// <param name="source">插件源目录，用于检查相对路径。</param>
    /// <exception cref="InvalidDataException">清单违反任一约束。</exception>
    private static void ValidateManifest(LocalPluginManifest manifest, string source)
    {
        Identifier(manifest.Id, "Plugin ID");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Plugin name and version are required.");
        if (DisplayText.ContainsControl(manifest.Name, allowLineBreaks: false) || DisplayText.ContainsControl(manifest.Version, allowLineBreaks: false)
            || manifest.Commands?.Any(command => command?.Name is { } name && DisplayText.ContainsControl(name, allowLineBreaks: false)) == true)
            throw new InvalidDataException("Plugin and command display names must not contain control characters.");
        if (manifest.Commands is null || manifest.Commands.Count is < 1 or > 64)
            throw new InvalidDataException("Declare between 1 and 64 plugin commands.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in manifest.Commands)
        {
            if (command is null) throw new InvalidDataException("Plugin command cannot be null.");
            Identifier(command.Id, "Command ID");
            if (!ids.Add(command.Id)) throw new InvalidDataException("Duplicate plugin command ID.");
            if (string.IsNullOrWhiteSpace(command.Name)) throw new InvalidDataException("A command needs a name.");
            if (command.TimeoutSeconds is < 1 or > 300 || command.MaxOutputCharacters is < 1 or > 1048576)
                throw new InvalidDataException("Command timeout must be 1–300 seconds and retained output 1–1048576 characters.");
            if (command.WorkingDirectory is not null) _ = ResolveRelative(source, command.WorkingDirectory);
            ValidateInvocation(command.Executable, command.Arguments, source);
            foreach (var platform in new[] { command.Windows, command.Linux, command.Osx })
                if (platform is not null) ValidateInvocation(platform.Executable ?? command.Executable, platform.Arguments, source);
        }
        if (manifest.Hooks?.Count > 64) throw new InvalidDataException("At most 64 event hooks are supported per plugin.");
        foreach (var hook in manifest.Hooks ?? [])
        {
            if (hook is null) throw new InvalidDataException("Event hook cannot be null.");
            Identifier(hook.Event, "Event name");
            if (!ids.Contains(hook.CommandId)) throw new InvalidDataException("Event hook refers to an undeclared command.");
        }
    }

    /// <summary>
    /// 校验一次进程调用（默认调用或某个操作系统的替代调用）。
    /// </summary>
    /// <param name="executable">可执行文件：不能为空，不能带引号、换行或 NUL；含目录分隔符的相对路径必须位于插件目录之内。</param>
    /// <param name="arguments">字面参数：至多 128 个，不能有 <see langword="null"/> 元素或含 NUL。</param>
    /// <param name="source">插件源目录，用于检查相对路径。</param>
    /// <exception cref="InvalidDataException">违反任一约束。</exception>
    private static void ValidateInvocation(string executable, string[]? arguments, string source)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new InvalidDataException("A command needs an unquoted executable.");
        if (arguments is { Length: > 128 } || arguments?.Any(a => a is null || a.Contains('\0')) == true)
            throw new InvalidDataException("Arguments must be at most 128 literal strings without NUL.");
        if (!Path.IsPathRooted(executable) && executable.IndexOfAny(['/', '\\']) >= 0)
            _ = ResolveRelative(source, executable);
    }

    /// <summary>
    /// 校验标识：非空，最长 80 个字符，以 ASCII 字母或数字开头，其后只含字母、数字、<c>_</c>、<c>.</c>、<c>-</c>。
    /// </summary>
    /// <param name="value">要校验的标识。</param>
    /// <param name="label">出错时写入异常消息的字段名，例如 <c>Plugin ID</c>。</param>
    /// <exception cref="InvalidDataException">标识不符合规则。</exception>
    private static void Identifier(string value, string label)
    {
        // 用 \z 而不是 $：$ 允许末尾多一个换行。
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_.-]*\z"))
            throw new InvalidDataException(label + " must use letters, digits, dots, underscores or hyphens.");
    }
}
