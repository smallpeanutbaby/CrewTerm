using System.Runtime.Versioning;
using CrewTerm.Core.IO;

namespace CrewTerm.Core.Tests.IO;

/// <summary>
/// 覆盖 <see cref="StateFiles"/> 的状态目录创建与路径组合。
/// </summary>
public sealed class StateFilesTests
{
    /// <summary>
    /// 验证：新建的状态目录（含不存在的上级目录）可以被创建；相对路径按当前目录转换为绝对路径后再与文件名组合。
    /// </summary>
    [Fact]
    public void EnsureDirectoryCreatesMissingParentsAndPathOfIsAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm state " + Guid.NewGuid().ToString("N"));
        try
        {
            var state = Path.Combine(root, "nested", "state");
            StateFiles.EnsureDirectory(state);
            Assert.True(Directory.Exists(state));
            StateFiles.EnsureDirectory(state);
            Assert.True(Path.IsPathFullyQualified(StateFiles.PathOf("relative-state", StateFiles.ServerLock)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>
    /// 验证（Linux / macOS）：新建的状态目录权限为 <c>0700</c>，其他本地用户无法读取；已存在的目录保持原有权限。
    /// </summary>
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void NewStateDirectoriesArePrivateOnUnix()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm state " + Guid.NewGuid().ToString("N"));
        try
        {
            StateFiles.EnsureDirectory(root);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
            const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(root, shared);
            StateFiles.EnsureDirectory(root);
            Assert.Equal(shared, File.GetUnixFileMode(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
