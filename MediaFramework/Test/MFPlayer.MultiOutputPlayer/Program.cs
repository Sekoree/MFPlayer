// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.MultiOutputPlayer
//   1. Pick a PortAudio host API
//   2. Pick PRIMARY output device  (leader — drives the clock)
//   3. Pick SECONDARY output device (fan-out sink — receives a copy of the mix)
//   4. Enter an audio file path
//   5. Play to both devices simultaneously via MediaPlayer routing
//      Press Enter or Ctrl+C to stop; auto-stops at EOF
// ═══════════════════════════════════════════════════════════════════════════════

using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.PortAudio;

Console.WriteLine("╔═══════════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Multi-Output Player    ║");
Console.WriteLine("╚═══════════════════════════════════════╝\n");

ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

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

// ── 7. Open outputs ──────────────────────────────────────────────────────────

var hwFmt = new AudioFormat(
    (int)primaryDevice.DefaultSampleRate,
    Math.Min(primaryDevice.MaxOutputChannels, 2));

Console.Write($"Opening primary device '{primaryDevice.Name}'… ");
using var primaryOutput = PortAudioEndpoint.Create(primaryDevice, hwFmt, framesPerBuffer: 512);
Console.WriteLine("OK");

var negotiatedFmt = primaryOutput.HardwareFormat;

Console.Write($"Opening secondary device '{secondaryDevice.Name}'… ");
using var secondarySink = PortAudioEndpoint.Create(
    secondaryDevice,
    negotiatedFmt,
    mode:            PortAudioDrivingMode.BlockingWrite,
    framesPerBuffer: 512,
    name:            $"Sink({secondaryDevice.Name})");
Console.WriteLine("OK");

// ── 8. Build player pipeline ─────────────────────────────────────────────────

using var player = MediaPlayer.Create()
    .WithAudioOutput(primaryOutput)
    .WithAudioOutput(secondarySink)
    .Build();

// ── 9. Completion detection ──────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 10. Start playback ──────────────────────────────────────────────────────

await player.OpenAndPlayAsync(filePath);

Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
Console.WriteLine($"  → {primaryDevice.Name}  (primary)");
Console.WriteLine($"  → {secondaryDevice.Name}  (secondary)");
Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

_ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });

try
{
    var reason = await player.WaitForCompletionAsync(ct: cts.Token);
    Console.WriteLine($"\n[Playback finished: {reason}]");
}
catch (OperationCanceledException) { }

// ── 11. Stop ─────────────────────────────────────────────────────────────────

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

static void PrintDeviceList(List<AudioDeviceInfo> devices, string apiName)
{
    Console.WriteLine($"\nOutput devices on  {apiName}:");
    for (int i = 0; i < devices.Count; i++)
        Console.WriteLine($"  [{i}]  {devices[i].Name}  " +
                          $"(ch: {devices[i].MaxOutputChannels},  " +
                          $"{devices[i].DefaultSampleRate:0} Hz)");
}
