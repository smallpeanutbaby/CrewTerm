using System.Diagnostics;
using System.Text;
using CrewTerm.Core.IO;
using CrewTerm.Workspace.Files;

namespace CrewTerm.Workspace.Git;

/// <summary>
/// 通过 Git CLI 读取改动、查看 diff、暂存文件和管理 worktree。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>参数一律通过 <see cref="ProcessStartInfo.ArgumentList"/> 传递，文件名不会被 Shell 解释。</description></item>
/// <item><description>始终使用 <c>--literal-pathspecs</c>，文件名中的 <c>*</c>、<c>[</c> 等不会被当作通配符。</description></item>
/// <item><description>不执行任何破坏性操作：不删除分支、不强制删除脏 worktree、不 reset、不 push。</description></item>
/// <item><description>每条命令最长 60 秒，超时或取消时结束整个 Git 进程树。</description></item>
/// </list>
/// </remarks>
public sealed class GitService
{
    /// <summary>
    /// 单条 Git 命令的最长运行时间（60 秒）；超时后结束 Git 进程树并抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 获取包含指定目录的仓库根目录。
    /// </summary>
    /// <param name="root">仓库内任意目录。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>仓库根目录绝对路径。</returns>
    public async Task<string> GetRepositoryRootAsync(string root, CancellationToken cancellationToken = default)
        => System.IO.Path.GetFullPath((await Run(root, ["rev-parse", "--show-toplevel"], cancellationToken)).Output.Trim());

