using CrewTerm.App.Server;
using CrewTerm.Terminal;

namespace CrewTerm.App.Tests;

public sealed class TuiSmokeTests
{
    [Fact]
    public async Task ConcurrentLayoutChangeCanDetachWithoutOverwritingOtherClient()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var first = ConPtySession.Start("layout-first", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => first.Screen.GetText().Contains("F10 Detach"), first, "first client");
            using var second = ConPtySession.Start("layout-second", $"\"{executable}\" --state-dir \"{state}\"", root, 150, 45);
            await WaitUntil(() => second.Screen.GetText().Contains("F10 Detach"), second, "second client");
            first.Write("\u001b[19~"); await Task.Delay(250); first.Write("\u001b[21~");
            await WaitUntil(() => first.HasExited, first, "first layout saved");
            Assert.True(new CrewTerm.Core.WorkspaceSettingsStore(state).Load().SplitTerminals);
            second.Write("\u001b[21~");
            await WaitUntil(() => second.Screen.GetText().Contains("Layout could not be saved"), second, "concurrent layout choice");
            second.Write("\r");
            await WaitUntil(() => second.HasExited, second, "detach without overwriting layout");
            Assert.Equal(0, second.ExitCode);
            Assert.True(new CrewTerm.Core.WorkspaceSettingsStore(state).Load().SplitTerminals);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CommandPaletteFiltersImmediatelyAndNewTerminalWizardStartsRealShell()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = ConPtySession.Start("wizard-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "wizard workspace");
            terminal.Write("\u001b[20~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Project: Add folder"), terminal, "command palette");
            terminal.Write("checkpoints");
            await WaitUntil(() => !terminal.Screen.GetText().Contains("Project: Add folder") && terminal.Screen.GetText().Contains("Checkpoints: Capture"), terminal, "palette filter");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Capture current project files"), terminal, "chosen palette action");
            terminal.Write("\u001b"); await Task.Delay(250); terminal.Write("\u001bOR");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Custom command"), terminal, "agent chooser");
            terminal.Write("custom command"); await Task.Delay(250); terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Custom terminal"), terminal, "custom command prompt");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Terminal name"), terminal, "terminal naming prompt");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Microsoft Windows"), terminal, "embedded shell from wizard");
            terminal.Write("echo WIZARD_STARTED\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("WIZARD_STARTED"), terminal, "interactive wizard shell");
            var live = (await new DaemonClient(state).CallAsync(new("list"))).Sessions!;
            Assert.Single(live);
            Assert.False(live[0].HasExited);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "wizard UI detach");
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SelectingSessionInAnotherProjectPersistsMatchingFileWorkspace()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(state, "project-a");
        var b = Path.Combine(state, "project-b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "ONLY_IN_B.cs"), "// Project B");
        var client = new DaemonClient(state);
        try
        {
            // Keep the outer test PTY job until cleanup. Disposing it intentionally kills
            // all descendants; UI process exit itself must preserve the daemon.
            using var terminal = ConPtySession.Start("binding-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{a}\"", root, 150, 45);
                await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "ready workspace A");
                var session = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: b, Name: "Agent B"));
                await WaitUntil(() => terminal.Screen.GetText().Contains("Agent B"), terminal, "B session in sidebar");
                terminal.Write("\u001b5"); await Task.Delay(250); terminal.Write("\r");
                await WaitUntil(() => terminal.Screen.GetText().Contains("ONLY_IN_B.cs"), terminal, "B file workspace");
                terminal.Write("\u001b[21~");
                await WaitUntil(() => terminal.HasExited, terminal, "detach B");
                var settings = new CrewTerm.Core.WorkspaceSettingsStore(state).Load();
                var project = new CrewTerm.Core.ProjectCatalog(state).Projects.Single(p => p.Id == settings.LastProjectId);
                Assert.Equal(b, project.Path);
                Assert.Equal(session.SessionId, settings.LastSessionId);
            using var reconnected = ConPtySession.Start("reconnected", $"\"{executable}\" --state-dir \"{state}\"", root, 150, 45);
            await WaitUntil(() => reconnected.Screen.GetText().Contains("ONLY_IN_B.cs") && reconnected.Screen.GetText().Contains("Agent B"), reconnected, "consistent reconnect");
            reconnected.Write("\u001b[21~");
            await WaitUntil(() => reconnected.HasExited, reconnected, "reconnected detach");
            using var explicitlySelected = ConPtySession.Start("explicit-project", $"\"{executable}\" --state-dir \"{state}\" --project \"{a}\"", root, 150, 45);
            await WaitUntil(() => explicitlySelected.Screen.GetText().Contains("Agent B"), explicitlySelected, "connected server session list");
            explicitlySelected.Write("\u001b1"); await Task.Delay(250); explicitlySelected.Write("x");
            await WaitUntil(() => explicitlySelected.Screen.GetText().Contains("F3 starts a terminal."), explicitlySelected, "explicit project must not bind saved session from another root");
            explicitlySelected.Write("\u001b[21~");
            await WaitUntil(() => explicitlySelected.HasExited, explicitlySelected, "explicit project detach");
        }
        finally
        {
            try { await client.CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task TaskMenuRunsCommandAndShowsRealOutput()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = ConPtySession.Start("task-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "task workspace");
            terminal.Write("\u001b[18~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Run command"), terminal, "task menu");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Run background command"), terminal, "command prompt");
            terminal.Write("\u0001echo UI_TASK_MARKER");
            await WaitUntil(() => terminal.Screen.GetText().Contains("UI_TASK_MARKER"), terminal, "entered command");
            terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("TASK OUTPUT") && terminal.Screen.GetText().Contains("EXIT 0"), terminal, "task completion");
            Assert.Contains("UI_TASK_MARKER", terminal.Screen.GetText());
            var tasks = (await new DaemonClient(state).CallAsync(new("task-list"))).Tasks!;
            Assert.Contains(tasks, task => task.Completed && task.ExitCode == 0 && task.Command == "echo UI_TASK_MARKER");
            terminal.Write("\u001b"); await Task.Delay(250); terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "task UI detach");
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task FileEditingSavesAndFrameworkQuitCannotDiscardDirtyBuffer()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(state, "project");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "hello.cs");
        File.WriteAllText(file, "// INITIAL_TEXT\n");
        try
        {
            using var terminal = ConPtySession.Start("editor-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{project}\"", root, 150, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("hello.cs"), terminal, "file browser");
            terminal.Write("\u001b4"); await Task.Delay(250); terminal.Write("\r");
            await WaitUntil(() => terminal.Screen.GetText().Contains("INITIAL_TEXT"), terminal, "opened code");
            terminal.Write("SAVED_EDIT"); await Task.Delay(250); terminal.Write("\u001b[15~");
            await WaitUntil(() => File.ReadAllText(file).Contains("SAVED_EDIT"), terminal, "saved editor text");
            terminal.Write("UNSAVED_EDIT"); await Task.Delay(250);
            terminal.Write("\u001b4"); await Task.Delay(250); terminal.Write("\u001b");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Unsaved changes"), terminal, "guard for framework Escape quit");
            Assert.False(terminal.HasExited);
            Assert.DoesNotContain("UNSAVED_EDIT", File.ReadAllText(file));
            terminal.Write("\u001b"); await Task.Delay(300);
            Assert.False(terminal.HasExited);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.Screen.GetText().Contains("Unsaved changes"), terminal, "detach guard after cancelling quit");
            // Save must have an explicit keyboard accelerator; Enter defaults to Cancel.
            terminal.Write("\u001bs");
            await WaitUntil(() => terminal.HasExited, terminal, "save and detach");
            Assert.Contains("UNSAVED_EDIT", File.ReadAllText(file));
            Assert.Equal(0, terminal.ExitCode);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SplitTerminalsRouteInputIndependentlyAndSurviveDetach()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var client = new DaemonClient(state);
        try
        {
            using var terminal = ConPtySession.Start("split-ui", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 180, 50);
            await WaitUntil(() => terminal.Screen.GetText().Contains("F10 Detach"), terminal, "ready workspace");
            var first = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: root, Name: "Agent A"));
            var second = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: root, Name: "Agent B"));
            await client.CallAsync(new("input", first.SessionId, Text: "echo CREW_A_READY\r"));
            await client.CallAsync(new("input", second.SessionId, Text: "echo CREW_B_READY\r"));
            terminal.Write("\u001b[19~"); // F8 split
            await WaitUntil(() => terminal.Screen.GetText().Contains("CREW_A_READY") && terminal.Screen.GetText().Contains("CREW_B_READY"), terminal, "two embedded terminals");
            // Alt chords are separate physical events, not an arbitrary combined ESC sequence.
            terminal.Write("\u001b1");
            await Task.Delay(250);
            terminal.Write("echo PRIMARY_ONLY\r");
            await WaitForSessionText(client, first.SessionId!, "PRIMARY_ONLY", terminal);
            terminal.Write("\u001b2");
            await Task.Delay(250);
            terminal.Write("echo SECONDARY_ONLY\r");
            await WaitForSessionText(client, second.SessionId!, "SECONDARY_ONLY", terminal);
            Assert.DoesNotContain("SECONDARY_ONLY", (await client.CallAsync(new("screen", first.SessionId))).Text);
            Assert.DoesNotContain("PRIMARY_ONLY", (await client.CallAsync(new("screen", second.SessionId))).Text);
            Directory.CreateDirectory(Path.Combine(root, "work"));
            File.WriteAllText(Path.Combine(root, "work", "tui-split-snapshot.json"), System.Text.Json.JsonSerializer.Serialize(terminal.Screen.Snapshot()));
            terminal.Write("echo QUEUED_BEFORE_DETACH\r\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "detach with live sessions");
            Assert.Equal(0, terminal.ExitCode);
            await WaitForSessionText(client, second.SessionId!, "QUEUED_BEFORE_DETACH");
            var sessions = (await client.CallAsync(new("list"))).Sessions!;
            Assert.Equal(2, sessions.Length);
            Assert.All(sessions, session => Assert.False(session.HasExited));
        }
        finally
        {
            try { await client.CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    private static async Task WaitForSessionText(DaemonClient client, string id, string expected, ConPtySession? ui = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? text = null;
        while (DateTime.UtcNow < deadline)
        {
            text = (await client.CallAsync(new("screen", id))).Text;
            if (text?.Contains(expected) == true) return;
            await Task.Delay(75);
        }
        Assert.Fail($"Expected {expected}; actual terminal:\n{text}\nOuter UI:\n{ui?.Screen.GetText()}");
    }

    [Fact]
    public async Task RealTerminalShowsWorkspaceHelpAndDetachesCleanly()
    {
        var root = FindRepository();
        var executable = GetExecutable(root);
        var state = Path.Combine(Path.GetTempPath(), "CrewTermTui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var terminal = ConPtySession.Start("tui-test", $"\"{executable}\" --state-dir \"{state}\" --project \"{root}\"", root, 140, 45);
            await WaitUntil(() => terminal.Screen.GetText().Contains("CrewTerm"), terminal, "workspace header");
            terminal.Write("\u001bOP");
            await WaitUntil(() => terminal.Screen.GetText().Contains("SHORTCUTS"), terminal, "help window");
            terminal.Write("\u001b");
            await Task.Delay(300);
            terminal.Write("\u001b[21~");
            await WaitUntil(() => terminal.HasExited, terminal, "clean detach");
            Assert.Equal(0, terminal.ExitCode);
        }
        finally
        {
            try { await new DaemonClient(state).CallAsync(new("stop")); } catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
    }

    private static string FindRepository()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CrewTerm.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Source repository not found");
    }

    private static string GetExecutable(string root)
    {
        var published = Environment.GetEnvironmentVariable("CREWTERM_TEST_EXE");
        if (!string.IsNullOrWhiteSpace(published)) return Path.GetFullPath(published);
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root, "src", "CrewTerm.App", "bin", config, "net10.0", "crewterm.exe");
    }

    private static async Task WaitUntil(Func<bool> condition, ConPtySession terminal, string behavior)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition() && !timeout.IsCancellationRequested) await Task.Delay(50);
        Assert.True(condition(), $"Missing {behavior}. Screen:\n{terminal.Screen.GetText()}");
    }
}
