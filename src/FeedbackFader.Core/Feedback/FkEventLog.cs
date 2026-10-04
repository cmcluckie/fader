using System.Globalization;

namespace FeedbackFader;

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
    private readonly StreamWriter? _rej;
    private StreamWriter? _capture;
    private readonly string _directory;
    private readonly object _lock = new();

    public string Path { get; }
    public string EqPath { get; private set; } = string.Empty;
    /// <summary>Every rejection, as it happens. Until 2026-09-30 these lived in a 400-entry
    /// memory queue and reached disk only when the operator pressed the label button;
    /// three presses on 09-28 wrote three files containing zero of them.</summary>
    public string RejectPath { get; private set; } = "";
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
            _writer.WriteLine("seconds,channel,frequency_hz,level_db,applied,gate,filter,cut_here_db,nearest_notch_hz,age_ms");

            EqPath = System.IO.Path.Combine(directory, $"eq-log-{stamp}.csv");
            _eq = new StreamWriter(EqPath, append: false) { AutoFlush = true };
            // scene/note/level are what the MIC was hearing, not what the guard
            // did about it: the two questions were impossible to separate when
            // only one of them was written down.
            RejectPath = System.IO.Path.Combine(directory, $"reject-log-{stamp}.csv");
            _rej = new StreamWriter(RejectPath, append: false) { AutoFlush = true };
            _rej.WriteLine("seconds,channel,frequency_hz,level_db,reason,frames");
            _eq.WriteLine("seconds,channel,bypassed,filters,avg_1k_4k,avg_4k_16k,worst_db,worst_hz," +
                          "scene,note,f0_hz,level_db,families");
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
    public void WriteCapture(double seconds, int slot, bool guardOn, FkDetection d,
                             double cutHere, double nearestNotchHz)
    {
        if (_capture is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F3},{ChannelName(slot)},{(guardOn ? 1 : 0)},{d.Hz:F1},{d.LevelDb:F1}," +
            $"{d.AgeMs:F0},{d.WidthLoHz:F1},{d.WidthHiHz:F1},{d.WidthHz:F1},{d.Gate}," +
            $"{cutHere:F1},{nearestNotchHz:F0},{d.Refusal}");
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
                                   "age_ms,width_lo_hz,width_hi_hz,width_hz,gate," +
                                   "cut_here_db,nearest_notch_hz,filter");
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
                        double avgLowDb, double avgTopDb, double worstDb, double worstHz,
                        FkContext? ctx = null)
    {
        if (_eq is null) return;
        var c = ctx ?? new FkContext(-120f, 0f, 0, 0f);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F1},{ChannelName(slot)},{(bypassed ? 1 : 0)},{filters}," +
            $"{avgLowDb:F2},{avgTopDb:F2},{worstDb:F2},{worstHz:F0}," +
            $"{c.Scene},{c.Note},{c.F0Hz:F0},{c.LevelDb:F1},{c.Families}");
        lock (_lock)
        {
            _eq.WriteLine(line);
        }
    }

    /// <param name="applied">True when AUTO deployed a notch; false when merely flagged (ASSIST/OFF).</param>
    /// <summary>A suspect that waited long enough and was declined, and why.</summary>
    public void WriteReject(double seconds, int slot, FkRejection r)
    {
        _rej?.WriteLine(FormattableString.Invariant(
            $"{seconds:F3},{slot},{r.Hz:F1},{r.LevelDb:F1},{r.Why},{r.Frames}"));
    }

    /// <remarks>
    /// The last four columns are what the bank DID about the detection: which gate
    /// let it through, whether a filter was placed or why not, how much cut was
    /// already on that frequency, and where the nearest filter sat. They used to
    /// live only in the capture log, behind a button - and on 2026-10-03 the
    /// button had to be pressed three times in one evening to find out that
    /// "placed" meant a filter 300 Hz away. Detected and suppressed are separate
    /// facts; both are written, always.
    /// </remarks>
    public void Write(FkDetection d, bool applied, double seconds, double cutHere = 0, double nearestNotchHz = 0)
    {
        if (_writer is null)
        {
            return;
        }
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{seconds:F3},{ChannelName(d.Channel)},{d.Hz:F2},{d.LevelDb:F2},{(applied ? 1 : 0)}," +
            $"{d.Gate},{d.Refusal},{cutHere:F1},{nearestNotchHz:F0},{d.AgeMs:F0}");
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
            _rej?.Dispose();
            _capture?.Flush();
            _capture?.Dispose();
        }
    }
}
