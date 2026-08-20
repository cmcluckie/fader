using System.Text.Json;

namespace Fader.Bridge.Feedback;

/// <summary>The persisted audio selection: device, the two input channels, and per-channel suppress.</summary>
public sealed record AudioSelection(
    string? Device, int Lead, int Bgv, bool SuppressLead = false, bool SuppressBgv = false)
{
    public static readonly AudioSelection Default = new(null, 0, 1);
}

/// <summary>
/// Persists the engine's audio device and channel choice so it is set once, not
/// every launch. The Apollo's internal routing (which physical input feeds which
/// ADAT channel) lives in UA Console and is not visible to Core Audio, so the
/// channel-to-vocal mapping can only come from the user - this remembers it.
/// </summary>
public sealed class FkAudioStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public FkAudioStore(string path) => _path = path;

    public AudioSelection Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AudioSelection.Default;
            }
            return JsonSerializer.Deserialize<AudioSelection>(File.ReadAllText(_path)) ?? AudioSelection.Default;
        }
        catch
        {
            return AudioSelection.Default;
        }
    }

    public void Save(AudioSelection selection)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(selection, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // best-effort; a lost preference must never take audio down
        }
    }
}
