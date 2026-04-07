// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.MultiOutputPlayer
//   1. Pick a PortAudio host API
//   2. Pick PRIMARY output device  (leader — drives the clock)
//   3. Pick SECONDARY output device (fan-out sink — receives a copy of the mix)
//   4. Enter an audio file path
//   5. Play to both devices simultaneously via AggregateOutput
//      Press Enter or Ctrl+C to stop; auto-stops at EOF
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.PortAudio;

Console.WriteLine("╔═══════════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Multi-Output Player    ║");
Console.WriteLine("╚═══════════════════════════════════════╝\n");

ffmpeg.RootPath = "/lib";

// ── 1. Initialise PortAudio ──────────────────────────────────────────────────

using var engine = new PortAudioEngine();
engine.Initialize();

// ── 2. Pick Host API ─────────────────────────────────────────────────────────

var apis = engine.GetHostApis();
Console.WriteLine("Available Host APIs:");
for (int i = 0; i < apis.Count; i++)
    Console.WriteLine($"  [{i}]  {apis[i].Name}  ({apis[i].DeviceCount} device{(apis[i].DeviceCount == 1 ? "" : "s")})");

int apiIdx      = PickNumber("Select API", 0, apis.Count - 1);
var selectedApi = apis[apiIdx];

// ── 3. Enumerate output devices for the chosen API ───────────────────────────

var outputDevices = engine.GetDevices()
    .Where(d => d.HostApiIndex == selectedApi.Index && d.MaxOutputChannels > 0)
    .ToList();

if (outputDevices.Count < 1)
{
    Console.WriteLine($"No output devices found for API '{selectedApi.Name}'.");
    return;
}

PrintDeviceList(outputDevices, selectedApi.Name);

// ── 4. Pick PRIMARY (leader) device ─────────────────────────────────────────

int primaryIdx = PickNumber("\nSelect PRIMARY device (drives clock)", 0, outputDevices.Count - 1);
var primaryDevice = outputDevices[primaryIdx];
Console.WriteLine($"  Primary:   {primaryDevice.Name}");

// ── 5. Pick SECONDARY (sink) device ──────────────────────────────────────────

int secondaryIdx = PickNumber("Select SECONDARY device (receives mix copy)", 0, outputDevices.Count - 1);
var secondaryDevice = outputDevices[secondaryIdx];
Console.WriteLine($"  Secondary: {secondaryDevice.Name}");

// ── 6. Enter file path ───────────────────────────────────────────────────────

Console.Write("\nAudio file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 7. Open decoder ───────────────────────────────────────────────────────────

Console.Write("\nOpening decoder… ");
FFmpegDecoder decoder;
try
{
    decoder = FFmpegDecoder.Open(filePath);
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED\n  {ex.Message}");
    return;
}

if (decoder.AudioChannels.Count == 0)
{
    Console.WriteLine("No audio streams in file.");
    decoder.Dispose();
    return;
}

using (decoder)
{
    var audioChannel = decoder.AudioChannels[0];
    var srcFmt       = audioChannel.SourceFormat;

    // Cap to stereo; use file's native sample rate on both outputs.
    int outChannels = Math.Min(srcFmt.Channels, Math.Min(primaryDevice.MaxOutputChannels, 2));
    var hwFmt       = new AudioFormat(srcFmt.SampleRate, outChannels);
    var routeMap    = BuildRouteMap(srcFmt.Channels, outChannels);

    Console.WriteLine("OK");
    Console.WriteLine($"  Source:   {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");
    Console.WriteLine($"  Output:   {hwFmt.SampleRate} Hz / {outChannels} ch");

    // ── 8. Open primary output ───────────────────────────────────────────────

    Console.Write($"Opening primary device '{primaryDevice.Name}'… ");
    var primaryOutput = new PortAudioOutput();
    try
    {
        primaryOutput.Open(primaryDevice, hwFmt, framesPerBuffer: 512);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        primaryOutput.Dispose();
        return;
    }
    Console.WriteLine("OK");

    // Wrap in AggregateOutput so we can fan-out to the secondary device.
    // AggregateOutput takes ownership of primaryOutput and disposes it.
    using var aggregate = new AggregateOutput(primaryOutput);

    // ── 9. Open secondary sink ───────────────────────────────────────────────

    // Use the actual negotiated format from the primary (in case sample rate changed).
    var negotiatedFmt = primaryOutput.HardwareFormat;

    Console.Write($"Opening secondary device '{secondaryDevice.Name}'… ");
    PortAudioSink secondarySink;
    try
    {
        secondarySink = new PortAudioSink(
            secondaryDevice,
            targetFormat:   negotiatedFmt,
            framesPerBuffer: 512,
            name:           $"Sink({secondaryDevice.Name})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    // AggregateOutput will start and dispose the sink via StartAsync / Dispose.
    aggregate.AddSink(secondarySink);

    // ── 10. Wire audio channel ───────────────────────────────────────────────

    // Add the channel to the mixer with a route for the leader (primary) output.
    aggregate.Mixer.AddChannel(audioChannel, routeMap);

    // With ChannelFallback.Silent (the default), sinks only receive audio when
    // explicitly routed.  Route the same channel+map to the secondary sink so
    // both outputs play the identical mix.
    // For different audio on each output, pass a different ChannelRouteMap here.
    aggregate.Mixer.RouteTo(audioChannel.Id, secondarySink, routeMap);

    // ── 11. EOF detection ────────────────────────────────────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var sw = Stopwatch.StartNew();
    audioChannel.BufferUnderrun += (_, _) =>
    {
        if (sw.Elapsed.TotalSeconds > 2 && !cts.IsCancellationRequested)
        {
            Console.WriteLine("\n[EOF reached]");
            cts.Cancel();
        }
    };

    // ── 12. Start playback ───────────────────────────────────────────────────

    decoder.Start();
    await aggregate.StartAsync(); // starts primary PA stream + secondary PA stream

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine($"  → {primaryDevice.Name}  (primary)");
    Console.WriteLine($"  → {secondaryDevice.Name}  (secondary)");
    Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

    _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // ── 13. Stop ─────────────────────────────────────────────────────────────

    Console.Write("\nStopping… ");
    await aggregate.StopAsync(); // stops sink, then primary
    Console.WriteLine("Done.");
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static int PickNumber(string label, int min, int max)
{
    while (true)
    {
        Console.Write($"{label} [{min}–{max}] (default {min}): ");
        string? line = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(line)) return min;
        if (int.TryParse(line, out int v) && v >= min && v <= max) return v;
        Console.WriteLine($"  Please enter a number between {min} and {max}.");
    }
}

static void PrintDeviceList(List<AudioDeviceInfo> devices, string apiName)
{
    Console.WriteLine($"\nOutput devices on  {apiName}:");
    for (int i = 0; i < devices.Count; i++)
        Console.WriteLine($"  [{i}]  {devices[i].Name}  " +
                          $"(ch: {devices[i].MaxOutputChannels},  " +
                          $"{devices[i].DefaultSampleRate:0} Hz)");
}

static ChannelRouteMap BuildRouteMap(int srcChannels, int dstChannels)
{
    var b = new ChannelRouteMap.Builder();
    if (srcChannels == 1 && dstChannels >= 2)
    {
        b.Route(0, 0).Route(0, 1); // mono → both stereo channels
    }
    else
    {
        int common = Math.Min(srcChannels, dstChannels);
        for (int i = 0; i < common; i++) b.Route(i, i);
    }
    return b.Build();
}

