using System.Text.Json;

namespace Fader.Shared.Net;

/// <summary>
/// Remembers the last X32 address that actually answered, so neither product
/// has to be told the IP twice. Stored in per-user app data, NOT in the repo:
///   Windows  %APPDATA%\Fader\x32.json
///   macOS    ~/.config/Fader/x32.json   (.NET maps ApplicationData to XDG here)
/// Both products point at the same file by default, so discovering the console
/// in one is remembered by the other.
/// </summary>
public sealed class X32AddressStore
{
    private readonly string _path;

    public X32AddressStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Fader");
        return Path.Combine(dir, "x32.json");
    }

    /// <summary>The last address that answered, or null if none has ever been saved.</summary>
    public string? LoadLast()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(_path));
            return string.IsNullOrWhiteSpace(record?.LastAddress) ? null : record.LastAddress;
        }
        catch
        {
            // A corrupt or unreadable file must never stop the app starting;
            // it just means "no memory", and discovery takes over.
            return null;
        }
    }

    public void SaveLast(string address)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(
                new Record(address, DateTimeOffset.UtcNow),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Persisting is best-effort. Failing to remember is not worth
            // crashing over - the app still works this session.
        }
    }

    private sealed record Record(string LastAddress, DateTimeOffset SavedUtc);
}
