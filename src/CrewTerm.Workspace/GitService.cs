using System.Diagnostics;
using System.Text;

namespace CrewTerm.Workspace;

public sealed record GitChange(string RelativePath, string Status, string? OriginalPath = null)
{
    public bool IsUntracked => Status == "??";
    public bool IsStaged => Status.Length > 0 && Status[0] != ' ' && Status[0] != '?';
    public override string ToString() => $"{Status} {RelativePath}";
}

public sealed record GitWorktree(string Path, string Branch, string Head, bool IsBare, bool IsDetached);

public sealed class GitOperationException(string message, int exitCode = -1) : IOException(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed class GitService
{
    public async Task<string> GetRepositoryRootAsync(string root, CancellationToken cancellationToken = default)
        => System.IO.Path.GetFullPath((await Run(root, ["rev-parse", "--show-toplevel"], cancellationToken)).Output.Trim());

    public async Task<IReadOnlyList<GitChange>> GetChangesAsync(string root, CancellationToken cancellationToken = default)
    {
        root = WorkspacePath.Root(root);
        var repository = await GetRepositoryRootAsync(root, cancellationToken);
        var result = await Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."], cancellationToken);
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

    public async Task<string> GetDiffAsync(string root, string relativePath, bool staged = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var path = WorkspacePath.Resolve(root, relativePath, allowMissing: true);
        if (!staged && (await GetChangesAsync(root, cancellationToken)).Any(c => c.RelativePath.Equals(relativePath.Replace('\\', '/'), WorkspacePath.Comparison) && c.IsUntracked))
            return (await Run(root, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--", "/dev/null", path], cancellationToken, allowDifference: true)).Output;
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv" };
        if (staged) arguments.Add("--cached");
        arguments.AddRange(["--", relativePath]);
        return (await Run(root, arguments, cancellationToken)).Output;
    }

    public async Task StageAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        WorkspacePath.Resolve(root, relativePath, allowMissing: true);
        await Run(root, ["add", "--", relativePath], cancellationToken);
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string root, CancellationToken cancellationToken = default)
    {
        var output = (await Run(root, ["worktree", "list", "--porcelain", "-z"], cancellationToken)).Output;
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

    public async Task<GitWorktree> CreateWorktreeAsync(string root, string path, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        await Run(root, ["check-ref-format", "--branch", branch], cancellationToken);
        path = System.IO.Path.GetFullPath(path);
        var exists = await Run(root, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken, allowDifference: true);
        if (exists.ExitCode == 0) await Run(root, ["worktree", "add", "--", path, branch], cancellationToken);
        else await Run(root, ["worktree", "add", "-b", branch, "--", path, "HEAD"], cancellationToken);
        return (await ListWorktreesAsync(root, cancellationToken)).Single(w => w.Path.Equals(path, WorkspacePath.Comparison));
    }

    public async Task RemoveWorktreeAsync(string root, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var worktrees = await ListWorktreesAsync(root, cancellationToken);
        if (!worktrees.Any(worktree => worktree.Path.Equals(fullPath, WorkspacePath.Comparison)))
            throw new GitOperationException("The selected path is not a worktree of this repository.");
        await Run(root, ["worktree", "remove", "--", fullPath], cancellationToken);
    }

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
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("--literal-pathspecs");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error) { throw new GitOperationException($"Cannot start Git. Install Git and add it to PATH. {error.Message}"); }
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

    private sealed record GitResult(string Output, int ExitCode);
}
