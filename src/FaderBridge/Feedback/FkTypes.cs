namespace Fader.Bridge.Feedback;

/// <summary>Engine mode: analyse-only, detect-and-log, or deploy notches.</summary>
public enum FkMode { Off = 0, Assist = 1, Auto = 2 }

/// <summary>Telemetry subscription bits for <c>/fk/subscribe</c>.</summary>
[Flags]
public enum FkTelemetry { None = 0, Events = 1, Notches = 2, Spectrum = 4, Status = 8, All = 15 }

/// <summary><c>/fk/status</c> — is the engine's audio callback running, and its CPU load.</summary>
public sealed record FkStatus(bool EngineOk, float CpuLoad);

/// <summary><c>/fk/event</c> — a detection fired on a channel.</summary>
public sealed record FkDetection(int Channel, float Hz, float LevelDb);

/// <summary>One notch slot, decoded from a <c>/fk/notches</c> frame.</summary>
public readonly record struct FkNotch(
    bool Active, bool Locked, bool Manual, float FreqHz, float CurrentDb, float TargetDb);

/// <summary><c>/fk/spectrum</c> — a downsampled magnitude frame for display.</summary>
public sealed record FkSpectrum(int Channel, float HzPerBin, float[] Magnitudes);

/// <summary><c>/fk/audio/state</c> — the engine's current device selection.</summary>
public sealed record FkAudioState(string Device, int SampleRate, int BufferSize, bool Running);
