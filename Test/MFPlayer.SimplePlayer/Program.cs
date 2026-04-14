// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.SimplePlayer
//   1. Pick a PortAudio host API
//   2. Pick an output device
//   3. Enter an audio file path
//   4. Play with controls:
//        Space = pause/play
//        Left/Right = seek -/+5 s
//        Up/Down = volume -/+0.05
//        Enter / Q / Esc / Ctrl+C = stop
//      auto-stops at EOF
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
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

    // Cap output to stereo.
    int outChannels = Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, 2));
    var hwFmt       = new AudioFormat(srcFmt.SampleRate, outChannels);
    var routeMap    = BuildRouteMap(srcFmt.Channels, outChannels);

    Console.WriteLine("OK");
    Console.WriteLine($"  Source:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");

    // ── 6. Open output ───────────────────────────────────────────────────────
    // PortAudioOutput.Open automatically falls back to the device's default
    // sample rate if the requested rate isn't supported.  The AudioMixer
    // resamples any source-rate ↔ output-rate mismatch transparently.

    Console.Write("Opening output device… ");
    using var output = new PortAudioOutput();
    try
    {
        output.Open(device, hwFmt, framesPerBuffer: 0);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    Console.WriteLine($"  Output:  {output.HardwareFormat.SampleRate} Hz / {output.HardwareFormat.Channels} ch  →  {device.Name}");

    using var avMixer = new AVMixer(output.HardwareFormat);
    avMixer.AttachAudioOutput(output);

    avMixer.AddAudioChannel(audioChannel, routeMap);
    audioChannel.Volume = 1.0f;

    if (srcFmt.SampleRate != output.HardwareFormat.SampleRate)
        Console.WriteLine($"  Resampling: {srcFmt.SampleRate} → {output.HardwareFormat.SampleRate} Hz (AudioMixer)");

    // ── 7. Completion detection (source-ended + drain) ───────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var seekState = new SeekUiState();
    int sourceEnded = 0;
    long sourceEndedTicks = 0;
    bool completionAnnounced = false;
    const double DrainGraceSeconds = 0.30;

    void MarkSourceEnded(string tag)
    {
        Volatile.Write(ref sourceEnded, 1);
        Interlocked.Exchange(ref sourceEndedTicks, Stopwatch.GetTimestamp());
        Console.WriteLine($"\n[{tag}: waiting for drain]");
    }

    decoder.EndOfMedia += (_, _) => MarkSourceEnded("demux EOF");
    audioChannel.EndOfStream += (_, _) => MarkSourceEnded("decode EOF");

    var sw = Stopwatch.StartNew();
    audioChannel.BufferUnderrun += (_, _) =>
    {
        // Ignore underruns during the first 2 s (buffer warm-up).
        if (sw.Elapsed.TotalSeconds <= 2) return;
        if (cts.IsCancellationRequested) return;

        // Seeking temporarily drains/flushes buffers; do not interpret that as EOF.
        long ticksSinceSeek = Stopwatch.GetTimestamp() - Interlocked.Read(ref seekState.LastSeekTicks);
        double secondsSinceSeek = ticksSinceSeek / (double)Stopwatch.Frequency;
        if (secondsSinceSeek < 1.2) return;

        // Underruns can happen before true EOF under decode pressure; do not auto-stop here.
    };

    // ── 8. Start playback ────────────────────────────────────────────────────

    decoder.Start();
    await output.StartAsync();
    var clockBase = output.Clock.Position;

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Controls: [Space]=pause/play  [Left/Right]=-+5s seek  [Up/Down]=-+0.05 vol  [Enter/Q/Esc]=stop\n");

    bool paused = false;
    float volume = 1.0f;
    var statsSw = Stopwatch.StartNew();

    // Main control loop: non-blocking key handling + periodic stats.
    while (!cts.IsCancellationRequested)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    cts.Cancel();
                    break;

                case ConsoleKey.Spacebar:
                    if (paused)
                    {
                        await output.StartAsync();
                        paused = false;
                        Console.WriteLine("[play]");
                    }
                    else
                    {
                        await output.StopAsync();
                        paused = true;
                        Console.WriteLine("[pause]");
                    }
                    break;

                case ConsoleKey.LeftArrow:
                {
                    if (TrySeekBy(TimeSpan.FromSeconds(-5), audioChannel, decoder,
                        ref seekState.SeekAnchor, ref seekState.LastSeekTicks, ref seekState.LastSeekCommandTicks, ref seekState.LastSeekTarget, out var target))
                        Console.WriteLine($"[seek] {FormatTime(target)}");
                    break;
                }

                case ConsoleKey.RightArrow:
                {
                    if (TrySeekBy(TimeSpan.FromSeconds(5), audioChannel, decoder,
                        ref seekState.SeekAnchor, ref seekState.LastSeekTicks, ref seekState.LastSeekCommandTicks, ref seekState.LastSeekTarget, out var target))
                        Console.WriteLine($"[seek] {FormatTime(target)}");
                    break;
                }

                case ConsoleKey.UpArrow:
                    volume = Math.Clamp(volume + 0.05f, 0.0f, 2.0f);
                    audioChannel.Volume = volume;
                    Console.WriteLine($"[volume] {volume:0.00}");
                    break;

                case ConsoleKey.DownArrow:
                    volume = Math.Clamp(volume - 0.05f, 0.0f, 2.0f);
                    audioChannel.Volume = volume;
                    Console.WriteLine($"[volume] {volume:0.00}");
                    break;
            }
        }

        if (statsSw.ElapsedMilliseconds >= 25)
        {
            statsSw.Restart();
            var clockPos = output.Clock.Position - clockBase;
            if (clockPos < TimeSpan.Zero) clockPos = TimeSpan.Zero;
            var chPos = audioChannel.Position;

            Console.Write("\r" +
                $"[stats] state={(paused ? "paused" : "playing"),7}  " +
                $"clock={FormatTime(clockPos)}  src={FormatTime(chPos)}  " +
                $"buffer={audioChannel.BufferAvailable,6}f  vol={audioChannel.Volume:0.00}");
        }

        if (!paused && Volatile.Read(ref sourceEnded) == 1 && audioChannel.BufferAvailable == 0)
        {
            long endedAt = Interlocked.Read(ref sourceEndedTicks);
            double sinceEnded = (Stopwatch.GetTimestamp() - endedAt) / (double)Stopwatch.Frequency;
            if (endedAt != 0 && sinceEnded >= DrainGraceSeconds)
            {
                if (!completionAnnounced)
                {
                    completionAnnounced = true;
                    Console.WriteLine("\n[Playback drained]");
                }
                cts.Cancel();
            }
        }

        try { await Task.Delay(20, cts.Token); }
        catch (OperationCanceledException) { }
    }

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

