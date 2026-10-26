using CrewTerm.Terminal.Pty;

namespace CrewTerm.App.Server;

/// <summary>
/// daemon 中一个正在运行（或已退出但尚未关闭）的终端会话。
/// </summary>
/// <param name="Terminal">伪终端会话（Windows 为 ConPTY，Linux / macOS 为 forkpty），由 daemon 负责释放。</param>
/// <param name="Name">显示名称。</param>
/// <param name="StartedAt">本次进程的启动时间（UTC）。</param>
internal sealed record DaemonSession(ITerminalSession Terminal, string Name, DateTimeOffset StartedAt);
