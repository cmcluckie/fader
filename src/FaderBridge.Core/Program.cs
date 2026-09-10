using Fader.Bridge;
using Fader.Bridge.Midi;
using Fader.Bridge.Osc;

var configPath = args.ElementAtOrDefault(0) ?? "config.json";

Console.WriteLine("FaderPort 8  <->  X32 Rack bridge");
Console.WriteLine("=================================\n");

BridgeConfig config;
try
{
    config = BridgeConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Config error ({configPath}): {ex.Message}");
    return 1;
}

var surface = new FaderPortDevice();
surface.Log += m => Console.WriteLine($"[midi] {m}");

try
{
    await surface.OpenAsync(config.MidiPortName);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nMIDI error: {ex.Message}");
    Console.Error.WriteLine(MidiBackend.Troubleshooting);
    return 1;
}

Console.WriteLine($"Surface in   : {surface.InputName}");
Console.WriteLine($"Surface out  : {surface.OutputName}");

await using var console = new X32Client(
    config.ResolvedAddress, config.X32Port, TimeSpan.FromSeconds(config.KeepaliveSeconds));
console.Error += m => Console.WriteLine($"[osc] {m}");

Console.WriteLine($"Console      : {console.Target}");
Console.WriteLine($"Local port   : {console.LocalPort}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

console.Start(shutdown.Token);

var bridge = new BridgeHost(config, surface, console);
bridge.Log += m => Console.WriteLine($"[bridge] {m}");
bridge.Start(shutdown.Token);

Console.WriteLine($"""

    Bridged channels 1-{config.StripCount} (bank window over {config.X32ChannelCount} channels).
    /xremote keepalive every {config.KeepaliveSeconds}s.

      Bank Left/Right     shift the window by {config.StripCount}
      Channel Left/Right  shift the window by 1

    Ctrl+C to stop.
    """);

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException)
{
    // expected on Ctrl+C
}

Console.WriteLine("\nStopping - clearing the surface...");
surface.Reset(config.StripCount);
await Task.Delay(150); // let the final SysEx drain before the port closes
await surface.DisposeAsync();
Console.WriteLine("Stopped.");
return 0;
