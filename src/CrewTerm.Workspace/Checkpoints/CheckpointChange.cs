namespace CrewTerm.Workspace.Checkpoints;

/// <summary>
/// 恢复预览中的一项文件变化。
/// </summary>
/// <param name="RelativePath">以 <c>/</c> 分隔的项目相对路径。</param>
/// <param name="Kind">
/// <c>Modified</c>：内容将被还原；<c>Added</c>：当前新增的文件，恢复时会被删除；
/// <c>Missing</c>：当前已不存在的文件，恢复时会被重建。
/// </param>
/// <param name="BeforeText">当前磁盘内容的预览（最多 8 KiB，二进制文件只显示大小）。</param>
/// <param name="AfterText">检查点内容的预览。</param>
public sealed record CheckpointChange(string RelativePath, string Kind, string BeforeText, string AfterText);
