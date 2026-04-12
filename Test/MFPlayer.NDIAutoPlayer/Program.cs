// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.NDIAutoPlayer
//   Demonstrates NDISource.OpenByNameAsync with auto-reconnection,
//   PortAudio audio output, and SDL3 video output.
//
//   Usage:
//     dotnet run                          — picks first PortAudio device, prompts for NDI source name
//     dotnet run -- "OBS"                 — auto-discovers source matching "OBS"
//     dotnet run -- "MY-PC (OBS)"         — exact NDI source name
//
//   The player will:
//     1. Initialise NDI + PortAudio + SDL3
//     2. Wait for an NDI source matching the given name to appear on the network
//     3. Connect and play audio + video
//     4. Automatically reconnect if the source goes offline
//     5. Print state changes and connection status in real time
//
//   Press [Enter] or [Ctrl+C] to stop. Closing the video window also stops.
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.NDI;
using S.Media.PortAudio;
using S.Media.SDL3;

// Print any unhandled background-thread exceptions before the process dies.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"\n[FATAL] {e.ExceptionObject}");

Console.WriteLine("╔═══════════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  NDI Auto Player        ║");
Console.WriteLine("╚═══════════════════════════════════════╝\n");

// ── 0. Optional: configure logging ───────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(opts =>
    {
        opts.SingleLine      = true;
        opts.TimestampFormat  = "HH:mm:ss.fff ";
    }).SetMinimumLevel(LogLevel.Information));

MediaCoreLogging.Configure(loggerFactory);
NDIMediaLogging.Configure(loggerFactory);
PortAudioLogging.Configure(loggerFactory);
SDL3VideoLogging.Configure(loggerFactory);

// ── 1. Initialise NDI runtime ────────────────────────────────────────────────

if (!NDIRuntime.IsSupportedCpu())
{
    Console.WriteLine("This CPU does not meet the NDI requirements (SSE4.2 required).");
    return;
}

int ndiRet = NDIRuntime.Create(out var ndiRuntime);
if (ndiRet != 0 || ndiRuntime == null)
{
    Console.WriteLine($"Failed to initialise NDI runtime (code {ndiRet}).");
    Console.WriteLine("Make sure the NDI runtime is installed: https://ndi.video/tools/");
    return;
}

