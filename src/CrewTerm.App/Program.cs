using System.Text;
using CrewTerm.App.Cli;
using CrewTerm.App.Client;
using CrewTerm.App.Server;
using CrewTerm.App.UI.Windows;
using CrewTerm.Automation.Agents;
using CrewTerm.Core.IO;
using Terminal.Gui.App;

namespace CrewTerm.App;

/// <summary>
/// <c>crewterm.exe</c> 入口：解析命令行并分派到工作台界面、daemon、Provider hook 回调或非交互子命令。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 进程入口。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>退出码：0 成功；1 失败（错误信息写入标准错误，daemon 的错误同时追加到错误日志）。</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CrewTermCommandLine? options = null;
        try
        {
            options = CrewTermCommandLine.Parse(args);
            if (options.Command is "help" or "h") { Console.WriteLine(CrewTermHelp.Text); return 0; }
            if (options.Command == "version") { Console.WriteLine("CrewTerm " + CrewTermVersion.Current); return 0; }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This release requires Windows 10 1809 or later (ConPTY).");
            return options.Command switch
            {
                "agent-hook" => AgentHookCli.RunAsync(options).GetAwaiter().GetResult(),
                "server" => RunServer(options),
                "doctor" => Doctor(options),
                "ui" => RunWorkspace(options),
                "stop" => StopAsync(options).GetAwaiter().GetResult(),
                _ => RunCommandAsync(options).GetAwaiter().GetResult()
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("CrewTerm: " + error.Message);
            if (options?.Command == "server")
            {
                try { File.AppendAllText(StateFiles.PathOf(options.StateDirectory, StateFiles.ServerErrorLog), $"{DateTimeOffset.Now:O} {error}\n"); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
            return 1;
        }
    }

    /// <summary>
    /// 以 daemon 身份运行，直到收到 <c>stop</c> 请求。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>退出码 0。</returns>
    private static int RunServer(CrewTermCommandLine options)
    {
        using var host = new DaemonHost(options.StateDirectory);
        host.RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// 列出本机已安装的 Agent CLI，不连接也不启动 daemon。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>退出码 0。</returns>
    private static int Doctor(CrewTermCommandLine options)
    {
        Console.WriteLine($"CrewTerm {CrewTermVersion.Current} | {Environment.OSVersion}\nState: {options.StateDirectory}");
        foreach (var item in new AgentCatalog(options.StateDirectory).Discover())
            Console.WriteLine($"{item.Profile.Name,-18} {item.ExecutablePath ?? "not installed"}");
        return 0;
    }

    /// <summary>
    /// 停止 daemon；daemon 未运行时直接说明，而不是先启动一个再停止它。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>退出码 0。</returns>
    private static async Task<int> StopAsync(CrewTermCommandLine options)
    {
        if (await DaemonLauncher.TryPingAsync(options.StateDirectory) is null)
        {
            Console.WriteLine("CrewTerm daemon is not running.");
            return 0;
        }
        return await CliCommands.ExecuteAsync(options, CliCommands.BuildRequest(options));
    }

    /// <summary>
    /// 执行一个非交互子命令：先校验参数，再连接（必要时启动）daemon。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>退出码 0。</returns>
    private static async Task<int> RunCommandAsync(CrewTermCommandLine options)
    {
        var request = CliCommands.BuildRequest(options);
        await ConnectAsync(options);
        return await CliCommands.ExecuteAsync(options, request);
    }

    /// <summary>
    /// 打开全屏工作台，直到用户分离（F10）或停止服务。
    /// </summary>
    /// <param name="options">已解析的命令行。</param>
    /// <returns>退出码：已接受的终端输入全部送达为 0，否则为 1。</returns>
    private static int RunWorkspace(CrewTermCommandLine options)
    {
        ConnectAsync(options).GetAwaiter().GetResult();
        bool inputDrained;
        using (var app = Application.Create())
        {
            app.Init();
            var window = new WorkspaceWindow(app, options);
            try { app.Run(window); }
            finally { window.Dispose(); }
            inputDrained = window.InputDrained;
        }
        if (inputDrained) return 0;
        Console.Error.WriteLine("CrewTerm: some input could not be delivered. Reconnect and inspect the terminal before retrying a command.");
        return 1;
    }

    /// <summary>
    /// 确保 daemon 正在运行，并记录旧版 daemon 的兼容性警告。
    /// </summary>
    /// <param name="options">已解析的命令行；警告写入 <see cref="CrewTermCommandLine.ServerCompatibilityWarning"/>。</param>
    /// <returns>表示连接过程的任务。</returns>
    private static async Task ConnectAsync(CrewTermCommandLine options)
    {
        var handshake = await DaemonLauncher.EnsureRunningAsync(options.StateDirectory);
        options.ServerCompatibilityWarning = CrewTermVersion.ServerWarning(handshake);
        if (options.ServerCompatibilityWarning is { } warning) Console.Error.WriteLine("CrewTerm: " + warning);
    }
}
