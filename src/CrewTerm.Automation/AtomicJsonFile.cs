using System.Collections.Concurrent;
using System.Text.Json;

namespace CrewTerm.Automation;

internal static class AtomicJsonFile
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static object Gate(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static T Read<T>(string path, Func<T> createEmpty)
    {
        if (!File.Exists(path)) return createEmpty();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"The JSON file is empty or null: {path}");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The JSON file is damaged; its original bytes were preserved: {path}", error);
        }
    }

    public static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
