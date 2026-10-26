using CrewTerm.Terminal.Emulation;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// 一个运行在伪终端中的进程会话：持有进程树、把输出喂给屏幕模型，并接受输入与尺寸调整。
/// </summary>
/// <remarks>
/// Windows 上由 <see cref="ConPtySession"/>（ConPTY）实现，Linux 与 macOS 上由 <see cref="UnixPtySession"/>（<c>forkpty</c>）实现；
/// 调用方通过 <see cref="TerminalSession.Start"/> 获得与当前平台匹配的实现。
/// 全部成员都可以从任意线程调用；<see cref="IDisposable.Dispose"/> 结束整个进程树并释放原生资源，可重复调用。
/// </remarks>
public interface ITerminalSession : IDisposable
{
    /// <summary>
    /// 会话标识。
    /// </summary>
    string Id { get; }

    /// <summary>
    /// 用户提供的原始命令行（未经程序路径解析）。
    /// </summary>
    string CommandLine { get; }

    /// <summary>
    /// 进程工作目录（绝对路径）。
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// 根进程的进程标识。
    /// </summary>
    int ProcessId { get; }

    /// <summary>
    /// 由输出驱动的屏幕状态。
    /// </summary>
    TerminalScreen Screen { get; }

    /// <summary>
    /// 进程是否已退出（会话已释放也视为已退出）。
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// 进程退出码；仍在运行或无法获知时为 <see langword="null"/>。Unix 上被信号结束时为 128 加信号编号。
    /// </summary>
    int? ExitCode { get; }

    /// <summary>
    /// 每收到一段输出（已解码为文本、已送入 <see cref="Screen"/>）时在读取线程上触发；回调的异常不会中断输出读取。
    /// </summary>
    event Action<string>? OutputReceived;

    /// <summary>
    /// 向进程写入输入（按键编码、粘贴内容等）。
    /// </summary>
    /// <param name="text">要写入的文本。</param>
    /// <exception cref="ObjectDisposedException">会话已释放。</exception>
    /// <exception cref="InvalidOperationException">进程已退出。</exception>
    void Write(string text);

    /// <summary>
    /// 调整伪终端与屏幕模型的尺寸。
    /// </summary>
    /// <param name="columns">列数（1–500）。</param>
    /// <param name="rows">行数（1–300）。</param>
    /// <exception cref="ObjectDisposedException">会话已释放。</exception>
    void Resize(int columns, int rows);
}