// Builds a route map that handles mono->stereo fan-out and multi-channel->stereo clipping.
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


static string FormatTime(TimeSpan ts)
{
    if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
    return ts.ToString(@"hh\:mm\:ss\.fff");
}

static bool TrySeekBy(
    TimeSpan delta,
    IAudioChannel audioChannel,
    FFmpegDecoder decoder,
    ref TimeSpan seekAnchor,
    ref long lastSeekTicks,
    ref long lastSeekCommandTicks,
    ref TimeSpan lastSeekTarget,
    out TimeSpan target)
{
    long nowTicks = Stopwatch.GetTimestamp();

    // During rapid key bursts, anchor on the most recently requested seek target
    // so each step is deterministic and not based on lagging decode position.
    double sinceLastCommand = (nowTicks - lastSeekCommandTicks) / (double)Stopwatch.Frequency;
    TimeSpan basePos = sinceLastCommand <= 0.075 ? seekAnchor : audioChannel.Position;

    target = basePos + delta;
    if (target < TimeSpan.Zero)
        target = TimeSpan.Zero;

    // When already at start, repeated back-seeks are semantic no-ops.
    // Still allow a real jump-to-zero from any non-zero base position.
    if (target == TimeSpan.Zero && lastSeekTarget == TimeSpan.Zero && basePos <= TimeSpan.FromMilliseconds(20))
        return false;

    // Ignore repeated identical targets from key auto-repeat noise during
    // rapid command bursts; allow same-target seeks again after playback advances.
    // Guard the sentinel to avoid TimeSpan overflow on the first seek.
    if (lastSeekTarget != TimeSpan.MinValue && sinceLastCommand <= 0.075)
    {
        long deltaTicks = target.Ticks >= lastSeekTarget.Ticks
            ? target.Ticks - lastSeekTarget.Ticks
            : lastSeekTarget.Ticks - target.Ticks;

        if (deltaTicks < TimeSpan.FromMilliseconds(20).Ticks)
            return false;
    }

    decoder.Seek(target);
    seekAnchor = target;
    lastSeekTarget = target;
    lastSeekCommandTicks = nowTicks;
    Interlocked.Exchange(ref lastSeekTicks, nowTicks);
    return true;
}

sealed class SeekUiState
{
    public long LastSeekTicks;
    public TimeSpan SeekAnchor = TimeSpan.Zero;
    public long LastSeekCommandTicks;
    public TimeSpan LastSeekTarget = TimeSpan.MinValue;
}

