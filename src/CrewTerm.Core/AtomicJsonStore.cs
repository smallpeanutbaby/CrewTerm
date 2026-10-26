using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace CrewTerm.Core;

public static class AtomicJsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Load<T>(string path)
        => Load<T>(path, out _);

    public static T? Load<T>(string path, out string? fingerprint)
    {
        fingerprint = null;
        if (!File.Exists(path)) return default;
        try
        {
            var bytes = File.ReadAllBytes(path);
            fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            return JsonSerializer.Deserialize<T>(bytes, Options)
                ?? throw new InvalidDataException($"Settings contain null: {path}");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Cannot read settings; original file preserved: {path}", error);
        }
    }

    public static string SaveIfUnchanged<T>(string path, T value, string? fingerprint)
    {
        var key = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        using var gate = new Mutex(false, "CrewTerm-json-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        var held = false;
        try
        {
            try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Settings are busy in another client");
            var actual = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
            if (actual != fingerprint) throw new IOException($"Settings changed in another client. Reload before saving: {path}");
            Save(path, value);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { if (held) gate.ReleaseMutex(); }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, value, Options);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
