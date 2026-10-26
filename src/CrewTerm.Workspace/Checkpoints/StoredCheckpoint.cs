namespace CrewTerm.Workspace.Checkpoints;

/// <summary>
/// 检查点快照文件（<c>&lt;标识&gt;.snapshot.json</c>）的存储格式；文件内容以 Base64 保存在 JSON 中。
/// </summary>
/// <param name="Info">检查点元数据。</param>
/// <param name="Files">项目相对路径（<c>/</c> 分隔）到文件完整字节的映射。</param>
internal sealed record StoredCheckpoint(CheckpointInfo Info, Dictionary<string, byte[]> Files);
