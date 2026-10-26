namespace CrewTerm.Workspace.Documents;

/// <summary>
/// 磁盘上的文件在编辑器打开后又被其他程序（通常是 Agent）修改，保存被拒绝。
/// </summary>
/// <param name="path">发生冲突的文件路径。</param>
public sealed class FileConflictException(string path)
    : IOException($"File changed outside the editor: {path}. Reload or compare before saving.")
{
    /// <summary>
    /// 发生冲突的文件路径。
    /// </summary>
    public string FilePath { get; } = path;
}
