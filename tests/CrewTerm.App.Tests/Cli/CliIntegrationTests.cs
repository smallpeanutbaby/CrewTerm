using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Client;
using CrewTerm.App.Protocol;
using CrewTerm.App.Tests.Fixtures;

namespace CrewTerm.App.Tests.Cli;

/// <summary>
/// 以真实进程运行 <c>crewterm.exe</c> 的基础命令行端到端测试：每条命令都通过 <c>--state-dir</c> 指向本测试独占的临时状态目录，
/// 并以含空格和中文的项目目录为工作目录，覆盖帮助/版本/诊断、跨进程的终端会话生命周期、后台任务以及参数错误的退出码与错误输出。
/// </summary>
public sealed class CliIntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// 本测试实例独占的 daemon 状态目录（系统临时目录下的 <c>CrewTermCliTests\&lt;GUID&gt;</c>）。
    /// </summary>
    private readonly string state = Path.Combine(Path.GetTempPath(), "CrewTermCliTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 被测 <c>crewterm.exe</c> 的完整路径。
    /// </summary>
    private readonly string executable;

    /// <summary>
    /// 状态目录下名为 <c>project with spaces 项目</c> 的项目目录，用来验证路径中的空格和非 ASCII 字符被原样传递；同时是 CLI 子进程的工作目录。
    /// </summary>
    private string Project => Path.Combine(state, "project with spaces 项目");

    /// <summary>
    /// 定位被测可执行文件：优先使用环境变量 <c>CREWTERM_TEST_EXE</c>；否则从测试输出目录向上查找包含 <c>CrewTerm.slnx</c> 的仓库根目录，
    /// 并按测试程序集的构建配置拼出 <c>src\CrewTerm.App\bin\&lt;配置&gt;\net10.0\crewterm.exe</c>。
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">找不到仓库根目录时抛出。</exception>
    public CliIntegrationTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrewTerm.slnx")))
            directory = directory.Parent;
        var repository = directory?.FullName ?? throw new DirectoryNotFoundException("Source repository not found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        executable = Environment.GetEnvironmentVariable("CREWTERM_TEST_EXE")
            ?? Path.Combine(repository, "src", "CrewTerm.App", "bin", configuration, "net10.0", "crewterm.exe");
    }

    /// <summary>
    /// 断言被测可执行文件已构建存在，并创建项目目录（连同状态目录）。
    /// </summary>
    /// <returns>已完成的任务。</returns>
    public Task InitializeAsync()
    {
        Assert.True(File.Exists(executable), "Build the application before exercising its executable: " + executable);
        Directory.CreateDirectory(Project);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 验证：<c>--help</c> 列出 <c>crewterm new</c> 和 <c>crewterm run</c>，<c>--version</c> 输出 <c>CrewTerm 主.次.修订</c> 格式的版本，
    /// <c>doctor</c> 报告状态目录和 Shell 信息；三者都成功且标准错误为空，并且全程没有启动 daemon（状态目录中不存在 <c>server.lock</c>）。
    /// </summary>
    [Fact]
    public async Task HelpVersionAndDoctorWorkWithoutStartingDaemon()
    {
        var help = await RunAsync("--help");
        AssertSuccess(help);
        Assert.Contains("crewterm new", help.Output);
        Assert.Contains("crewterm run", help.Output);

        var version = await RunAsync("--version");
        AssertSuccess(version);
        Assert.Matches(@"^CrewTerm \d+\.\d+\.\d+", version.Output);

        var doctor = await RunAsync("doctor");
        AssertSuccess(doctor);
        Assert.Contains(state, doctor.Output);
        Assert.Contains("Shell", doctor.Output);
        Assert.False(File.Exists(Path.Combine(state, "server.lock")));
    }

    /// <summary>
    /// 验证：分别由不同的 CLI 进程执行 <c>new</c>、<c>send</c>、<c>read</c>、<c>sessions</c>、<c>close</c> 时，同一会话在进程之间持续存在：
    /// 写入的 <c>echo</c> 命令在屏幕上产生完整的标记行，会话列表报告正确的名称、工作目录且未退出，关闭后不再出现在列表中。
    /// </summary>
    [Fact]
    public async Task TerminalCanBeCreatedWrittenReadListedAndClosedAcrossCliProcesses()
    {
        var created = ReadResponse(await RunAsync("new", "--command", "cmd.exe /d /q", "--project", Project, "--name", "CLI shell"));
        Assert.False(string.IsNullOrWhiteSpace(created.SessionId));
        var id = created.SessionId!;
        var marker = "CLI_TERMINAL_" + Guid.NewGuid().ToString("N");

        ReadResponse(await RunAsync("send", id, "--text", "echo " + marker + "\r"));
        await WaitForScreenLineAsync(id, marker);

        var listed = ReadResponse(await RunAsync("sessions"));
        var session = Assert.Single(listed.Sessions!, item => item.Id == id);
        Assert.Equal("CLI shell", session.Name);
        Assert.Equal(Project, session.WorkingDirectory);
        Assert.False(session.HasExited);

        ReadResponse(await RunAsync("close", id));
        Assert.DoesNotContain(ReadResponse(await RunAsync("sessions")).Sessions!, item => item.Id == id);
    }

    /// <summary>
    /// 验证：<c>run</c> 启动的后台命令在发起它的 CLI 进程退出后继续运行；<c>tasks</c> 列表中的摘要不携带输出（为空字符串），
    /// 任务完成后 <c>task</c> 返回退出码 0、未取消、无错误，并保留名称、工作目录和命令实际打印的标记。
    /// </summary>
    [Fact]
    public async Task BackgroundCommandContinuesAfterCliExitAndExposesActualOutput()
    {
        var marker = "CLI_TASK_" + Guid.NewGuid().ToString("N");
        var command = "echo " + marker + " & ping -n 2 127.0.0.1 >nul";
        var started = ReadResponse(await RunAsync("run", "--command", command, "--project", Project, "--name", "CLI check"));
        Assert.False(string.IsNullOrWhiteSpace(started.SessionId));
        var id = started.SessionId!;

        var listed = ReadResponse(await RunAsync("tasks"));
        var summary = Assert.Single(listed.Tasks!, item => item.Id == id);
        Assert.Equal("", summary.Output);

        var finished = await WaitForTaskAsync(id);
        Assert.Equal(0, finished.ExitCode);
        Assert.False(finished.Cancelled);
        Assert.Null(finished.Error);
        Assert.Equal("CLI check", finished.Name);
        Assert.Equal(Project, finished.WorkingDirectory);
        Assert.Contains(marker, finished.Output);
    }

    /// <summary>
    /// 验证：后台命令 <c>exit /b 7</c> 失败时，启动和查询它的 CLI 请求本身仍然成功（退出码 0），任务信息中记录的是命令自己的退出码 7 且未取消。
    /// </summary>
    [Fact]
    public async Task FailedBackgroundCommandKeepsItsExitCodeSeparateFromCliRequestSuccess()
    {
        var started = ReadResponse(await RunAsync("run", "--command", "exit /b 7", "--project", Project));
        var finished = await WaitForTaskAsync(started.SessionId!);
        Assert.Equal(7, finished.ExitCode);
        Assert.False(finished.Cancelled);
    }

    /// <summary>
    /// 验证：未知子命令或缺少必填参数的命令以退出码 1 结束，在标准错误中给出可读的错误信息，标准输出为空。
    /// </summary>
    /// <param name="command">要运行的（无效）子命令。</param>
    /// <param name="error">标准错误中应包含的错误片段。</param>
    [Theory]
    [InlineData("does-not-exist", "Unknown command")]
    [InlineData("new", "Required: --command")]
    public async Task InvalidCliCommandReturnsExitOneAndReadableError(string command, string error)
    {
        var result = await RunAsync(command);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(error, result.Error);
        Assert.Equal("", result.Output);
    }

    /// <summary>
    /// 验证：需要目标标识的命令缺少标识、或选项取值格式错误时，以退出码 1 给出明确的参数错误（而不是 daemon 的 “Session not found”），
    /// 并且参数校验发生在连接 daemon 之前：状态目录中不会出现 <c>server.lock</c>。
    /// </summary>
    /// <param name="commandLine">以空格分隔的子命令与参数。</param>
    /// <param name="error">标准错误中应包含的错误片段。</param>
    [Theory]
    [InlineData("send --text x", "requires a target ID")]
    [InlineData("read", "requires a target ID")]
    [InlineData("close", "requires a target ID")]
    [InlineData("task", "requires a target ID")]
    [InlineData("agent-wait abc --state done --timeout-ms soon", "--timeout-ms must be an integer")]
    [InlineData("plugin-enable abc --enabled maybe", "--enabled must be true or false")]
    public async Task InvalidArgumentsAreReportedBeforeAnyDaemonStarts(string commandLine, string error)
    {
        var result = await RunAsync(commandLine.Split(' '));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(error, result.Error);
        Assert.False(File.Exists(Path.Combine(state, "server.lock")));
    }

    /// <summary>
    /// 验证：daemon 未运行时执行 <c>stop</c> 只提示 “not running” 并返回 0，不会先启动一个 daemon 再停止它。
    /// </summary>
    [Fact]
    public async Task StopWithoutRunningDaemonDoesNotStartOne()
    {
        var result = await RunAsync("stop");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("not running", result.Output);
        Assert.False(File.Exists(Path.Combine(state, "server.lock")));
    }

    /// <summary>
    /// 验证：<c>--project .</c> 这样的相对路径按 CLI 进程自己的当前目录（测试项目目录）解析，
    /// 而不是按 daemon 的当前目录（程序安装目录）解析。
    /// </summary>
    [Fact]
    public async Task RelativeProjectIsResolvedAgainstTheCallersDirectory()
    {
        var created = ReadResponse(await RunAsync("new", "--command", "cmd.exe /d /q", "--project", ".", "--name", "relative"));
        var session = Assert.Single(ReadResponse(await RunAsync("sessions")).Sessions!, item => item.Id == created.SessionId);
        Assert.Equal(Project, session.WorkingDirectory);
        ReadResponse(await RunAsync("close", created.SessionId!));
    }

    /// <summary>
    /// 在 15 秒内每 75 毫秒运行一次 <c>read</c>，直到屏幕上某一行去掉首尾空白后恰好等于期望文本（排除只出现在回显命令行中的情况）；超时则附上最后一次屏幕内容使测试失败。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="expected">期望出现的完整行。</param>
    /// <returns>表示等待过程的任务。</returns>
    private async Task WaitForScreenLineAsync(string id, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            var result = await RunAsync("read", id);
            AssertSuccess(result);
            last = result.Output;
            if (last.Replace("\r", "").Split('\n').Any(line => line.Trim() == expected)) return;
            await Task.Delay(75);
        }
        Assert.Fail($"Terminal did not produce the expected complete line {expected}:\n{last}");
    }

    /// <summary>
    /// 在 15 秒内每 75 毫秒运行一次 <c>task</c> 查询后台任务，直到其 <c>Completed</c> 为 true。
    /// </summary>
    /// <param name="id">后台任务标识。</param>
    /// <returns>已完成任务的信息。</returns>
    /// <exception cref="TimeoutException">超时仍未完成时抛出，消息中包含最后一次查询到的任务信息。</exception>
    private async Task<DaemonTaskInfo> WaitForTaskAsync(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        DaemonTaskInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadResponse(await RunAsync("task", id)).Task;
            Assert.NotNull(last);
            if (last.Completed) return last;
            await Task.Delay(75);
        }
        throw new TimeoutException($"Background task did not finish: {last}");
    }

    /// <summary>
    /// 以项目目录为工作目录、并在参数前加上 <c>--state-dir</c> 运行一次被测可执行文件，以 UTF-8 收集标准输出和标准错误。
    /// </summary>
    /// <param name="arguments">子命令及其参数。</param>
    /// <returns>退出码与输出。</returns>
    /// <exception cref="IOException">无法启动进程时抛出。</exception>
    /// <exception cref="TimeoutException">25 秒内未结束时杀死整个进程树后抛出，消息中说明进程及两个输出流是否已结束。</exception>
    private async Task<CliResult> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--state-dir");
        start.ArgumentList.Add(state);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start CLI");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(25));
            return new(process.ExitCode, await output, await error);
        }
        catch (TimeoutException)
        {
            var exited = process.HasExited;
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            throw new TimeoutException($"CLI {arguments[0]} exceeded 25 seconds. Process exited: {exited}; stdout finished: {output.IsCompleted}; stderr finished: {error.IsCompleted}.");
        }
    }

    /// <summary>
    /// 断言命令成功（见 <see cref="AssertSuccess"/>），把标准输出反序列化为 <see cref="DaemonResponse"/>，并断言其 <c>Ok</c> 为 true。
    /// </summary>
    /// <param name="result">命令运行结果。</param>
    /// <returns>反序列化后的 daemon 响应。</returns>
    private static DaemonResponse ReadResponse(CliResult result)
    {
        AssertSuccess(result);
        var response = JsonSerializer.Deserialize<DaemonResponse>(result.Output);
        Assert.NotNull(response);
        Assert.True(response.Ok, response.Error);
        return response;
    }

    /// <summary>
    /// 断言命令退出码为 0 且标准错误为空；失败信息中附带退出码和两路输出。
    /// </summary>
    /// <param name="result">命令运行结果。</param>
    private static void AssertSuccess(CliResult result)
    {
        Assert.True(result.ExitCode == 0, $"CLI exited {result.ExitCode}: {result.Error}\n{result.Output}");
        Assert.Equal("", result.Error);
    }

    /// <summary>
    /// 测试结束后清理：若存在 <c>server.lock</c>，先用 CLI 的 <c>stop</c> 关闭 daemon（失败时退回为直接通过 <see cref="DaemonClient"/> 发送 <c>stop</c>），
    /// 再在 10 秒内等待能以独占方式打开锁文件（即 daemon 进程已释放它）；随后确认目录位于 <c>CrewTermCliTests</c> 临时根目录之下，
    /// 并在 10 秒内反复尝试递归删除。
    /// </summary>
    /// <returns>表示清理过程的任务。</returns>
    public async Task DisposeAsync()
    {
        var lockPath = Path.Combine(state, "server.lock");
        if (File.Exists(lockPath))
        {
            try { AssertSuccess(await RunAsync("stop")); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            {
                try { await new DaemonClient(state).CallAsync(new("stop")); }
                catch (Exception stopError) when (stopError is IOException or TimeoutException or OperationCanceledException) { }
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    using var released = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
            }
        }
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CrewTermCliTests")) + Path.DirectorySeparatorChar;
        var fullState = Path.GetFullPath(state);
        Assert.StartsWith(allowedRoot, fullState, StringComparison.OrdinalIgnoreCase);
        var cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(fullState))
        {
            try { Directory.Delete(fullState, recursive: true); }
            catch (IOException) when (DateTime.UtcNow < cleanupDeadline) { await Task.Delay(50); }
        }
    }
}
