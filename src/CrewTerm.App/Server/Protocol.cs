using CrewTerm.Terminal;
using CrewTerm.Automation;
using System.Text.Json;

namespace CrewTerm.App.Server;

public sealed record DaemonRequest(string Operation, string? SessionId = null, string? Command = null,
    string? WorkingDirectory = null, string? Text = null, int Columns = 100, int Rows = 30, string? Name = null,
    CommandTask[]? Steps = null);
public sealed record SessionInfo(string Id, string Name, string Command, string WorkingDirectory,
    DateTimeOffset StartedAt, bool HasExited, int? ExitCode);
public sealed record DaemonResponse(bool Ok, string? Error = null, string? SessionId = null,
    SessionInfo[]? Sessions = null, TerminalSnapshot? Screen = null, string? Text = null,
    DaemonTaskInfo[]? Tasks = null, DaemonTaskInfo? Task = null);

internal static class Protocol
{
    public const int MaxRequest = 131072;
    public const int MaxResponse = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<string> ReadLineAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= maximum) throw new InvalidDataException("Protocol message exceeds maximum size");
            text.Append(buffer[0]);
        }
        throw new EndOfStreamException("Daemon connection ended before a complete response");
    }
}
