namespace CrewTerm.Core.Settings;

/// <summary>
/// 可复用的后台命令模板。
/// </summary>
/// <param name="Name">模板名称（不区分大小写唯一）。</param>
/// <param name="Command">交给系统 Shell 执行的命令文本。</param>
/// <param name="WorkingDirectory">保存模板时所在的项目目录（运行时使用当前选中的项目）。</param>
public sealed record CommandTemplate(string Name, string Command, string WorkingDirectory);
