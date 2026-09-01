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
    private readonly object _lock = new();

    public string Path { get; }
    public string EqPath { get; private set; } = string.Empty;

    public static string ChannelName(int ch) => ch == 0 ? "LEAD" : "BGV";

    public FkEventLog(string directory, string? stamp = null)
    {
        Directory.CreateDirectory(directory);
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
        }
    }
}
