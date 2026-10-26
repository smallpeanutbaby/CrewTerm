using CrewTerm.App.Server;

namespace CrewTerm.App.Tests;

public sealed class DaemonTests : IAsyncLifetime
{
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermDaemonTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource stop = new();
    private Task? serverTask;
    private DaemonHost? server;

    public async Task InitializeAsync()
    {
        server = new DaemonHost(state);
        serverTask = server.RunAsync(stop.Token);
        var client = new DaemonClient(state);
        for (var i = 0; i < 40; i++)
        {
            try { await client.CallAsync(new("ping")); return; }
            catch (TimeoutException) { await Task.Delay(25); }
        }
        throw new TimeoutException("Test daemon did not start");
    }

    [Fact]
    public async Task ReconnectingClientSeesSameLiveSession()
    {
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
        Assert.True(created.Ok, created.Error);
        var second = new DaemonClient(state);
        var sessions = await second.CallAsync(new("list"));
        Assert.Contains(sessions.Sessions!, s => s.Id == created.SessionId);
        await second.CallAsync(new("input", SessionId: created.SessionId, Text: "echo crewterm-reconnected\r\n"));
        for (var i = 0; i < 60; i++)
        {
            var screen = await second.CallAsync(new("screen", SessionId: created.SessionId));
            if (screen.Text?.Contains("crewterm-reconnected") == true) break;
            await Task.Delay(50);
        }
        var result = await second.CallAsync(new("screen", SessionId: created.SessionId));
        Assert.Contains("crewterm-reconnected", result.Text);
        Assert.True((await second.CallAsync(new("close", SessionId: created.SessionId))).Ok);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    [Fact]
    public async Task InvalidWorkingDirectoryFailsWithoutSession()
    {
        var client = new DaemonClient(state);
        var result = await client.CallAsync(new("create", Command: "cmd.exe", WorkingDirectory: Path.Combine(state, "missing")));
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error!);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    [Fact]
    public async Task UnknownSessionDoesNotRouteInputElsewhere()
    {
        var result = await new DaemonClient(state).CallAsync(new("input", SessionId: "absent", Text: "exit\r"));
        Assert.False(result.Ok);
        Assert.Contains("Session", result.Error);
    }

    [Fact]
    public async Task InvalidResizeIsRejected()
    {
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
        var result = await client.CallAsync(new("resize", SessionId: created.SessionId, Columns: -1));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotLeaveUnreportedSession()
    {
        Directory.CreateDirectory(Path.Combine(state, "saved-sessions.json"));
        var client = new DaemonClient(state);
        var created = await client.CallAsync(new("create", Command: "cmd.exe /d /q", WorkingDirectory: state));
        Assert.False(created.Ok);
        Assert.Empty((await client.CallAsync(new("list"))).Sessions!);
    }

    [Fact]
    public async Task ConnectionSaturationDoesNotTerminateServer()
    {
        var held = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", DaemonClient.PipeName(state),
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                held.Add(pipe);
                await pipe.ConnectAsync(2000);
            }
            await Task.Delay(150);
        }
        finally { foreach (var pipe in held) pipe.Dispose(); }
        await Task.Delay(150);
        Assert.True((await new DaemonClient(state).CallAsync(new("ping"))).Ok);
    }

    public async Task DisposeAsync()
    {
        await stop.CancelAsync();
        if (serverTask is not null) await serverTask;
        server?.Dispose();
        stop.Dispose();
        if (Directory.Exists(state)) Directory.Delete(state, true);
    }
}
