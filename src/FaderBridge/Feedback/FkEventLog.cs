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
    private readonly object _lock = new();

    public string Path { get; }

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
        }
        catch
        {
            _writer = null;   // logging is best-effort; never fatal
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
        }
    }
}
