using System.Diagnostics;
using Fader.Bridge;
using Fader.Bridge.Midi;

namespace Fader.Bridge.App;

/// <summary>
/// Scrolls the currently-playing track across the FaderPort's eight scribble
/// strips - title on the top row, artist + album on the bottom - while music
/// plays. Polls the player every couple of seconds and advances the marquee a
/// few times a second. When nothing is playing, or the feature is switched off,
/// it releases the strips and the bridge repaints its channel labels.
/// </summary>
public sealed class NowPlaying : IDisposable
{
    private const int Width = McuProtocol.RowWidth;   // 56 chars across the 8 strips
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScrollInterval = TimeSpan.FromMilliseconds(280);

    private readonly IControlSurface _surface;
    private readonly BridgeHost _host;
    private readonly object _lock = new();

    private Timer? _poll;
    private Timer? _scroll;

    private string _topBase = "";
    private string _bottomBase = "";
    private string _top = "";        // marquee (raw text + gap if it scrolls)
    private string _bottom = "";
    private int _offset;
    private bool _showing;           // currently owns the strips
    private bool _enabled = true;

    public NowPlaying(IControlSurface surface, BridgeHost host)
    {
        _surface = surface;
        _host = host;
    }

    public void Start()
    {
        _poll = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        _scroll = new Timer(_ => Tick(), null, ScrollInterval, ScrollInterval);
    }

    public void SetEnabled(bool on)
    {
        bool release = false;
        lock (_lock)
        {
            _enabled = on;
            if (!on && _showing)
            {
                _showing = false;
                release = true;
            }
        }

        if (release)
        {
            _host.SetDisplayOverride(false);   // give the strips back to the bridge
        }
    }

    private void Poll()
    {
        var track = Fetch();   // process I/O, kept outside the lock

        bool takeOver = false, release = false;
        lock (_lock)
        {
            if (!_enabled)
            {
                return;
            }

            if (track is null)
            {
                if (_showing)
                {
                    _showing = false;
                    release = true;
                }
            }
            else
            {
                var (title, artist, album) = track.Value;
                var top = title.Trim();
                var bottom = string.Join("   ",
                    new[] { artist.Trim(), album.Trim() }.Where(s => s.Length > 0));

                if (top != _topBase || bottom != _bottomBase)
                {
                    _topBase = top;
                    _bottomBase = bottom;
                    _top = Scrollable(top);
                    _bottom = Scrollable(bottom);
                    _offset = 0;
                }

                if (!_showing)
                {
                    _showing = true;
                    takeOver = true;
                }
            }
        }

        if (takeOver) _host.SetDisplayOverride(true);
        if (release) _host.SetDisplayOverride(false);
    }

    private void Tick()
    {
        string top, bottom;
        lock (_lock)
        {
            if (!_showing || !_enabled)
            {
                return;
            }
            top = Frame(_top, _offset);
            bottom = Frame(_bottom, _offset);
            _offset++;
        }

        _surface.SetScribbleLine(0, top);
        _surface.SetScribbleLine(1, bottom);
    }

    /// <summary>Add a trailing gap only when the text is long enough to scroll.</summary>
    private static string Scrollable(string text) =>
        text.Length > Width ? text + "     " : text;

    /// <summary>A Width-char window into the marquee; static when it already fits.</summary>
    private static string Frame(string marquee, int offset)
    {
        if (marquee.Length == 0)
        {
            return new string(' ', Width);
        }
        if (marquee.Length <= Width)
        {
            return marquee.PadRight(Width);
        }

        var buffer = new char[Width];
        for (var i = 0; i < Width; i++)
        {
            buffer[i] = marquee[(offset + i) % marquee.Length];
        }
        return new string(buffer);
    }

    /// <summary>Title/artist/album of the playing track, or null if nothing plays.</summary>
    private static (string Title, string Artist, string Album)? Fetch()
    {
        const string script = """
            set out to ""
            if application "Spotify" is running then
            	tell application "Spotify"
            		if player state is playing then
            			set out to (name of current track) & tab & (artist of current track) & tab & (album of current track)
            		end if
            	end tell
            end if
            if out is "" and application "Music" is running then
            	tell application "Music"
            		if player state is playing then
            			set out to (name of current track) & tab & (artist of current track) & tab & (album of current track)
            		end if
            	end tell
            end if
            return out
            """;

        var output = Osascript(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var parts = output.Trim().Split('\t');
        return (
            parts.ElementAtOrDefault(0) ?? "",
            parts.ElementAtOrDefault(1) ?? "",
            parts.ElementAtOrDefault(2) ?? "");
    }

    private static string Osascript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            return output;
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _scroll?.Dispose();

        bool release;
        lock (_lock)
        {
            release = _showing;
            _showing = false;
        }
        if (release)
        {
            _host.SetDisplayOverride(false);
        }
    }
}
