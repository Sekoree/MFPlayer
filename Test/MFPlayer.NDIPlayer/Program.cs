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
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
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
        // Poll up to 5 s in 1 s increments; stop early if sources appear.
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

    int outChannels = Math.Min(device.MaxOutputChannels, 2); // cap to stereo

    NDISource ndiSource;
    try
    {
        ndiSource = NDISource.Open(selectedSource, new NDISourceOptions
        {
            SampleRate       = 48000,
            Channels         = outChannels,
            QueueBufferDepth = NDILatencyPreset.FromQueueDepth(queueDepth),
            LowLatency       = lowLatencyPolling,
            EnableVideo      = false   // audio-only path
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
        var routeMap     = BuildRouteMap(srcFmt.Channels, hwFmt.Channels);

        Console.WriteLine($"  NDI audio:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");

        // ── 8. Open PortAudio output ─────────────────────────────────────────
        // PortAudioOutput.Open automatically falls back to the device's default
        // sample rate if the requested rate isn't supported.  The AudioMixer
        // resamples any source-rate ↔ output-rate mismatch transparently.

        Console.Write("Opening output device… ");
        using var output = new PortAudioOutput();
        try
        {
            output.Open(device, hwFmt, framesPerBuffer: 1024);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            return;
        }
        Console.WriteLine("OK");

        Console.WriteLine($"  Output:     {output.HardwareFormat.SampleRate} Hz / {output.HardwareFormat.Channels} ch  →  {device.Name}");

        using var avMixer = new AVMixer(output.HardwareFormat);
        avMixer.AttachAudioOutput(output);

        avMixer.AddAudioChannel(audioChannel, routeMap);

        // ── 9. Start ─────────────────────────────────────────────────────────

        Console.Write("Starting… ");
        try
        {
            ndiSource.Start();

            // Pre-buffer: let the capture thread fill a few ring chunks before opening
            // the hardware stream.  Without this the RT callback fires on an empty ring and
            // underruns repeatedly during the first ~300 ms of playback.
            // 8 chunks × 1024 frames @ 48 kHz ≈ 170 ms of headroom; also absorbs OS
            // scheduler jitter between the capture thread's Sleep wakeups.
            Console.Write("buffering… ");
            using var preCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try   { await ndiSource.WaitForAudioBufferAsync(8, preCts.Token); }
            catch (OperationCanceledException) { /* timed out — proceed with whatever arrived */ }

            await output.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            ndiSource.Stop();
            return;
        }
        Console.WriteLine("OK");

        Console.WriteLine($"\nPlaying NDI:  {selectedSource.Name}");
        Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Console.ReadLine() returns null when stdin is at EOF (e.g. Rider's piped
        // console after the user finishes the selection prompts).  Guard against that
        // so the only way to stop is an actual Enter key-press or Ctrl+C.
        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line != null)          // user pressed Enter
                {
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(200);         // stdin at EOF — keep waiting for Ctrl+C
            }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
        catch (OperationCanceledException) { }

        // ── 10. Stop ─────────────────────────────────────────────────────────

        Console.Write("\nStopping… ");
        await output.StopAsync();
        ndiSource.Stop();
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

/// <summary>
/// Builds a route map: mono → both stereo channels; multi-channel → straight-across up to min(src, dst).
/// </summary>
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

