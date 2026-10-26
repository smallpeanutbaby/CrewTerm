namespace CrewTerm.Core.Projects;

/// <summary>
/// 侧边栏中的一个项目。
/// </summary>
/// <param name="Id">稳定的项目标识（GUID，无连字符）。</param>
/// <param name="Name">显示名称，默认取目录名。</param>
/// <param name="Path">规范化后的项目根目录绝对路径。</param>
public sealed record ProjectEntry(string Id, string Name, string Path);
