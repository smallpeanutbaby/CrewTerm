using System.Text.Json;
using CrewTerm.Automation.Plugins;

namespace CrewTerm.Automation.Tests.Plugins;

/// <summary>
/// 覆盖 <see cref="LocalPluginCatalog"/> 的插件安装、启用/停用、删除、并发实例与清单校验，以及 <see cref="LocalPluginRunner"/>
/// 运行插件命令和事件 hook 的行为（标准输入 JSON、参数、工作目录、输出上限、超时、取消、标准输入写入失败）。
/// 插件由 <see cref="Plugin"/> 在临时目录中生成，命令通过 <see cref="PowerShellFixture"/> 以真实 PowerShell 进程运行脚本。
/// </summary>
public sealed class LocalPluginTests : IDisposable
{
    /// <summary>
    /// 本测试实例专用的临时根目录（以随机 GUID 命名），存放生成的插件源目录和状态目录。
    /// </summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "CrewTermPlugins", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 插件目录使用的状态目录（<see cref="root"/> 下的 <c>state</c> 子目录）。
    /// </summary>
    private string State => Path.Combine(root, "state");
    /// <summary>
    /// 创建本测试实例使用的临时根目录。
    /// </summary>
    public LocalPluginTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// 验证：新安装的插件默认停用，登记的源目录为规范化的绝对路径；启用状态会持久化，停用同样生效；
    /// 删除后重新加载的列表为空，但源目录中的清单文件仍在；再次删除返回 <see langword="false"/>。
    /// </summary>
    [Fact]
    public void InstallationEnableDisableAndRemovalAreDurableWithoutDeletingSource()
    {
        var source = Plugin("local.echo");
        var catalog = new LocalPluginCatalog(State);
        var installed = catalog.Install(source);
        Assert.False(installed.Enabled);
        Assert.Equal(Path.GetFullPath(source), installed.SourceDirectory);
        Assert.True(catalog.SetEnabled("local.echo", true).Enabled);
        Assert.True(new LocalPluginCatalog(State).Get("local.echo")!.Enabled);
        Assert.False(catalog.SetEnabled("local.echo", false).Enabled);
        Assert.True(catalog.Remove("local.echo"));
        Assert.Empty(new LocalPluginCatalog(State).List());
        Assert.True(File.Exists(Path.Combine(source, "crewterm-plugin.json")));
        Assert.False(catalog.Remove("local.echo"));
    }

    /// <summary>
    /// 验证：从另一个目录以 <c>enabled: true</c> 安装标识相同的插件时抛出 <see cref="InvalidOperationException"/>，
    /// 现有登记仍唯一、仍指向第一个源目录，且保持停用。
    /// </summary>
    [Fact]
    public void DuplicateInstallDoesNotReplaceExistingSourceOrEnableIt()
    {
        var first = Plugin("duplicate");
        var second = Plugin("duplicate", directoryName: "second source");
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(first);

        Assert.Throws<InvalidOperationException>(() => catalog.Install(second, enabled: true));

        var installed = Assert.Single(catalog.List());
        Assert.Equal(first, installed.SourceDirectory);
        Assert.False(installed.Enabled);
    }

    /// <summary>
    /// 验证：同一状态目录上的两个独立目录实例各自安装一个插件、其中一个再启用自己的插件后，两条登记都被保留，
    /// 另一个实例安装的插件也不会被误启用。
    /// </summary>
    [Fact]
    public void IndependentCatalogInstancesPreserveOtherRegistrations()
    {
        var first = new LocalPluginCatalog(State);
        var second = new LocalPluginCatalog(State);
        first.Install(Plugin("one"));
        second.Install(Plugin("two"));
        first.SetEnabled("one", true);
        Assert.Equal(2, new LocalPluginCatalog(State).List().Count);
        Assert.False(second.Get("two")!.Enabled);
    }