using (ndiRuntime)
{
    Console.WriteLine($"NDI runtime  v{NDIRuntime.Version}\n");

    // ── 2. Initialise PortAudio ──────────────────────────────────────────────

    using var engine = new PortAudioEngine();
    engine.Initialize();

    // ── 3. Pick Host API ─────────────────────────────────────────────────────

    var apis = engine.GetHostApis();
    Console.WriteLine("Available Host APIs:");
    for (int i = 0; i < apis.Count; i++)
        Console.WriteLine($"  [{i}]  {apis[i].Name}  ({apis[i].DeviceCount} device{(apis[i].DeviceCount == 1 ? "" : "s")})");

    int apiIdx      = PickNumber("Select API", 0, apis.Count - 1);
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

    int devIdx = PickNumber("Select device", 0, outputDevices.Count - 1);
    var device = outputDevices[devIdx];

    // ── 5. Get NDI source name ───────────────────────────────────────────────

    string sourceName;
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
    {
        sourceName = args[0];
    }
    else
    {
        Console.Write("\nEnter NDI source name (full or partial, e.g. \"OBS\"): ");
        sourceName = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            Console.WriteLine("No source name provided.");
            return;
        }
    }

    // ── 6. Open NDI source by name (async discovery + auto-reconnect) ────────

    int outChannels = Math.Min(device.MaxOutputChannels, 2);

    Console.WriteLine($"\nWaiting for NDI source matching '{sourceName}'…");
    Console.WriteLine("  (the source does not need to be online yet — we'll wait)\n");

    using var discoveryCts = new CancellationTokenSource();

    // Allow Ctrl+C to cancel discovery too.
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; discoveryCts.Cancel(); };

    NDISource ndiSource;
    try
    {
        ndiSource = await NDISource.OpenByNameAsync(
            sourceName,
            new NDISourceOptions
            {
                SampleRate               = 48000,
                Channels                 = outChannels,
                AudioBufferDepth         = 32,
                VideoBufferDepth         = 4,
                EnableVideo              = true,
                AutoReconnect            = true,
                ConnectionCheckIntervalMs = 2000,
                FinderSettings           = new NDIFinderSettings { ShowLocalSources = true }
            },
            discoveryCts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Discovery cancelled.");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to open NDI source: {ex.Message}");
        return;
    }

    Console.WriteLine($"  ✓ Connected to NDI source");

    if (ndiSource.AudioChannel == null)
    {
        Console.WriteLine("The selected NDI source has no audio stream.");
        ndiSource.Dispose();
        return;
    }

    using (ndiSource)
    {
        // ── 7. Subscribe to state changes ────────────────────────────────────

        ndiSource.StateChanged += (_, e) =>
        {
            var color = e.NewState switch
            {
                NDISourceState.Connected    => ConsoleColor.Green,
                NDISourceState.Reconnecting => ConsoleColor.Yellow,
                NDISourceState.Disconnected => ConsoleColor.Red,
                NDISourceState.Discovering  => ConsoleColor.Cyan,
                _                           => ConsoleColor.Gray
            };
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"  [NDI] {e.OldState} → {e.NewState}  ({e.SourceName})");
            Console.ForegroundColor = prev;
        };

        // ── 8. Wire up audio pipeline ────────────────────────────────────────

        var audioChannel = ndiSource.AudioChannel;
        var srcFmt       = audioChannel.SourceFormat;
        int outCh        = Math.Min(srcFmt.Channels, outChannels);
        var hwFmt        = new AudioFormat(srcFmt.SampleRate, outCh);
        var routeMap     = BuildRouteMap(srcFmt.Channels, outCh);

        Console.WriteLine($"  NDI audio:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");

        // PortAudioOutput.Open automatically falls back to the device's native
        // sample rate if the requested rate isn't supported (e.g. JACK at 44100 Hz
        // when NDI delivers 48000 Hz).  The AudioMixer resamples transparently.
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
        if (srcFmt.SampleRate != output.HardwareFormat.SampleRate)
            Console.WriteLine($"  Resampling: {srcFmt.SampleRate} → {output.HardwareFormat.SampleRate} Hz (AudioMixer)");

        // ── 8b. Wire up video pipeline (if available) ─────────────────────────

        var videoChannel = ndiSource.VideoChannel;
        SDL3VideoOutput? videoOutput = null;

        if (videoChannel != null)
        {
            // NDISource.Start() must be called before we can receive frames,
            // so start now and wait for the first video frame to learn the format.
            ndiSource.Start();

            Console.Write("  Waiting for first video frame… ");
            VideoFormat videoFormat = default;
            using var vfCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!vfCts.Token.IsCancellationRequested)
                {
                    videoFormat = videoChannel.SourceFormat;
                    if (videoFormat.Width > 0 && videoFormat.Height > 0)
                        break;
                    await Task.Delay(100, vfCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* timeout — use fallback */ }

            if (videoFormat.Width > 0 && videoFormat.Height > 0)
            {
                Console.WriteLine($"OK ({videoFormat})");
            }
            else
            {
                videoFormat = new VideoFormat(1920, 1080, PixelFormat.Bgra32, 30000, 1001);
                Console.WriteLine($"timed out — using {videoFormat}");
            }

            var (winW, winH) = FitWithin(videoFormat.Width, videoFormat.Height, 1920, 1080);
            videoOutput = new SDL3VideoOutput();
            try
            {
                videoOutput.Open($"NDI — {sourceName}", winW, winH, videoFormat);
                Console.WriteLine($"  SDL3 video: {winW}×{winH} window, {videoOutput.OutputFormat}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SDL3 video FAILED: {ex.Message}  (continuing audio-only)");
                videoOutput.Dispose();
                videoOutput = null;
            }
        }
        else
        {
            Console.WriteLine("  (No video channel — audio only)");
        }

        // ── 8c. Create AV mixer ───────────────────────────────────────────────

        using var avMixer = videoOutput != null
            ? new AVMixer(output.HardwareFormat, videoOutput.OutputFormat)
            : new AVMixer(output.HardwareFormat);
        avMixer.AttachAudioOutput(output);
        avMixer.AddAudioChannel(audioChannel, routeMap);

        if (videoOutput != null && videoChannel != null)
        {
            avMixer.AttachVideoOutput(videoOutput);
            avMixer.AddVideoChannel(videoChannel);
        }

        // ── 9. Start ─────────────────────────────────────────────────────────

        Console.Write("Starting… ");
        try
        {
            // ndiSource.Start() may already have been called above for video
            // format detection — calling it again is a safe no-op.
            ndiSource.Start();

            // Pre-buffer audio
            Console.Write("buffering… ");
            using var preCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try   { await audioChannel.WaitForBufferAsync(8, preCts.Token); }
            catch (OperationCanceledException) { /* timed out — proceed */ }

            await output.StartAsync();
            if (videoOutput != null)
                await videoOutput.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            ndiSource.Stop();
            videoOutput?.Dispose();
            return;
        }
        Console.WriteLine("OK");

        Console.WriteLine($"\n  ▶  Playing NDI source: '{sourceName}'");
        Console.WriteLine("     Auto-reconnect is ENABLED — source can go offline and come back.");
        Console.WriteLine(videoOutput != null
            ? "     Press [Enter] or [Ctrl+C] to stop. Closing the video window also stops.\n"
            : "     Press [Enter] or [Ctrl+C] to stop.\n");

        // ── 10. Status ticker ────────────────────────────────────────────────

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Auto-stop when the SDL3 window is closed
        if (videoOutput != null)
        {
            videoOutput.WindowClosed += () =>
            {
                Console.WriteLine("\n[Window closed]");
                if (!cts.IsCancellationRequested) cts.Cancel();
            };
        }

        // Print periodic status
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(5000, cts.Token).ConfigureAwait(false);
                var state    = ndiSource.State;
                var aPos     = audioChannel.Position;
                var bufAvail = audioChannel.BufferAvailable;
                var line     = $"  [{DateTime.Now:HH:mm:ss}]  state={state}  audio={aPos:mm\\:ss\\.fff}  buf={bufAvail}";
                if (videoChannel != null)
                    line += $"  video={videoChannel.Position:mm\\:ss\\.fff}";
                if (videoOutput != null)
                {
                    var snap = videoOutput.GetDiagnosticsSnapshot();
                    line += $"  presented={snap.PresentedFrames}  black={snap.BlackFrames}";
                }
                Console.WriteLine(line);
            }
        }, cts.Token);

        // Wait for Enter or Ctrl+C
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
        if (videoOutput != null)
            await videoOutput.StopAsync();
        await output.StopAsync();
        ndiSource.Stop();
        videoOutput?.Dispose();
        Console.WriteLine("Done.");
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static (int Width, int Height) FitWithin(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
{
    srcWidth  = srcWidth  > 0 ? srcWidth  : 1280;
    srcHeight = srcHeight > 0 ? srcHeight : 720;

    double scale = Math.Min((double)maxWidth / srcWidth, (double)maxHeight / srcHeight);
    scale = Math.Min(1.0, scale);

    int width  = Math.Max(320, (int)Math.Round(srcWidth  * scale));
    int height = Math.Max(180, (int)Math.Round(srcHeight * scale));
    return (width, height);
}

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
/// Builds a route map: mono → both stereo channels; multi-channel → straight-across up to min(src, dst).
/// </summary>
static ChannelRouteMap BuildRouteMap(int srcChannels, int dstChannels)
{
    var b = new ChannelRouteMap.Builder();
    if (srcChannels == 1 && dstChannels >= 2)
    {
        b.Route(0, 0).Route(0, 1);
    }
    else
    {
        int common = Math.Min(srcChannels, dstChannels);
        for (int i = 0; i < common; i++) b.Route(i, i);
    }
    return b.Build();
}

