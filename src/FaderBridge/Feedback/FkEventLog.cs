using System.Globalization;

namespace Fader.Bridge.Feedback;

/// <summary>
/// Writes detection events to a CSV, the same columns the plugin wrote so the
/// user's ASSIST-then-read-the-log workflow is unchanged. Logging lives on the
/// C# side now (§4) - the engine only emits <c>/fk/event</c>, never touches a
/// file on the audio thread.
/// </summary>
public sealed class FkEventLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly StreamWriter? _eq;
    private StreamWriter? _capture;
    private readonly string _directory;
    private readonly object _lock = new();

    public string Path { get; }
    public string EqPath { get; private set; } = string.Empty;
    public string CapturePath { get; private set; } = string.Empty;

    public static string ChannelName(int ch) => ch == 0 ? "LEAD" : "BGV";

    public FkEventLog(string directory, string? stamp = null)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        stamp ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Path = System.IO.Path.Combine(directory, $"feedback-log-{stamp}.csv");
        try
        {
            _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
            _writer.WriteLine("seconds,channel,frequency_hz,level_db,applied");

            EqPath = System.IO.Path.Combine(directory, $"eq-log-{stamp}.csv");
            _eq = new StreamWriter(EqPath, append: false) { AutoFlush = true };
            _eq.WriteLine("seconds,channel,bypassed,filters,avg_1k_4k,avg_4k_16k,worst_db,worst_hz");
        }
        catch
        {
            _writer = null; _eq = null;   // logging is best-effort; never fatal
        }
    }

    /// <summary>
    /// The capture log: one row per detection, with the measurements that can only
    /// be taken at the moment of firing.
    ///
    /// Opened on demand, because it is a diagnostic and not something to accumulate
    /// through every gig. "How long did that take to catch" and "how wide was it"
    /// have been the two questions behind every investigation here, and both were
    /// being reconstructed after the fact from a downsampled spectrum. Recorded at
    /// the source they are exact. The guard column is the point: with capture on,
    /// detection keeps running while the guard is off, so a session played half on
    /// and half off yields two comparable sets rather than one set and a silence.
    /// </summary>
    public void WriteCapture(double seconds, int slot, bool guardOn, FkDetection d)
    {
        if (_capture is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F3},{ChannelName(slot)},{(guardOn ? 1 : 0)},{d.Hz:F1},{d.LevelDb:F1}," +
            $"{d.AgeMs:F0},{d.WidthLoHz:F1},{d.WidthHiHz:F1},{d.WidthHz:F1}");
        lock (_lock)
        {
            _capture.WriteLine(line);
        }
    }

    /// <summary>Start (or stop) the capture log. Off unless explicitly asked for.</summary>
    public void SetCapture(bool on)
    {
        lock (_lock)
        {
            if (on == (_capture is not null)) return;
            if (! on) { _capture?.Flush(); _capture?.Dispose(); _capture = null; return; }
            try
            {
                CapturePath = System.IO.Path.Combine(
                    _directory, $"capture-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _capture = new StreamWriter(CapturePath, append: false) { AutoFlush = true };
                _capture.WriteLine("seconds,channel,guard_on,frequency_hz,level_db," +
                                   "age_ms,width_lo_hz,width_hi_hz,width_hz");
            }
            catch { _capture = null; }
        }
    }

    /// <summary>
    /// A second log, alongside the detections: what the guard is doing to the
    /// SOUND, once a second.
    ///
    /// Detections say what was caught. They say nothing about the cost, and the
    /// cost is what "it sounds muffled" is about - twenty-two individually correct
    /// filters summing to a -13.6 dB high-cut, with nothing anywhere recording
    /// that number. Bypass is logged beside it so an on/off comparison is a
    /// subtraction rather than a memory of how it sounded a minute ago.
    /// </summary>
    public void WriteEq(double seconds, int slot, bool bypassed, int filters,
                        double avgLowDb, double avgTopDb, double worstDb, double worstHz)
    {
        if (_eq is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F1},{ChannelName(slot)},{(bypassed ? 1 : 0)},{filters}," +
            $"{avgLowDb:F2},{avgTopDb:F2},{worstDb:F2},{worstHz:F0}");
        lock (_lock)
        {
            _eq.WriteLine(line);
        }
    }

    /// <param name="applied">True when AUTO deployed a notch; false when merely flagged (ASSIST/OFF).</param>
    public void Write(FkDetection d, bool applied, double seconds)
    {
        if (_writer is null)
        {
            return;
        }
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F3},{ChannelName(d.Channel)},{d.Hz:F2},{d.LevelDb:F2},{(applied ? 1 : 0)}");
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _eq?.Flush();
            _eq?.Dispose();
            _capture?.Flush();
            _capture?.Dispose();
        }
    }
}
