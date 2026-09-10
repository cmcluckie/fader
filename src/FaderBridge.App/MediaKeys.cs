using System.Diagnostics;
using Fader.Bridge;

namespace Fader.Bridge.App;

/// <summary>
/// Turns the FaderPort's transport presses into music-player control on macOS.
/// Uses AppleScript against whichever supported player is running - Spotify if
/// it is open, otherwise Apple Music - so Play/Stop and prev/next "just work"
/// for the app you're actually listening to.
/// </summary>
public static class MediaKeys
{
    public static void Handle(TransportCommand command)
    {
        var action = command switch
        {
            TransportCommand.Next => "next track",
            TransportCommand.Previous => "previous track",
            TransportCommand.PlayPause => "playpause",
            TransportCommand.Stop => "pause",
            _ => null,
        };

        if (action is null)
        {
            return;
        }

        Osascript($"tell application \"{Player()}\" to {action}");
    }

    /// <summary>Spotify if it is already running, else Apple Music (the default).</summary>
    private static string Player()
    {
        try
        {
            var running = OsascriptCapture("application \"Spotify\" is running");
            if (running.Trim() == "true")
            {
                return "Spotify";
            }
        }
        catch
        {
            // Fall through to the default.
        }

        return "Music";
    }

    private static void Osascript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
        }
        catch
        {
            // Best effort - a music button should never crash the bridge.
        }
    }

    private static string OsascriptCapture(string script)
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
        process.WaitForExit(1000);
        return output;
    }
}
