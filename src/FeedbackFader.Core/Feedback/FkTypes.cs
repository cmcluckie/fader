namespace FeedbackFader;

/// <summary>Engine mode: analyse-only, detect-and-log, or deploy notches.</summary>
public enum FkMode { Off = 0, Assist = 1, Auto = 2 }

/// <summary>Telemetry subscription bits for <c>/fk/subscribe</c>.</summary>
[Flags]
public enum FkTelemetry { None = 0, Events = 1, Notches = 2, Spectrum = 4, Status = 8, All = 15 }

/// <summary><c>/fk/status</c> — is the engine's audio callback running, and its CPU load.</summary>
/// <summary>Pid is the engine process that sent it; 0 from an engine too old to say.</summary>
public sealed record FkStatus(bool EngineOk, float CpuLoad, int Pid = 0);

/// <summary>
/// What the microphone is hearing, as opposed to what the guard is doing about
/// it: how loud, what note, how many independent voices, and how noise-like.
///
/// Families are counted rather than pitches - one fundamental carrying partials
/// is a voice, several at once is a backing track or a band, none is a room.
/// </summary>
/// <summary>
/// One ring followed across its hops, as the engine's track layer sees it.
///
/// A ring does not always stay put. Measured at the rig on 2026-09-27 one went
/// 9293 -> 10006 -> 10716 Hz, and on a 100-band log axis - where a single column
/// spans 7.2% - that entire journey is three columns. It reads as one peak
/// sitting still while nothing happens to it, which is exactly what it looked
/// like at the time. Heat carries the movement that the axis cannot.
/// </summary>
/// <param name="Heat">0 while it sits still, 1 once it has walked a full hop.</param>
public sealed record FkTrack(int Channel, int Index, float Hz, float LoHz, float HiHz, int Hops, float Heat);

public sealed record FkContext(float LevelDb, float F0Hz, int Families, float Flatness)
{
    /// <summary>Room, one voice, or something polyphonic.</summary>
    public string Scene => LevelDb < -70f ? "silent"
                         : Families == 0  ? "room"
                         : Families == 1  ? "voice"
                                          : "music";

    /// <summary>The note nearest F0, e.g. "D3" - empty when nothing is pitched.</summary>
    public string Note
    {
        get
        {
            if (F0Hz < 25f) return "";
            var semis = (int)Math.Round(12.0 * Math.Log2(F0Hz / 440.0)) + 57;   // A4 = 57
            if (semis < 0) return "";
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return $"{names[semis % 12]}{semis / 12}";
        }
    }
}

/// <summary><c>/fk/event</c> — a detection fired on a channel.</summary>
public sealed record FkDetection(int Channel, float Hz, float LevelDb,
                                 float AgeMs = 0f, float WidthLoHz = 0f, float WidthHiHz = 0f,
                                 int Path = 0, int Refused = 0)
{
    /// <summary>Why the bank declined to put a filter on this, if it did.</summary>
    public string Refusal => Refused switch
    {
        1 => "already-covered", 2 => "region-too-dark", 3 => "all-locked", _ => "placed",
    };

    /// <summary>Which gate let it through - the thing every "why was that slow" needs.</summary>
    public string Gate => Path switch
    {
        1 => "growth", 2 => "sustain", 3 => "escalation", 4 => "plateau", 5 => "runaway", _ => "?",
    };

    /// <summary>How wide the peak was, in Hz, at the frame that fired it.</summary>
    public float WidthHz => WidthHiHz > WidthLoHz ? WidthHiHz - WidthLoHz : 0f;
}

/// <summary>One notch slot, decoded from a <c>/fk/notches</c> frame.</summary>
public readonly record struct FkNotch(
    bool Active, bool Locked, bool Manual, float FreqHz, float CurrentDb, float TargetDb);

/// <summary><c>/fk/spectrum</c> — a downsampled magnitude frame for display.</summary>
public sealed record FkSpectrum(int Channel, float HzPerBin, float[] Magnitudes);

/// <summary><c>/fk/audio/state</c> — the engine's current device selection.</summary>
public sealed record FkAudioState(string Device, int SampleRate, int BufferSize, bool Running);

/// <summary>
/// <c>/fk/reject</c> - a candidate that looked like a peak but did not become a
/// detection, and which gate stopped it. Logging only what fired meant every
/// "it missed one" had to be reverse-engineered; this says why.
/// </summary>
public sealed record FkRejection(int Channel, float Hz, float LevelDb, int Reason, int Frames)
{
    public string Why => Reason switch
    {
        1 => "harmonic",     // looked like part of a series
        2 => "unstable",     // pitch wandered too far
        3 => "no-growth",    // not rising, and not old enough to be a sustained ring
        4 => "vibrato", 5 => "drifting",      // wobbling like a sung note
        _ => "unknown",
    };
}

/// <summary>Outcome of a signal-path check: did our tone reach the console?</summary>
public sealed record PathCheckResult(bool Reached, int Channel, float Rise, string Message);
