using System.Diagnostics;

namespace CrewTerm.Workspace.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crewterm-workspace-tests", Guid.NewGuid().ToString("N"));
    public TempWorkspace() => Directory.CreateDirectory(Root);
    public string Write(string relative, string text)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
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
    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}
