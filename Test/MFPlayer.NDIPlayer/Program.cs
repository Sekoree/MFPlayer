// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.NDIPlayer
//   1. Pick a PortAudio host API
//   2. Pick an output device
//   3. Discover NDI sources on the network
//   4. Pick a source
//   5. Play — press Enter or Ctrl+C to stop
// ═══════════════════════════════════════════════════════════════════════════════

using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.NDI;
using S.Media.PortAudio;

// Print any unhandled background-thread exceptions before the process dies.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"\n[FATAL] {e.ExceptionObject}");

Console.WriteLine("╔═══════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  NDI Player         ║");
Console.WriteLine("╚═══════════════════════════════════╝\n");

// ── 1. Initialize NDI runtime ────────────────────────────────────────────────

if (!NDIRuntime.IsSupportedCpu())
{
    Console.WriteLine("This CPU does not meet the NDI requirements (SSE4.2 required).");
    return;
}

int ndiRet = NDIRuntime.Create(out var ndiRuntime);
if (ndiRet != 0 || ndiRuntime == null)
{
    Console.WriteLine($"Failed to initialize NDI runtime (code {ndiRet}).");
    Console.WriteLine("Make sure the NDI runtime is installed: https://ndi.video/tools/");
    return;
}

using (ndiRuntime)
{
    Console.WriteLine($"NDI runtime  v{NDIRuntime.Version}\n");

    // ── 2. Initialize PortAudio ──────────────────────────────────────────────

    using var engine = new PortAudioEngine();
    engine.Initialize();

    // ── 3. Pick Host API ─────────────────────────────────────────────────────

    var apis = engine.GetHostApis();
    Console.WriteLine("Available Host APIs:");
    for (int i = 0; i < apis.Count; i++)
        Console.WriteLine($"  [{i}]  {apis[i].Name}  ({apis[i].DeviceCount} device{(apis[i].DeviceCount == 1 ? "" : "s")})");

    int apiIdx      = PickNumber("Select host API", 0, apis.Count - 1);
    var selectedApi = apis[apiIdx];

    // ── 4. Pick output device ────────────────────────────────────────────────

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

    int devIdx = PickNumber("Select output device", 0, outputDevices.Count - 1);
    var device = outputDevices[devIdx];

    // ── 5. Discover NDI sources ───────────────────────────────────────────────

    Console.Write("\nSearching for NDI sources");

    int finderRet = NDIFinder.Create(out var finder, new NDIFinderSettings
    {
        ShowLocalSources = true
    });

    if (finderRet != 0 || finder == null)
    {
        Console.WriteLine($"\nFailed to create NDI finder (code {finderRet}).");
        return;
    }

    NDIDiscoveredSource[] sources;
    using (finder)
    {
        sources = [];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            finder.WaitForSources(1000);
            sources = finder.GetCurrentSources();
            Console.Write(".");
            if (sources.Length > 0) break;
        }
    }

    Console.WriteLine();

    if (sources.Length == 0)
    {
        Console.WriteLine("No NDI sources found on the network.");
        return;
    }

    // ── 6. Pick NDI source ────────────────────────────────────────────────────

    Console.WriteLine($"\nAvailable NDI sources:");
    for (int i = 0; i < sources.Length; i++)
    {
        string url = sources[i].UrlAddress is { Length: > 0 } u ? $"  [{u}]" : string.Empty;
        Console.WriteLine($"  [{i}]  {sources[i].Name}{url}");
    }

    int srcIdx          = PickNumber("Select source", 0, sources.Length - 1);
    var selectedSource  = sources[srcIdx];

    var preset = PickNdiPreset();
    var latencyPreset = NDILatencyPreset.FromEndpointPreset(preset);
    int queueDepth = PickNumber("Queue buffer depth", 1, 64, latencyPreset.ResolveQueueDepth());
    bool defaultLowLatencyPolling = preset == NDIEndpointPreset.LowLatency;
    bool lowLatencyPolling = PickYesNo(
        "Use LowLatency polling (faster polling, higher CPU)",
        defaultLowLatencyPolling);
    Console.WriteLine($"NDI receive profile: preset={preset}, queueDepth={queueDepth}, lowLatencyPolling={(lowLatencyPolling ? "on" : "off")}");

    // ── 7. Open NDI source ────────────────────────────────────────────────────

    Console.Write($"\nConnecting to '{selectedSource.Name}'… ");

    int outChannels = Math.Min(device.MaxOutputChannels, 2);

    NDISource ndiSource;
    try
    {
        ndiSource = NDISource.Open(selectedSource, new NDISourceOptions
        {
            SampleRate       = 48000,
            Channels         = outChannels,
            QueueBufferDepth = NDILatencyPreset.FromQueueDepth(queueDepth),
            LowLatency       = lowLatencyPolling,
            EnableVideo      = false
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }

    Console.WriteLine("OK");

    if (ndiSource.AudioChannel == null)
    {
        Console.WriteLine("The selected NDI source has no audio stream.");
        ndiSource.Dispose();
        return;
    }

    using (ndiSource)
    {
        var audioChannel = ndiSource.AudioChannel;
        var srcFmt       = audioChannel.SourceFormat;
        var hwFmt        = new AudioFormat(srcFmt.SampleRate, Math.Min(srcFmt.Channels, outChannels));

        Console.WriteLine($"  NDI audio:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");

        // ── 8. Open PortAudio output ─────────────────────────────────────────

        Console.Write("Opening output device… ");
        PortAudioEndpoint output;
        try
        {
            output = PortAudioEndpoint.Create(device, hwFmt, framesPerBuffer: 1024);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            return;
        }
        using var _outputScope = output;
        Console.WriteLine("OK");
        Console.WriteLine($"  Output:     {output.HardwareFormat.SampleRate} Hz / {output.HardwareFormat.Channels} ch  →  {device.Name}");

        // ── 9. Build player pipeline ─────────────────────────────────────────

        using var player = MediaPlayer.Create()
            .WithAudioOutput(output)
            .WithAudioInput(audioChannel)
            .Build();

        // ── 10. Start ────────────────────────────────────────────────────────

        Console.Write("Starting… ");
        try
        {
            ndiSource.Start();

            // Pre-buffer: let the capture thread fill a few ring chunks before
            // opening the hardware stream.
            Console.Write("buffering… ");
            using var preCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try   { await ndiSource.WaitForAudioBufferAsync(8, preCts.Token); }
            catch (OperationCanceledException) { /* timed out — proceed */ }

            await player.PlayAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            ndiSource.StopClock();
            return;
        }
        Console.WriteLine("OK");

        Console.WriteLine($"\nPlaying NDI:  {selectedSource.Name}");
        Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line != null)
                {
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(200);
            }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
        catch (OperationCanceledException) { }

        // ── 11. Stop ─────────────────────────────────────────────────────────

        Console.Write("\nStopping… ");
        await player.StopAsync();
        ndiSource.StopClock();
        Console.WriteLine("Done.");
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static int PickNumber(string label, int min, int max, int? defaultValue = null)
{
    int fallback = defaultValue ?? min;
    while (true)
    {
        Console.Write($"{label} [{min}-{max}] (default {fallback}): ");
        string? line = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(line)) return fallback;
        if (int.TryParse(line, out int v) && v >= min && v <= max) return v;
        Console.WriteLine($"  Please enter a number between {min} and {max}.");
    }
}

static NDIEndpointPreset PickNdiPreset()
{
    Console.Write("NDI receive preset [Safe/Balanced/LowLatency] (default Balanced): ");
    string raw = (Console.ReadLine() ?? string.Empty).Trim();
    if (raw.Equals("safe", StringComparison.OrdinalIgnoreCase)) return NDIEndpointPreset.Safe;
    if (raw.Equals("low", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("low-latency", StringComparison.OrdinalIgnoreCase)) return NDIEndpointPreset.LowLatency;
    return NDIEndpointPreset.Balanced;
}

static bool PickYesNo(string label, bool defaultValue)
{
    while (true)
    {
        Console.Write($"{label} [{(defaultValue ? "Y/n" : "y/N")}]: ");
        string? raw = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(raw)) return defaultValue;
        if (raw.Equals("y", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("n", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        Console.WriteLine("  Please enter y/yes or n/no.");
    }
}
