using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace CrewTerm.Automation.Tasks;

/// <summary>
/// 以有限并发运行用户命令，实时转发输出，并返回真实的退出码与末尾输出。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>命令通过系统 Shell 执行（Windows 为 <c>cmd.exe /d /s /c</c>，其他平台为 <c>/bin/sh -c</c>），标准输入立即关闭。</description></item>
/// <item><description>标准输出与标准错误并发读取、按到达顺序合并，只保留最近 <c>maxOutputCharacters</c> 个字符。</description></item>
/// <item><description>取消时结束整个进程树，结果仍记录实际退出码并标记为已取消。</description></item>
/// </list>
/// </remarks>
public sealed class TaskRunner
{
    /// <summary>
    /// 并发槽位：初始计数与上限都等于 <c>maxConcurrency</c>，每次运行占用一个，结束（含异常）时释放。
    /// </summary>
    private readonly SemaphoreSlim slots;

    /// <summary>
    /// 每次运行保留的输出字符上限，传给 <see cref="BoundedTextBuffer"/>。
    /// </summary>
    private readonly int maxOutputCharacters;

    /// <summary>
    /// 创建运行器。
    /// </summary>
    /// <param name="maxConcurrency">同时运行的命令上限；超出的调用排队等待。</param>
    /// <param name="maxOutputCharacters">每次运行保留的输出字符上限。</param>
    public TaskRunner(int maxConcurrency = 4, int maxOutputCharacters = 262144)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputCharacters);
        slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        this.maxOutputCharacters = maxOutputCharacters;
    }

    /// <summary>
    /// 运行一条命令直到结束或被取消。
    /// </summary>
    /// <param name="task">要运行的命令。</param>
    /// <param name="onOutput">实时输出回调；在输出锁内按顺序调用，抛出的异常会在命令结束后重新抛出。</param>
    /// <param name="cancellationToken">取消排队或结束正在运行的进程树。</param>
    /// <returns>运行结果；排队期间被取消时 <see cref="TaskRunResult.ExitCode"/> 为 <see langword="null"/>。</returns>
    /// <exception cref="DirectoryNotFoundException">工作目录不存在。</exception>
    public async Task<TaskRunResult> RunAsync(CommandTask task, Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.WorkingDirectory);
        var directory = Path.GetFullPath(task.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            return new(id, task.Name, task.Command, directory, now, now, null, "", true);
        }

        try
        {
            var started = DateTimeOffset.UtcNow;
            using var process = new Process { StartInfo = CreateStartInfo(task.Command, directory) };
            var output = new BoundedTextBuffer(maxOutputCharacters);
            var outputLock = new object();
            ExceptionDispatchInfo? callbackError = null;
            if (!process.Start()) throw new InvalidOperationException("The command could not be started.");
            process.StandardInput.Close();
            // 注册在进程启动之后：若此前已请求取消，注册时会立即执行回调并结束进程。
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });

            // 读取一个输出流直到结束；两个流共用同一把锁，保证缓冲与回调的顺序一致。
            async Task PumpAsync(StreamReader reader)
            {
                var buffer = new char[4096];
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    var chunk = new string(buffer, 0, length);
                    lock (outputLock)
                    {
                        output.Append(chunk);
                        // 回调失败后不再调用它，但继续收集输出，避免子进程因管道写满而阻塞。
                        if (callbackError is null)
                        {
                            try { onOutput?.Invoke(chunk); }
                            catch (Exception exception) { callbackError = ExceptionDispatchInfo.Capture(exception); }
                        }
                    }
                }
            }

            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            callbackError?.Throw();
            return new(id, task.Name, task.Command, directory, started, DateTimeOffset.UtcNow,
                process.ExitCode, output.ToString(), cancellationToken.IsCancellationRequested, output.Truncated);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// 构造 Shell 启动参数。
    /// </summary>
    /// <param name="command">用户命令文本。</param>
    /// <param name="directory">工作目录。</param>
    /// <returns>重定向了标准输入、输出和错误的启动信息。</returns>
    private static ProcessStartInfo CreateStartInfo(string command, string directory)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh",
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            // cmd 有自己的引号语法；ArgumentList 使用 CRT 反斜杠转义，会把命令中的引号变成字面的 \"，
            // 因此这里直接拼接 Arguments。命令是用户有意输入的 Shell 文本，而不是被插值的路径。
            // cmd 在启动时缓存重定向输出的编码：先切换代码页为 UTF-8，再在新的 cmd 中执行命令。
            // 借助延迟变量展开，命令原样穿过外层 cmd 的解析：命令中带引号的 &、| 和 !
            // 只能由内层 cmd 在 chcp 完成之后解释。
            var commandVariable = "CREWTERM_TASK_COMMAND_" + Guid.NewGuid().ToString("N");
            start.Environment[commandVariable] = "\"" + command + "\"";
            start.Arguments = "/d /v:on /s /c \"chcp 65001 >nul & \"" + start.FileName
                + "\" /d /v:off /s /c !" + commandVariable + "!\"";
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        return start;
    }
}
