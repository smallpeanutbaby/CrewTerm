using System.Diagnostics;

namespace CrewTerm.Terminal.Tests;

public sealed class ConPtySessionTests
{
    [Fact]
    public async Task RealCommandProducesTerminalOutputAndExitCode()
    {
        using var session = ConPtySession.Start("echo", "cmd.exe /d /q /c \"echo crewterm-native & exit /b 7\"", Path.GetTempPath(), 80, 24);
        try { await Until(() => session.HasExited && session.Screen.GetText().Contains("crewterm-native")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY output timeout. Exited={session.HasExited}; code={session.ExitCode}; screen={session.Screen.GetText()}"); }
        Assert.Equal(7, session.ExitCode);
        Assert.Equal("echo", session.Id);
    }

    [Fact]
    public async Task InteractiveInputAndResizeReachRealShell()
    {
        using var session = ConPtySession.Start("interactive", "cmd.exe /d /q", Path.GetTempPath(), 80, 24);
        session.Resize(100, 30);
        session.Write("echo crewterm-input\r");
        try { await Until(() => session.Screen.GetText().Contains("crewterm-input")); }
        catch (OperationCanceledException) { Assert.Fail($"PTY input timeout. Exited={session.HasExited}; code={session.ExitCode}; screen={session.Screen.GetText()}"); }
        Assert.Equal(100, session.Screen.Snapshot().Columns);
        Assert.Equal(30, session.Screen.Snapshot().Rows);
        session.Write("exit 0\r");
        await Until(() => session.HasExited);
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task DisposeTerminatesTheOwnedProcessAndIsIdempotent()
    {
        var session = ConPtySession.Start("dispose", "cmd.exe /d /q", Path.GetTempPath(), 80, 24);
        using var process = Process.GetProcessById(session.ProcessId);
        session.Dispose();
        session.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.HasExited);
        Assert.Throws<ObjectDisposedException>(() => session.Write("echo late\r"));
    }

    [Fact]
    public void MissingWorkingDirectoryIsRejectedBeforeLaunching()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ConPtySession.Start("missing", "cmd.exe", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), 80, 24));
    }

    [Fact]
    public void MissingExecutableIsReported()
    {
        Assert.Throws<System.ComponentModel.Win32Exception>(() => ConPtySession.Start("missing", "crewterm-does-not-exist-xyz.exe", Path.GetTempPath(), 80, 24));
    }

    [Fact]
    public async Task CmdShimWithSpacesAndArgumentsRunsInsidePseudoconsole()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var shim = Path.Combine(root, "agent shim.cmd");
            File.WriteAllText(shim, "@echo off\r\necho shim-%~1\r\nexit /b 3\r\n");
            using var session = ConPtySession.Start("shim", $"\"{shim}\" \"two words\"", root, 80, 24);
            await Until(() => session.HasExited && session.Screen.GetText().Contains("shim-two words"));
            Assert.Equal(3, session.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BareCommandFindsCmdShimInWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "crewterm-test-shim.cmd"), "@echo off\r\necho shim-discovered\r\n");
            using var session = ConPtySession.Start("shim", "crewterm-test-shim", root, 80, 24);
            await Until(() => session.HasExited && session.Screen.GetText().Contains("shim-discovered"));
            Assert.Equal(0, session.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsCmdShimTakesPriorityOverExtensionlessUnixShim(bool absolutePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm npm shim " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var extensionless = Path.Combine(root, "crewterm-test-agent");
            File.WriteAllText(extensionless, "#!/bin/sh\necho wrong-unix-shim\n");
            File.WriteAllText(extensionless + ".cmd", "@echo off\r\necho windows-shim-%~1\r\nexit /b 9\r\n");
            var command = absolutePath ? $"\"{extensionless}\" check" : "crewterm-test-agent check";
            using var session = ConPtySession.Start("shim-priority", command, root, 80, 24);
            await Until(() => session.HasExited && session.Screen.GetText().Contains("windows-shim-check"));
            Assert.Equal(9, session.ExitCode);
            Assert.DoesNotContain("wrong-unix-shim", session.Screen.GetText());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposeTerminatesDescendantProcessesOwnedByTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm child " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pidFile = Path.Combine(root, "child.pid");
            var script = "$child = Start-Process cmd.exe -ArgumentList '/d /q /c ping -t 127.0.0.1' -WindowStyle Hidden -PassThru; "
                + "$child.Id | Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "'; Start-Sleep -Seconds 120";
            using var session = ConPtySession.Start("children", $"powershell.exe -NoLogo -NoProfile -Command \"{script}\"", root, 80, 24);
            await Until(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            using var child = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile)).Trim()));
            Assert.False(child.HasExited);
            session.Dispose();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
        }
        finally { Directory.Delete(root, true); }
    }
}
