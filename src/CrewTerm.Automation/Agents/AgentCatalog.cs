using CrewTerm.Core.IO;

namespace CrewTerm.Automation.Agents;

/// <summary>
/// Agent 配置目录：内置配置（Claude Code、Codex、OpenCode、Gemini、Copilot、Shell）加上用户自定义配置。
/// </summary>
/// <remarks>
/// <para>只有自定义配置会写入 <see cref="StateFiles.AgentProfiles"/>；自定义配置的标识与内置配置相同时覆盖内置配置。
/// 未提供状态目录时只保存在内存中（用于测试）。</para>
/// <para>本类只描述“如何启动”，不安装、不登录、也不保存任何模型 API Key；认证与计费由各 CLI 自己负责。</para>
/// </remarks>
public sealed class AgentCatalog
{
    /// <summary>
    /// 自定义配置文件 <c>agent-profiles.json</c> 的绝对路径；为 <see langword="null"/> 时处于纯内存模式。
    /// </summary>
    private readonly string? path;

    /// <summary>
    /// 内存模式下保护 <see cref="memoryProfiles"/> 的进程内锁；落盘模式改用跨进程互斥锁，不使用它。
    /// </summary>
    private readonly object memoryGate = new();

    /// <summary>
    /// 内存模式下的自定义配置；修改时先复制再整体替换，读取方拿到的是副本。
    /// </summary>
    private List<AgentProfile> memoryProfiles = [];

    /// <summary>
    /// 创建配置目录，并立即校验现有的自定义配置文件。
    /// </summary>
    /// <param name="stateDirectory">状态目录；为 <see langword="null"/> 时只在内存中保存自定义配置。</param>
    /// <exception cref="InvalidDataException">现有配置文件损坏。</exception>
    public AgentCatalog(string? stateDirectory = null)
    {
        if (stateDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
            StateFiles.EnsureDirectory(stateDirectory);
            path = StateFiles.PathOf(stateDirectory, StateFiles.AgentProfiles);
        }
        _ = Profiles;
    }

    /// <summary>
    /// 合并后的全部配置：内置配置在前，自定义配置覆盖或追加在后。
    /// </summary>
    public IReadOnlyList<AgentProfile> Profiles
    {
        get
        {
            var profiles = BuiltIns().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var custom in Load()) profiles[custom.Id] = custom with { IsBuiltIn = false };
            return profiles.Values.ToArray();
        }
    }

    /// <summary>
    /// 在 PATH 中查找每个配置的可执行文件（包括 Windows 的 <c>.cmd</c> 启动脚本）。
    /// </summary>
    /// <returns>每个配置及其可执行文件路径；未安装时路径为 <see langword="null"/>。</returns>
    public IReadOnlyList<AgentAvailability> Discover()
        => Profiles.Select(profile => new AgentAvailability(profile, ExecutableLocator.Find(profile.Executable))).ToArray();

    /// <summary>
    /// 新增或替换（按标识，不区分大小写）一个自定义配置。
    /// </summary>
    /// <param name="profile">要保存的配置；<see cref="AgentProfile.IsBuiltIn"/> 会被强制设为 <see langword="false"/>。</param>
    /// <exception cref="ArgumentException">配置字段无效。</exception>
    public void Upsert(AgentProfile profile)
    {
        Validate(profile);
        Mutate(profiles =>
        {
            profiles.RemoveAll(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            profiles.Add(profile with { IsBuiltIn = false });
            return true;
        });
    }

    /// <summary>
    /// 删除一个自定义配置；内置配置无法删除。
    /// </summary>
    /// <param name="id">配置标识。</param>
    /// <returns>找到并删除时返回 <see langword="true"/>。</returns>
    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = false;
        Mutate(profiles => removed = profiles.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    /// <summary>
    /// 读取并校验自定义配置。
    /// </summary>
    /// <returns>自定义配置列表的副本。</returns>
    /// <exception cref="InvalidDataException">配置文件损坏。</exception>
    private List<AgentProfile> Load()
    {
        if (path is null)
            lock (memoryGate) return memoryProfiles.ToList();
        return Validated(AtomicJsonStore.Read(path, () => new List<AgentProfile>()));
    }

    /// <summary>
    /// 在锁内修改自定义配置：落盘模式使用跨进程互斥锁，内存模式使用进程内锁。
    /// </summary>
    /// <param name="mutate">就地修改列表；返回 <see langword="false"/> 表示无需保存。</param>
    private void Mutate(Func<List<AgentProfile>, bool> mutate)
    {
        if (path is not null)
        {
            AtomicJsonStore.Update(path, () => new List<AgentProfile>(), profiles => mutate(Validated(profiles)));
            return;
        }
        lock (memoryGate)
        {
            var profiles = memoryProfiles.ToList();
            if (mutate(profiles)) memoryProfiles = profiles;
        }
    }

    /// <summary>
    /// 校验从磁盘读到的全部配置，把字段错误转换为“文件损坏”异常。
    /// </summary>
    /// <param name="profiles">从文件读取的配置。</param>
    /// <returns>原列表（便于链式调用）。</returns>
    /// <exception cref="InvalidDataException">存在无效配置。</exception>
    private List<AgentProfile> Validated(List<AgentProfile> profiles)
    {
        try { foreach (var profile in profiles) Validate(profile); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid agent profile data; original file preserved: {path}", error);
        }
        return profiles;
    }

    /// <summary>
    /// 校验配置：标识只允许字母、数字、连字符和下划线；可执行文件字段不能包含引号或换行，
    /// 防止在拼接命令行时注入额外参数。
    /// </summary>
    /// <param name="profile">要校验的配置。</param>
    /// <exception cref="ArgumentException">配置无效。</exception>
    private static void Validate(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Executable);
        if (profile.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Profile IDs may contain letters, digits, hyphens and underscores.", nameof(profile));
        if (profile.Executable.Contains('"') || profile.Executable.Contains('\n') || profile.Executable.Contains('\r'))
            throw new ArgumentException("The executable field must contain an unquoted executable name or path.", nameof(profile));
    }

    /// <summary>
    /// 内置配置。Shell 在 Windows 上优先使用 PowerShell 7（pwsh），否则回退到系统自带的 Windows PowerShell。
    /// </summary>
    /// <returns>内置配置序列。</returns>
    private static IEnumerable<AgentProfile> BuiltIns()
    {
        yield return new("claude", "Claude Code", "claude", IsBuiltIn: true);
        yield return new("codex", "Codex", "codex", IsBuiltIn: true);
        yield return new("opencode", "OpenCode", "opencode", IsBuiltIn: true);
        yield return new("gemini", "Gemini CLI", "gemini", IsBuiltIn: true);
        yield return new("copilot", "GitHub Copilot CLI", "copilot", IsBuiltIn: true);
        var shell = OperatingSystem.IsWindows()
            ? ExecutableLocator.Find("pwsh") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        yield return new("shell", "Shell", shell, IsBuiltIn: true);
    }
}
