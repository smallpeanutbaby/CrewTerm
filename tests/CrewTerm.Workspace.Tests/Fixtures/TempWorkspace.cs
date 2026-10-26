using System.Diagnostics;

namespace CrewTerm.Workspace.Tests.Fixtures;

/// <summary>
/// 测试用的临时工作目录：在系统临时目录下创建一个随机命名的文件夹，提供写文件、运行真实 <c>git</c> 命令
/// 与初始化测试仓库的辅助方法，释放时连同其中的只读文件一起删除。
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    /// <summary>
    /// 临时目录的绝对路径（位于 <c>%TEMP%/crewterm-workspace-tests</c> 下，以随机 GUID 命名）。
    /// </summary>
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crewterm-workspace-tests", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 创建临时目录。
    /// </summary>
    public TempWorkspace() => Directory.CreateDirectory(Root);
    /// <summary>
    /// 在临时目录中写入文本文件，必要时先创建其上级目录；已存在的文件会被覆盖。
    /// </summary>
    /// <param name="relative">相对 <see cref="Root"/> 的文件路径。</param>
    /// <param name="text">要写入的文本内容。</param>
    /// <returns>写入文件的完整路径。</returns>
    public string Write(string relative, string text)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    /// <summary>
    /// 以 <see cref="Root"/> 为工作目录运行一次真实的 <c>git</c> 命令（不经过 Shell，参数逐个传递），并同时读取标准输出与标准错误。
    /// </summary>
    /// <param name="args">传给 <c>git</c> 的参数。</param>
    /// <returns>命令的标准输出文本。</returns>
    /// <exception cref="InvalidOperationException">命令以非零退出码结束；异常消息为其标准错误输出。</exception>
    public async Task<string> Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        return await output;
    }
    /// <summary>
    /// 把临时目录初始化为默认分支为 <c>main</c> 的 Git 仓库：配置仓库级的提交者姓名与邮箱、关闭 <c>core.autocrlf</c>，
    /// 然后写入 <c>initial.txt</c> 并完成第一次提交。
    /// </summary>
    /// <returns>初始化与首次提交完成时结束的任务。</returns>
    public async Task InitRepo()
    {
        await Git("init", "--initial-branch=main");
        await Git("config", "user.name", "CrewTerm Test");
        await Git("config", "user.email", "crewterm-test@example.invalid");
        await Git("config", "core.autocrlf", "false");
        Write("initial.txt", "initial\n");
        await Git("add", ".");
        await Git("commit", "-m", "initial");
    }
    /// <summary>
    /// 删除临时目录：先把其中所有文件的属性重置为普通（Git 对象文件是只读的，否则无法删除），再递归删除；目录不存在时什么也不做。
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
