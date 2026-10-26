namespace CrewTerm.Automation.Tasks;

/// <summary>
/// 用户明确提交的一条非交互式 Shell 命令（构建、测试等）。
/// </summary>
/// <param name="Name">在任务列表和历史中显示的名称。</param>
/// <param name="Command">交给系统 Shell（Windows 为 cmd.exe）执行的命令文本。</param>
/// <param name="WorkingDirectory">命令的工作目录，必须已存在。</param>
public sealed record CommandTask(string Name, string Command, string WorkingDirectory);
