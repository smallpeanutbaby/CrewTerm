namespace CrewTerm.Workspace.Checkpoints;

/// <summary>
/// 一次恢复预览：展示给用户的差异，以及执行恢复所需的目标内容与“预览时的磁盘状态”。
/// </summary>
/// <remarks>
/// 预览对象记录了生成预览时每个文件的哈希。<see cref="CheckpointStore.RestoreAsync"/>
/// 会再次比对，若用户查看预览后文件又发生变化，则拒绝恢复，确保用户确认的就是实际执行的。
/// </remarks>
public sealed class CheckpointPreview
{
    /// <summary>
    /// 项目根目录。
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// 被预览的检查点。
    /// </summary>
    public CheckpointInfo Checkpoint { get; }

    /// <summary>
    /// 恢复将产生的全部变化；为空表示当前状态与检查点一致。
    /// </summary>
    public IReadOnlyList<CheckpointChange> Changes { get; }

    /// <summary>
    /// 检查点中保存的全部文件内容（相对路径 → 字节）。
    /// </summary>
    internal IReadOnlyDictionary<string, byte[]> TargetFiles { get; }

    /// <summary>
    /// 生成预览时项目中每个文件的 SHA-256（相对路径 → 哈希）。
    /// </summary>
    internal IReadOnlyDictionary<string, string> ExpectedHashes { get; }

    /// <summary>
    /// 由 <see cref="CheckpointStore.PreviewAsync"/> 创建预览。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="checkpoint">被预览的检查点。</param>
    /// <param name="changes">恢复将产生的变化。</param>
    /// <param name="targetFiles">检查点中的文件内容。</param>
    /// <param name="expectedHashes">生成预览时的磁盘文件哈希。</param>
    internal CheckpointPreview(string root, CheckpointInfo checkpoint, IReadOnlyList<CheckpointChange> changes,
        IReadOnlyDictionary<string, byte[]> targetFiles, IReadOnlyDictionary<string, string> expectedHashes)
    {
        Root = root; Checkpoint = checkpoint; Changes = changes;
        TargetFiles = targetFiles; ExpectedHashes = expectedHashes;
    }
}