    /// <summary>
    /// 验证：清单不是有效 JSON、没有任何命令、命令的工作目录用 <c>..</c> 跳出插件目录，或某个操作系统的替代调用
    /// 带引号、用相对路径跳出插件目录、含未知字段时，安装抛出 <see cref="InvalidDataException"/>，且不会登记任何插件。
    /// </summary>
    /// <param name="json">写入 <c>crewterm-plugin.json</c> 的非法清单内容。</param>
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1\",\"commands\":[]}")]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1\",\"commands\":[{\"id\":\"run\",\"name\":\"Run\",\"executable\":\"cmd.exe\",\"workingDirectory\":\"..\"}]}")]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1\",\"commands\":[{\"id\":\"run\",\"name\":\"Run\",\"executable\":\"tool\",\"windows\":{\"executable\":\"\\\"quoted\\\"\"}}]}")]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1\",\"commands\":[{\"id\":\"run\",\"name\":\"Run\",\"executable\":\"tool\",\"linux\":{\"executable\":\"../outside/run\"}}]}")]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1\",\"commands\":[{\"id\":\"run\",\"name\":\"Run\",\"executable\":\"tool\",\"osx\":{\"shell\":\"zsh\"}}]}")]
    public void InvalidManifestIsRejectedBeforeRegistration(string json)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "invalid")).FullName;
        File.WriteAllText(Path.Combine(source, "crewterm-plugin.json"), json);
        var catalog = new LocalPluginCatalog(State);
        Assert.Throws<InvalidDataException>(() => catalog.Install(source));
        Assert.Empty(catalog.List());
    }

    /// <summary>
    /// 验证：命令按当前操作系统选用 <c>windows</c>、<c>linux</c> 或 <c>osx</c> 替代调用中给出的字段，未给出的字段沿用默认值；
    /// 没有对应替代调用时使用默认调用。
    /// </summary>
    [Fact]
    public void PlatformOverridesReplaceOnlyTheFieldsTheyDeclare()
    {
        var current = new LocalPluginPlatformCommand("platform-tool");
        var command = new LocalPluginCommand("run", "Run", "default-tool", ["--default"],
            Windows: OperatingSystem.IsWindows() ? current : null,
            Linux: OperatingSystem.IsLinux() ? current : null,
            Osx: OperatingSystem.IsMacOS() ? current : null);
        Assert.Equal("platform-tool", command.EffectiveExecutable);
        Assert.Equal(new[] { "--default" }, command.EffectiveArguments);

        var argumentsOnly = new LocalPluginPlatformCommand(Arguments: ["--platform"]);
        var other = new LocalPluginCommand("run", "Run", "default-tool", ["--default"],
            Windows: argumentsOnly, Linux: argumentsOnly, Osx: argumentsOnly);
        Assert.Equal("default-tool", other.EffectiveExecutable);
        Assert.Equal(new[] { "--platform" }, other.EffectiveArguments);

        var plain = new LocalPluginCommand("run", "Run", "default-tool", ["--default"]);
        Assert.Equal("default-tool", plain.EffectiveExecutable);
        Assert.Equal(new[] { "--default" }, plain.EffectiveArguments);
    }

    /// <summary>
    /// 验证：真实插件进程从标准输入收到 UTF-8 JSON 请求（<c>kind</c> 为 <c>command</c>、带插件标识与负载），含空格和 <c>&amp;</c> 的额外参数
    /// 原样传入，进程在清单指定的 <c>work folder</c> 子目录中运行；含中文、引号、<c>&amp;</c> 与 <c>$(...)</c> 的负载文本被原样回显，
    /// 不会被任何 Shell 解释。
    /// </summary>
    [Fact]
    public async Task RealPluginReceivesJsonStdinLiteralArgumentsAndItsWorkingDirectory()
    {
        var source = Plugin("local.echo", script: """
            param([string]$Literal)
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $raw = [Console]::In.ReadToEnd()
            [IO.File]::WriteAllText((Join-Path (Get-Location) 'received.json'), $raw)
            [IO.File]::WriteAllText((Join-Path (Get-Location) 'argument.txt'), $Literal)
            $request = $raw | ConvertFrom-Json
            [Console]::Out.Write('value=' + $request.payload.text)
            """, extraArgument: "two words & literal", workingDirectory: "work folder");
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source, enabled: true);
        var payload = JsonSerializer.SerializeToElement(new { text = "中文 \"quoted\" & $(literal)" });

        var result = await new LocalPluginRunner(catalog).RunAsync("local.echo", "run", payload);

        Assert.True(result.Success, result.Error + result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("value=中文 \"quoted\" & $(literal)", result.Output);
        var cwd = Path.Combine(source, "work folder");
        using var received = JsonDocument.Parse(File.ReadAllText(Path.Combine(cwd, "received.json")));
        Assert.Equal("command", received.RootElement.GetProperty("kind").GetString());
        Assert.Equal("local.echo", received.RootElement.GetProperty("pluginId").GetString());
        Assert.Equal(payload.GetProperty("text").GetString(), received.RootElement.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal("two words & literal", File.ReadAllText(Path.Combine(cwd, "argument.txt")));
        Assert.False(File.Exists(Path.Combine(source, "received.json")));
    }

    /// <summary>
    /// 验证：运行已停用插件的命令时返回失败结果、错误信息提及 disabled，且插件脚本没有被执行（未生成 <c>executed.txt</c>）。
    /// </summary>
    [Fact]
    public async Task DisabledPluginDoesNotLaunchAProcess()
    {
        var source = Plugin("disabled", script: "[IO.File]::WriteAllText('executed.txt', 'wrong')");
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source);
        var result = await new LocalPluginRunner(catalog).RunAsync("disabled", "run", JsonSerializer.SerializeToElement(new { }));
        Assert.False(result.Success);
        Assert.Contains("disabled", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(source, "executed.txt")));
    }

    /// <summary>
    /// 验证：输出上限为 128 个字符的插件写出 20000 个字符后，进程仍能正常结束并返回成功；保留的输出被标记为已截断，长度在 1～128 之间。
    /// </summary>
    [Fact]
    public async Task OutputRetentionIsBoundedWhileTheProcessCanFinish()
    {
        var source = Plugin("output", script: "[Console]::Out.Write(('x' * 20000)); [Console]::Error.Write('THE_END')", outputLimit: 128);
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source, enabled: true);
        var result = await new LocalPluginRunner(catalog).RunAsync("output", "run", JsonSerializer.SerializeToElement(new { }));
        Assert.True(result.Success, result.Error);
        Assert.True(result.OutputTruncated);
        Assert.InRange(result.Output.Length, 1, 128);
    }

    /// <summary>
    /// 验证：超时设为 1 秒的插件在休眠 10 秒期间被终止，运行在 30 秒内返回失败结果，标记为超时而非取消；
    /// 脚本已开始执行（有 <c>started.txt</c>），但没有执行到休眠之后（无 <c>finished.txt</c>）。
    /// </summary>
    [Fact]
    public async Task TimeoutStopsRealPluginAndReturnsFailure()
    {
        var source = Plugin("timeout", script: "[IO.File]::WriteAllText('started.txt', 'yes'); Start-Sleep -Seconds 10; [IO.File]::WriteAllText('finished.txt', 'wrong')", timeout: 1);
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source, enabled: true);
        var result = await new LocalPluginRunner(catalog).RunAsync("timeout", "run", JsonSerializer.SerializeToElement(new { })).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(File.Exists(Path.Combine(source, "started.txt")));
        Assert.False(File.Exists(Path.Combine(source, "finished.txt")));
    }

    /// <summary>
    /// 验证：等插件脚本确实开始执行（出现 <c>started.txt</c>）后取消调用方令牌，运行在 30 秒内结束，
    /// 结果标记为已取消、未超时且不成功。
    /// </summary>
    [Fact]
    public async Task CallerCancellationStopsRealPluginAndReportsCancellation()
    {
        var source = Plugin("cancel", script: "[IO.File]::WriteAllText('started.txt', 'yes'); Start-Sleep -Seconds 10");
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source, enabled: true);
        using var cancellation = new CancellationTokenSource();
        var run = new LocalPluginRunner(catalog).RunAsync("cancel", "run", JsonSerializer.SerializeToElement(new { }), cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Path.Combine(source, "started.txt")) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(File.Exists(Path.Combine(source, "started.txt")));
        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.False(result.Success);
    }

    /// <summary>
    /// 验证（Windows，探针通过 kernel32 关闭标准输入句柄）：插件进程记录自身 PID 后关闭自己的标准输入句柄并休眠，导致运行器写入 60000 个字符的请求失败时，
    /// 运行在 30 秒内返回失败结果，并且该插件进程已被终止而不是成为孤儿进程；测试结束时如进程仍在则强制结束其进程树。
    /// </summary>
    [WindowsFact]
    public async Task BrokenStdinStopsThePluginInsteadOfLeavingAnOrphanProcess()
    {
        var source = Plugin("broken-input", script: """
            [IO.File]::WriteAllText('pid.txt', $PID.ToString())
            Add-Type 'using System; using System.Runtime.InteropServices; public static class PluginFixtureHandles { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h); }'
            [PluginFixtureHandles]::CloseHandle([PluginFixtureHandles]::GetStdHandle(-10)) | Out-Null
            Start-Sleep -Seconds 10
            """);
        var catalog = new LocalPluginCatalog(State);
        catalog.Install(source, enabled: true);
        System.Diagnostics.Process? child = null;
        try
        {
            var result = await new LocalPluginRunner(catalog).RunAsync("broken-input", "run",
                JsonSerializer.SerializeToElement(new { data = new string('x', 60000) })).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(result.Success);
            var pid = int.Parse(File.ReadAllText(Path.Combine(source, "pid.txt")));
            try { child = System.Diagnostics.Process.GetProcessById(pid); }
            catch (ArgumentException) { }
            Assert.True(child is null || child.HasExited, "A failed stdin write left the plugin process running.");
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                child.Dispose();
            }
        }
    }

    /// <summary>
    /// 验证：触发 <c>session.created</c> 事件时只运行已启用插件的 hook（停用的 <c>off</c> 不运行，共 2 个结果）；
    /// <c>bad</c> 以退出码 7 失败不影响 <c>good</c> 成功运行，且 <c>good</c> 从标准输入收到 <c>kind</c> 为 <c>event</c>、
    /// 带事件名与负载的 JSON。
    /// </summary>
    [Fact]
    public async Task EventHooksReceiveEventJsonAndOneFailureDoesNotStopOtherPlugins()
    {
        var catalog = new LocalPluginCatalog(State);
        var good = Plugin("good", script: "$raw = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText('event.json', $raw); Write-Output 'handled'", hook: "session.created");
        catalog.Install(Plugin("bad", script: "[Console]::Error.Write('hook failed'); exit 7", hook: "session.created"), enabled: true);
        catalog.Install(good, enabled: true);
        catalog.Install(Plugin("off", hook: "session.created"));

        var results = await new LocalPluginRunner(catalog).RunEventAsync("session.created", JsonSerializer.SerializeToElement(new { sessionId = "crew-42" }));

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.PluginId == "good").Success);
        Assert.Equal(7, results.Single(r => r.PluginId == "bad").ExitCode);
        Assert.False(results.Single(r => r.PluginId == "bad").Success);
        using var received = JsonDocument.Parse(File.ReadAllText(Path.Combine(good, "event.json")));
        Assert.Equal("event", received.RootElement.GetProperty("kind").GetString());
        Assert.Equal("session.created", received.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("crew-42", received.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
    }

    /// <summary>
    /// 在临时根目录下生成一个插件源目录：写入 PowerShell 脚本 <c>run.ps1</c>，并写出只含一条命令 <c>run</c>
    /// （用 PowerShell 以文件方式运行该脚本）、可选绑定一个事件 hook 的 <c>crewterm-plugin.json</c> 清单。
    /// </summary>
    /// <param name="id">插件标识；清单中的名称为 <c>Local {id}</c>。</param>
    /// <param name="script">脚本正文；为 <see langword="null"/> 时脚本读取并输出全部标准输入。</param>
    /// <param name="directoryName">源目录名；为 <see langword="null"/> 时使用 <paramref name="id"/>。</param>
    /// <param name="extraArgument">追加在脚本路径之后、原样传给脚本的额外参数；为 <see langword="null"/> 时不追加。</param>
    /// <param name="workingDirectory">命令的工作目录（相对插件目录），会预先创建；为 <see langword="null"/> 时不指定。</param>
    /// <param name="timeout">命令超时秒数。</param>
    /// <param name="outputLimit">命令最多保留的输出字符数。</param>
    /// <param name="hook">要绑定到 <c>run</c> 命令的事件名；为 <see langword="null"/> 时不声明 hook。</param>
    /// <returns>生成的插件源目录的绝对路径。</returns>
    private string Plugin(string id, string? script = null, string? directoryName = null, string? extraArgument = null,
        string? workingDirectory = null, int timeout = 10, int outputLimit = 65536, string? hook = null)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, directoryName ?? id)).FullName;
        var scriptPath = Path.Combine(source, "run.ps1");
        PowerShellFixture.WriteScript(scriptPath, script ?? "[Console]::In.ReadToEnd()");
        if (workingDirectory is not null) Directory.CreateDirectory(Path.Combine(source, workingDirectory));
        var arguments = new List<string>(PowerShellFixture.FileArguments(scriptPath));
        if (extraArgument is not null) arguments.Add(extraArgument);
        var manifest = new
        {
            id, name = "Local " + id, version = "1.0.0",
            commands = new[] { new { id = "run", name = "Run", executable = PowerShellFixture.Executable, arguments, timeoutSeconds = timeout,
                maxOutputCharacters = outputLimit, workingDirectory } },
            hooks = hook is null ? Array.Empty<object>() : new object[] { new { @event = hook, commandId = "run" } }
        };
        File.WriteAllText(Path.Combine(source, "crewterm-plugin.json"), JsonSerializer.Serialize(manifest));
        return source;
    }

    /// <summary>
    /// 递归删除本测试实例的临时根目录及其中所有文件。
    /// </summary>
    public void Dispose() => Directory.Delete(root, true);
}
