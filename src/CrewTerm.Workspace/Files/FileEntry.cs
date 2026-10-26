namespace CrewTerm.Workspace.Files;

/// <summary>
/// 文件区中的一个条目。
/// </summary>
/// <param name="Name">文件或目录名。</param>
/// <param name="RelativePath">以 <c>/</c> 分隔的项目相对路径。</param>
/// <param name="IsDirectory">是否为目录。</param>
public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory);
