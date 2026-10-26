using System.Reflection;
using CrewTerm.App.Protocol;
using CrewTerm.Core.IO;

namespace CrewTerm.App.Client;

/// <summary>
/// 连接状态目录对应的 daemon，必要时以脱离调用方的方式启动它。
/// </summary>
internal static class DaemonLauncher
{
    /// <summary>
    /// 启动 daemon 后等待其就绪的最大尝试次数。
    /// </summary>
    private const int StartupAttempts = 30;

    /// <summary>
    /// 两次就绪探测之间的间隔。
    /// </summary>
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 确保 daemon 正在运行并返回其握手响应；未运行时启动一个新的 daemon。
    /// </summary>
    /// <param name="stateDirectory">状态目录；不存在时自动创建。</param>
    /// <returns><c>ping</c> 的响应，包含 daemon 版本与协议版本。</returns>
    /// <exception cref="IOException">daemon 在约 3 秒内没有就绪。</exception>
    internal static async Task<DaemonResponse> EnsureRunningAsync(string stateDirectory)
    {
        if (await TryPingAsync(stateDirectory) is { } running) return running;
        StateFiles.EnsureDirectory(stateDirectory);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        var arguments = new List<string>();
        // 以 “dotnet crewterm.dll” 运行时（测试与开发环境），daemon 也要用同一个程序集启动。
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add("--server");
        arguments.Add("--state-dir");
        arguments.Add(Path.GetFullPath(stateDirectory));
        DetachedProcess.Start(processPath, arguments);
        for (var attempt = 0; attempt < StartupAttempts; attempt++)
        {
            if (await TryPingAsync(stateDirectory) is { } started) return started;
            await Task.Delay(StartupPollInterval);
        }
        throw new IOException($"Session server did not start. Inspect {StateFiles.ServerErrorLog} in " + stateDirectory);
    }

    /// <summary>
    /// 探测 daemon 是否正在运行，不会启动新的 daemon。
    /// </summary>
    /// <param name="stateDirectory">状态目录。</param>
    /// <returns>daemon 的 <c>ping</c> 响应；未运行或无法连接时为 <see langword="null"/>。</returns>
    internal static async Task<DaemonResponse?> TryPingAsync(string stateDirectory)
    {
        try
        {
            var ping = await new DaemonClient(stateDirectory).CallAsync(new("ping"));
            return ping.Ok ? ping : null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { return null; }
    }
}
