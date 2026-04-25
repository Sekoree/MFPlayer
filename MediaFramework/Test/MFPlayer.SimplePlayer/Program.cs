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
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.PortAudio;

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Simple Player  ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

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

// ── 5. Build player pipeline ─────────────────────────────────────────────────

var hwFmt = new AudioFormat(
    (int)device.DefaultSampleRate,
    Math.Min(device.MaxOutputChannels, 2));

Console.Write("Opening output device… ");
// Request a short callback period and low device latency so volume / seek
// changes reach the speakers within one or two callbacks. PortAudio treats
// `suggestedLatency` as a hint — the host API may round up on PulseAudio etc.
using var output = PortAudioEndpoint.Create(
    device, hwFmt,
    framesPerBuffer:  256,
    suggestedLatency: 0.020);
Console.WriteLine("OK");
Console.WriteLine($"  Output:  {output.HardwareFormat.SampleRate} Hz / {output.HardwareFormat.Channels} ch  →  {device.Name}");

using var player = MediaPlayer.Create()
    .WithAudioOutput(output)
    .WithDecoderOptions(new FFmpegDecoderOptions { EnableVideo = false })
    .Build();

// ── 6. Completion detection ──────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

player.PlaybackCompleted += (_, e) =>
{
    if (e.Reason == PlaybackCompletedReason.SourceEnded)
    {
        Console.WriteLine("\n[Playback completed]");
        // Small drain grace so the tail of buffered audio reaches the hardware.
        Task.Delay(300).ContinueWith(_ => cts.Cancel());
    }
};

// ── 7. Start playback ────────────────────────────────────────────────────────

await player.OpenAndPlayAsync(filePath);

Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
Console.WriteLine("Controls: [Space]=pause/play  [Left/Right]=-+5s seek  [Up/Down]=-+0.05 vol  [Enter/Q/Esc]=stop\n");

var seekState = new SeekUiState();
var statsSw = Stopwatch.StartNew();

// ── 8. Main control loop ─────────────────────────────────────────────────────

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
                if (player.State == PlaybackState.Paused)
                {
                    await player.PlayAsync();
                    Console.WriteLine("[play]");
                }
                else if (player.State == PlaybackState.Playing)
                {
                    await player.PauseAsync();
                    Console.WriteLine("[pause]");
                }
                break;

            case ConsoleKey.LeftArrow:
            {
                if (TrySeekBy(TimeSpan.FromSeconds(-5), player,
                    ref seekState.SeekAnchor, ref seekState.LastSeekTicks,
                    ref seekState.LastSeekCommandTicks, ref seekState.LastSeekTarget, out var target))
                    Console.WriteLine($"[seek] {FormatTime(target)}");
                break;
            }

            case ConsoleKey.RightArrow:
            {
                if (TrySeekBy(TimeSpan.FromSeconds(5), player,
                    ref seekState.SeekAnchor, ref seekState.LastSeekTicks,
                    ref seekState.LastSeekCommandTicks, ref seekState.LastSeekTarget, out var target))
                    Console.WriteLine($"[seek] {FormatTime(target)}");
                break;
            }

            case ConsoleKey.UpArrow:
                player.Volume = Math.Clamp(player.Volume + 0.05f, 0.0f, 2.0f);
                Console.WriteLine($"[volume] {player.Volume:0.00}");
                break;

            case ConsoleKey.DownArrow:
                player.Volume = Math.Clamp(player.Volume - 0.05f, 0.0f, 2.0f);
                Console.WriteLine($"[volume] {player.Volume:0.00}");
                break;
        }
    }

    if (statsSw.ElapsedMilliseconds >= 25)
    {
        statsSw.Restart();
        var pos = player.Position;
        var buf = player.AudioChannel?.BufferAvailable ?? 0;

        Console.Write("\r" +
            $"[stats] state={player.State,7}  " +
            $"pos={FormatTime(pos)}  " +
            $"buffer={buf,6}f  vol={player.Volume:0.00}");
    }

    try { await Task.Delay(20, cts.Token); }
    catch (OperationCanceledException) { }
}

// ── 9. Stop ──────────────────────────────────────────────────────────────────

Console.Write("\nStopping… ");
await player.StopAsync();
Console.WriteLine("Done.");

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

static string FormatTime(TimeSpan ts)
{
    if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
    return ts.ToString(@"hh\:mm\:ss\.fff");
}

static bool TrySeekBy(
    TimeSpan delta,
    MediaPlayer player,
    ref TimeSpan seekAnchor,
    ref long lastSeekTicks,
    ref long lastSeekCommandTicks,
    ref TimeSpan lastSeekTarget,
    out TimeSpan target)
{
    long nowTicks = Stopwatch.GetTimestamp();

    double sinceLastCommand = (nowTicks - lastSeekCommandTicks) / (double)Stopwatch.Frequency;
    TimeSpan basePos = sinceLastCommand <= 0.075 ? seekAnchor : player.Position;

    target = basePos + delta;
    if (target < TimeSpan.Zero)
        target = TimeSpan.Zero;

    if (target == TimeSpan.Zero && lastSeekTarget == TimeSpan.Zero && basePos <= TimeSpan.FromMilliseconds(20))
        return false;

    if (lastSeekTarget != TimeSpan.MinValue && sinceLastCommand <= 0.075)
    {
        long deltaTicks = target.Ticks >= lastSeekTarget.Ticks
            ? target.Ticks - lastSeekTarget.Ticks
            : lastSeekTarget.Ticks - target.Ticks;

        if (deltaTicks < TimeSpan.FromMilliseconds(20).Ticks)
            return false;
    }

    player.Seek(target);
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
