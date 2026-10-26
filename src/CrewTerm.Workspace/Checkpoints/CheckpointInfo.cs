namespace CrewTerm.Workspace.Checkpoints;

/// <summary>
/// 检查点元数据，单独保存在 <c>{id}.meta.json</c> 中；列出检查点时只读取它，不读取完整快照。
/// </summary>
/// <param name="Id">检查点标识（GUID，无连字符）。</param>
/// <param name="Label">用户填写的名称。</param>
/// <param name="Root">捕获时的项目根目录。</param>
/// <param name="CreatedAt">捕获时间（UTC）。</param>
/// <param name="FileCount">捕获的文件数。</param>
/// <param name="TotalBytes">捕获的文件总字节数。</param>
public sealed record CheckpointInfo(string Id, string Label, string Root, DateTimeOffset CreatedAt, int FileCount, long TotalBytes);
