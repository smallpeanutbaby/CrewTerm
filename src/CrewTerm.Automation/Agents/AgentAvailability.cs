namespace CrewTerm.Automation.Agents;

/// <summary>
/// 配置及其在本机的安装情况。
/// </summary>
/// <param name="Profile">Agent 配置。</param>
/// <param name="ExecutablePath">在 PATH 中找到的可执行文件绝对路径；未安装时为 <see langword="null"/>。</param>
public sealed record AgentAvailability(AgentProfile Profile, string? ExecutablePath)
{
    /// <summary>
    /// 是否已在本机找到可执行文件。
    /// </summary>
    public bool IsAvailable => ExecutablePath is not null;
}
