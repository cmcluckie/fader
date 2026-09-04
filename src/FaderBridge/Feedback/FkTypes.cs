namespace Fader.Bridge.Feedback;

/// <summary>Engine mode: analyse-only, detect-and-log, or deploy notches.</summary>
public enum FkMode { Off = 0, Assist = 1, Auto = 2 }

/// <summary>Telemetry subscription bits for <c>/fk/subscribe</c>.</summary>
[Flags]
public enum FkTelemetry { None = 0, Events = 1, Notches = 2, Spectrum = 4, Status = 8, All = 15 }

/// <summary><c>/fk/status</c> — is the engine's audio callback running, and its CPU load.</summary>
public sealed record FkStatus(bool EngineOk, float CpuLoad);

/// <summary><c>/fk/event</c> — a detection fired on a channel.</summary>
public sealed record FkDetection(int Channel, float Hz, float LevelDb,
                                 float AgeMs = 0f, float WidthLoHz = 0f, float WidthHiHz = 0f,
                                 int Path = 0)
{
    /// <summary>Which gate let it through - the thing every "why was that slow" needs.</summary>
    public string Gate => Path switch
    {
        1 => "growth", 2 => "sustain", 3 => "escalation", _ => "?",
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
