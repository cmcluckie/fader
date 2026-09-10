using System.Text.Json;

namespace FeedbackFader;

/// <summary>A locked notch as persisted between runs.</summary>
public sealed record StoredNotch(int Channel, float Hz, float DepthDb, bool Manual);

/// <summary>
/// Persists the locked notch filters so they survive a restart (§8). Per the
/// ADR the engine is stateless across restarts, so this lives on the C# side and
/// is replayed to the engine on each (re)launch. Only locked filters are saved -
/// live ones are transient by design.
/// </summary>
public sealed class FkNotchStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public FkNotchStore(string path) => _path = path;

    public string Path => _path;

    public IReadOnlyList<StoredNotch> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<StoredNotch>();
            }
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<StoredNotch>>(json) ?? new List<StoredNotch>();
        }
        catch
        {
            // A corrupt store must not stop the engine coming up; start clean.
            return Array.Empty<StoredNotch>();
        }
    }

    public void Save(IReadOnlyList<StoredNotch> notches)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            // Write-then-rename so a crash mid-write cannot leave a half file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(notches, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; losing it must never take audio down.
        }
    }
}
