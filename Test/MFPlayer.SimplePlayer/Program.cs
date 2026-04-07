// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.SimplePlayer
//   1. Pick a PortAudio host API
//   2. Pick an output device
//   3. Enter an audio file path
//   4. Play — press Enter or Ctrl+C to stop; auto-stops at EOF
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.PortAudio;

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Simple Player  ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = "/lib";

// ── 1. Initialise PortAudio ──────────────────────────────────────────────────

using var engine = new PortAudioEngine();
engine.Initialize();

// ── 2. Pick Host API ─────────────────────────────────────────────────────────

var apis = engine.GetHostApis();
Console.WriteLine("Available Host APIs:");
for (int i = 0; i < apis.Count; i++)
    Console.WriteLine($"  [{i}]  {apis[i].Name}  ({apis[i].DeviceCount} device{(apis[i].DeviceCount == 1 ? "" : "s")})");

int apiIdx     = PickNumber("Select API", 0, apis.Count - 1);
var selectedApi = apis[apiIdx];

// ── 3. Pick output device ────────────────────────────────────────────────────

var outputDevices = engine.GetDevices()
    .Where(d => d.HostApiIndex == selectedApi.Index && d.MaxOutputChannels > 0)
    .ToList();

if (outputDevices.Count == 0)
{
    Console.WriteLine($"No output devices found for API '{selectedApi.Name}'.");
    return;
}

Console.WriteLine($"\nOutput devices on  {selectedApi.Name}:");
for (int i = 0; i < outputDevices.Count; i++)
    Console.WriteLine($"  [{i}]  {outputDevices[i].Name}  " +
                      $"(ch: {outputDevices[i].MaxOutputChannels},  " +
                      $"{outputDevices[i].DefaultSampleRate:0} Hz)");

int devIdx = PickNumber("Select device", 0, outputDevices.Count - 1);
var device = outputDevices[devIdx];

// ── 4. Enter file path ───────────────────────────────────────────────────────

Console.Write("\nAudio file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 5. Open decoder ───────────────────────────────────────────────────────────

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

    // Cap output to stereo; use file's native sample rate to avoid resampling.
    int outChannels = Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, 2));
    var hwFmt       = new AudioFormat(srcFmt.SampleRate, outChannels);
    var routeMap    = BuildRouteMap(srcFmt.Channels, outChannels);

    Console.WriteLine("OK");
    Console.WriteLine($"  Source:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");
    Console.WriteLine($"  Output:  {hwFmt.SampleRate} Hz / {outChannels} ch  →  {device.Name}");

    // ── 6. Open output ───────────────────────────────────────────────────────

    Console.Write("Opening output device… ");
    using var output = new PortAudioOutput();
    try
    {
        output.Open(device, hwFmt, framesPerBuffer: 512);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    output.Mixer.AddChannel(audioChannel, routeMap);

    // ── 7. EOF detection via BufferUnderrun ──────────────────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var sw = Stopwatch.StartNew();
    audioChannel.BufferUnderrun += (_, _) =>
    {
        // Ignore underruns during the first 2 s (buffer warm-up).
        if (sw.Elapsed.TotalSeconds > 2 && !cts.IsCancellationRequested)
        {
            Console.WriteLine("\n[EOF reached]");
            cts.Cancel();
        }
    };

    // ── 8. Start playback ────────────────────────────────────────────────────

    decoder.Start();
    await output.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

    // Wait for Ctrl+C, Enter, or auto-EOF.
    _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // ── 9. Stop ──────────────────────────────────────────────────────────────

    Console.Write("\nStopping… ");
    await output.StopAsync();
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

/// <summary>
/// Builds a route map that handles mono→stereo fan-out and multi-channel→stereo clipping.
/// </summary>
static ChannelRouteMap BuildRouteMap(int srcChannels, int dstChannels)
{
    var b = new ChannelRouteMap.Builder();
    if (srcChannels == 1 && dstChannels >= 2)
    {
        // Mono source → both stereo channels.
        b.Route(0, 0).Route(0, 1);
    }
    else
    {
        // Route up to min(src, dst) channels straight across.
        int common = Math.Min(srcChannels, dstChannels);
        for (int i = 0; i < common; i++) b.Route(i, i);
    }
    return b.Build();
}

