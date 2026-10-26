namespace CrewTerm.Automation.Agents;

/// <summary>
/// 一个可启动的 Agent / CLI 配置。
/// </summary>
/// <param name="Id">配置标识，只允许字母、数字、<c>-</c> 和 <c>_</c>。</param>
/// <param name="Name">在选择器和侧边栏中显示的名称。</param>
/// <param name="Executable">可执行文件名或完整路径（不带引号）。</param>
/// <param name="Arguments">附加在可执行文件后的参数文本，原样传递。</param>
/// <param name="IsBuiltIn">是否为 CrewTerm 内置配置。</param>
public sealed record AgentProfile(string Id, string Name, string Executable, string Arguments = "", bool IsBuiltIn = false)
{
    /// <summary>
    /// 交给终端启动的完整命令行。可执行文件路径含空格或 cmd 特殊字符时自动加引号。
    /// </summary>
    public string CommandLine
    {
        get
        {
            var executable = Executable.IndexOfAny([' ', '\t', '&', '(', ')', '^']) >= 0
                ? $"\"{Executable}\"" : Executable;
            return string.IsNullOrWhiteSpace(Arguments) ? executable : executable + " " + Arguments;
        }
    }
}