    /// <summary>
    /// 读取项目目录（可以是仓库的子目录）范围内的改动，包括所有未跟踪文件。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>改动列表，路径相对于项目根目录。</returns>
    public async Task<IReadOnlyList<GitChange>> GetChangesAsync(string root, CancellationToken cancellationToken = default)
    {
        root = WorkspacePath.Root(root);
        var repository = await GetRepositoryRootAsync(root, cancellationToken);
        var result = await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."], cancellationToken);
        // -z 格式：每项为 "XY 路径\0"；重命名/复制时紧跟一项 "原路径\0"。路径相对于仓库根目录。
        var tokens = result.Output.Split('\0');
        var changes = new List<GitChange>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length < 4) continue;
            var status = token[..2];
            var path = WorkspacePath.Relative(root, System.IO.Path.Combine(repository, token[3..]));
            string? original = null;
            if ((status.Contains('R') || status.Contains('C')) && index + 1 < tokens.Length)
                original = WorkspacePath.Relative(root, System.IO.Path.Combine(repository, tokens[++index]));
            changes.Add(new GitChange(path, status, original));
        }
        return changes;
    }

    /// <summary>
    /// 获取单个文件的 diff；未跟踪文件显示为相对空文件的新增。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">项目相对路径。</param>
    /// <param name="staged">为 <see langword="true"/> 时显示暂存区相对 HEAD 的 diff。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>统一 diff 文本；没有差异时为空字符串。</returns>
    public async Task<string> GetDiffAsync(string root, string relativePath, bool staged = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var path = WorkspacePath.Resolve(root, relativePath, allowMissing: true);
        if (!staged && (await GetChangesAsync(root, cancellationToken)).Any(c => FileSystemPath.AreSame(c.RelativePath, relativePath.Replace('\\', '/')) && c.IsUntracked))
            // --no-index 在有差异时以退出码 1 结束，这不是错误。
            return (await Run(root, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--", "/dev/null", path], cancellationToken, allowDifference: true)).Output;
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv" };
        if (staged) arguments.Add("--cached");
        arguments.AddRange(["--", relativePath]);
        return (await Run(root, arguments, cancellationToken)).Output;
    }

    /// <summary>
    /// 暂存单个文件（包括删除）。
    /// </summary>
    /// <param name="root">项目根目录。</param>
    /// <param name="relativePath">项目相对路径；越界路径会被拒绝。</param>
    /// <param name="cancellationToken">取消操作。</param>
    public async Task StageAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        WorkspacePath.Resolve(root, relativePath, allowMissing: true);
        await Run(root, ["add", "--", relativePath], cancellationToken);
    }

    /// <summary>
    /// 列出仓库的所有 worktree（包括主工作区）。
    /// </summary>
    /// <param name="root">仓库内任意目录。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>worktree 列表。</returns>
    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string root, CancellationToken cancellationToken = default)
    {
        var output = (await Run(root, ["worktree", "list", "--porcelain", "-z"], cancellationToken)).Output;
        // 每条记录由若干 "键 值\0" 组成，以一个空项（连续两个 \0）结束。
        var result = new List<GitWorktree>();
        string? path = null;
        var head = "";
        var branch = "";
        var bare = false;
        var detached = false;
        foreach (var token in output.Split('\0'))
        {
            if (token.StartsWith("worktree ", StringComparison.Ordinal)) path = System.IO.Path.GetFullPath(token[9..]);
            else if (token.StartsWith("HEAD ", StringComparison.Ordinal)) head = token[5..];
            else if (token.StartsWith("branch ", StringComparison.Ordinal)) branch = token[7..].Replace("refs/heads/", "", StringComparison.Ordinal);
            else if (token == "bare") bare = true;
            else if (token == "detached") detached = true;
            else if (token.Length == 0 && path is not null)
            {
                result.Add(new GitWorktree(path, branch, head, bare, detached));
                path = null; head = branch = ""; bare = detached = false;
            }
        }
        return result;
    }

    /// <summary>
    /// 在新目录创建 worktree：分支已存在则检出该分支，否则从当前 HEAD 新建分支。
    /// </summary>
    /// <param name="root">仓库内任意目录。</param>
    /// <param name="path">新 worktree 目录（不能已存在内容）。</param>
    /// <param name="branch">分支名，必须通过 <c>git check-ref-format --branch</c> 校验。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>新建的 worktree。</returns>
    public async Task<GitWorktree> CreateWorktreeAsync(string root, string path, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        await Run(root, ["check-ref-format", "--branch", branch], cancellationToken);
        // 去掉末尾分隔符，否则与 Git 输出的路径比较时会匹配失败。
        path = FileSystemPath.Normalize(path);
        var exists = await Run(root, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken, allowDifference: true);
        if (exists.ExitCode == 0) await Run(root, ["worktree", "add", "--", path, branch], cancellationToken);
        else await Run(root, ["worktree", "add", "-b", branch, "--", path, "HEAD"], cancellationToken);
        return (await ListWorktreesAsync(root, cancellationToken)).Single(w => FileSystemPath.AreSame(w.Path, path));
    }

    /// <summary>
    /// 删除一个干净的 worktree；有未提交改动时 Git 会拒绝，分支保留。
    /// </summary>
    /// <param name="root">仓库内任意目录。</param>
    /// <param name="path">要删除的 worktree 目录，必须属于本仓库。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <exception cref="GitOperationException">路径不是本仓库的 worktree，或 worktree 不干净。</exception>
    public async Task RemoveWorktreeAsync(string root, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = FileSystemPath.Normalize(path);
        var worktrees = await ListWorktreesAsync(root, cancellationToken);
        if (!worktrees.Any(worktree => FileSystemPath.AreSame(worktree.Path, fullPath)))
            throw new GitOperationException("The selected path is not a worktree of this repository.");
        // 不传 --force：脏 worktree 由 Git 拒绝删除，保护用户与 Agent 的未提交工作。
        await Run(root, ["worktree", "remove", "--", fullPath], cancellationToken);
    }

    /// <summary>
    /// 在项目目录中运行一条 Git 命令并收集输出。
    /// </summary>
    /// <param name="root">工作目录（项目根目录）。</param>
    /// <param name="arguments">Git 子命令与参数。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <param name="allowDifference">把退出码 1 视为成功（<c>diff --no-index</c>、<c>show-ref</c> 等）。</param>
    /// <returns>标准输出的完整文本（UTF-8 解码）与退出码。</returns>
    /// <exception cref="GitOperationException">无法启动 Git，或 Git 以非零（且不被允许的）退出码结束；消息包含标准错误输出。</exception>
    /// <exception cref="OperationCanceledException">调用方取消或超过 <see cref="CommandTimeout"/>；此时 Git 进程树已被结束。</exception>
    private static async Task<GitResult> Run(string root, IEnumerable<string> arguments, CancellationToken cancellationToken, bool allowDifference = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root = WorkspacePath.Root(root);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // 禁止 Git 弹出凭据提示，否则无界面的子进程会一直等待输入。
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // status 等只读命令默认会顺手刷新索引并短暂持有 index.lock；关闭可选锁，
        // 避免与同一仓库中正在执行 git 的 Agent 发生 "index.lock exists" 冲突。
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.ArgumentList.Add("--literal-pathspecs");
        start.ArgumentList.Add("-c");
        // 输出原始 UTF-8 文件名，而不是 "\344\270\255" 形式的八进制转义。
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error) { throw new GitOperationException($"Cannot start Git. Install Git and add it to PATH. {error.Message}"); }
        // 同时读取 stdout 与 stderr，避免任一管道写满导致 Git 阻塞。
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorOutput = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await output;
        var errorText = await errorOutput;
        if (process.ExitCode != 0 && !(allowDifference && process.ExitCode == 1))
            throw new GitOperationException($"Git failed ({process.ExitCode}): {errorText.Trim()}", process.ExitCode);
        return new GitResult(text, process.ExitCode);
    }
}
