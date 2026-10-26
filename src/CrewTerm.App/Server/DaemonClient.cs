using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrewTerm.App.Server;

public sealed class DaemonClient(string stateDirectory)
{
    public static string PipeName(string stateDirectory)
    {
        var path = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        return "CrewTerm-" + Environment.UserName + "-" + key;
    }

    public async Task<DaemonResponse> CallAsync(DaemonRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", PipeName(stateDirectory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(500, timeout.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var json = JsonSerializer.Serialize(request, Protocol.Json);
        if (json.Length > Protocol.MaxRequest) throw new InvalidDataException("Input too large for one request");
        await writer.WriteLineAsync(json.AsMemory(), timeout.Token);
        var response = await Protocol.ReadLineAsync(reader, Protocol.MaxResponse, timeout.Token);
        return JsonSerializer.Deserialize<DaemonResponse>(response, Protocol.Json)
            ?? throw new InvalidDataException("Invalid daemon response");
    }
}
