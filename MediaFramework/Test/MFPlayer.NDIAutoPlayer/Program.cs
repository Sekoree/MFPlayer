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
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.Playback;
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

    var preset  = PickNdiPreset();
    var profile = NDIPlaybackProfile.For(preset);
    bool useNdiClock = PickYesNo("Use NDI source clock as master? (recommended — avoids domain mismatch)", defaultYes: true);
    // Live monitoring behaves best when the video timeline is authoritative.
    // Using VideoPreferred avoids accidental audio-first clock ownership and
    // keeps NDIClock in the same domain as NDIVideoChannel frame PTS.
    var clockPolicy = NDIClockPolicy.VideoPreferred;
    bool livePreviewMode = PickYesNo("Enable live preview mode (bypass PTS scheduling for lowest latency)?", defaultYes: false);
    Console.WriteLine($"NDI profile: preset={preset}, audioCapture={profile.AudioFramesPerCapture}smp, " +
                      $"liveMode={profile.BypassVideoPtsScheduling}, adaptiveVSync={profile.AdaptiveVSync}, " +
                      $"masterClock={(useNdiClock ? "NDI" : "PortAudio")}, " +
                      $"ndiClockPolicy={clockPolicy}, livePreview={livePreviewMode}");

    // ── 6. Open NDI source by name (async discovery + auto-reconnect) ────────

    int outChannels = Math.Min(device.MaxOutputChannels, 2);

    Console.WriteLine($"\nWaiting for NDI source matching '{sourceName}'...");
    Console.WriteLine("  (Source can be offline now; discovery will keep waiting.)\n");

    using var discoveryCts = new CancellationTokenSource();

    // Allow Ctrl+C to cancel discovery too.
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; discoveryCts.Cancel(); };

    var sourceOptions = BuildSourceOptions(preset, outChannels, clockPolicy);

    NDIAVChannel avSource;
    try
    {
        avSource = await NDIAVChannel.OpenByNameAsync(
            sourceName,
            sourceOptions,
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

    Console.WriteLine("  Connected to NDI source");

    using (avSource)
    {
        var videoChannel = avSource.VideoChannel;
        SDL3VideoEndpoint? videoOutput = null;

        // ── 7. Subscribe to state changes ────────────────────────────────────

        avSource.StateChanged += (_, e) =>
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
            Console.WriteLine($"  [NDI] state {e.OldState} -> {e.NewState}  ({e.SourceName})");
            Console.ForegroundColor = prev;
        };
        avSource.VideoFormatChanged += (_, e) =>
        {
            Console.WriteLine($"  [NDI] video format changed: {e.PreviousFormat} -> {e.NewFormat}");
            videoOutput?.SetInputFormatHint(e.NewFormat);
        };

        // ── 8. Wire up audio pipeline ────────────────────────────────────────

        var audioChannel = avSource.AudioChannel
            ?? throw new InvalidOperationException("NDIAutoPlayer requires an NDI source with audio.");
        var srcFmt       = audioChannel.SourceFormat;
        int outCh        = Math.Min(srcFmt.Channels, outChannels);
        var hwFmt        = new AudioFormat(srcFmt.SampleRate, outCh);

        Console.WriteLine($"  NDI audio:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");

        // PortAudioEndpoint.Create automatically falls back to the device's native
        // sample rate if the requested rate isn't supported (e.g. JACK at 44100 Hz
        // when NDI delivers 48000 Hz).  The AudioMixer resamples transparently.
        Console.Write("Opening output device… ");
        PortAudioEndpoint output;
        try
        {
            output = PortAudioEndpoint.Create(device, hwFmt, suggestedLatency: profile.AudioSuggestedLatency);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            return;
        }
        using var _outputScope = output;
        Console.WriteLine("OK");

        Console.WriteLine($"  Output:     {output.HardwareFormat.SampleRate} Hz / {output.HardwareFormat.Channels} ch  →  {device.Name}");
        if (srcFmt.SampleRate != output.HardwareFormat.SampleRate)
            Console.WriteLine($"  Resampling: {srcFmt.SampleRate} → {output.HardwareFormat.SampleRate} Hz (AudioMixer)");

        // ── 8b. Wire up video pipeline (if available) ─────────────────────────

        if (videoChannel != null)
        {
            // Start only the video capture thread first. The audio capture thread will be
            // started in step 9, AFTER the first real NDI video frame has been confirmed.
            // This ensures the audio ring is never pre-filled with framesync-generated
            // silence from before the NDI source began streaming, which would otherwise
            // add T_conn worth of silent pre-buffer to the effective startup latency.
            avSource.StartVideoCapture();

            // Wait for the first video frame to arrive in the ring — this is far more
            // latency-efficient than polling SourceFormat every 100 ms, because the
            // format is set by the capture thread immediately before the frame is
            // enqueued. WaitForVideoBufferAsync polls quickly (2-10 ms depending on
            // LowLatency mode), so format detection happens near the first real frame.
            Console.Write("  Waiting for first video frame… ");
            VideoFormat videoFormat = default;
            using var vfCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await avSource.WaitForVideoBufferAsync(1, vfCts.Token).ConfigureAwait(false);
                videoFormat = videoChannel.SourceFormat;
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

            var (winW, winH) = FitWithin(videoFormat.Width, videoFormat.Height, 2560, 1440);
            videoOutput = new SDL3VideoEndpoint();
            try
            {
                if (profile.AdaptiveVSync)
                    videoOutput.VsyncMode = VsyncMode.Adaptive;

                videoOutput.Open($"NDI — {sourceName}", winW, winH, videoFormat);
                // Pick the presentation clock that shares a timestamp domain with the
                // video frames. NDIClock is fed by NDI frame timestamps, so it matches
                // VideoFrame.Pts exactly. PortAudioClock starts at 0 locally; if the
                // NDI sender stamps frames with Unix-style timestamps the drift tracker
                // would latch onto a multi-day offset and park on the first frame.
                videoOutput.OverridePresentationClock(useNdiClock ? avSource.Clock : output.Clock);

                if (profile.ResetClockOrigin)
                    videoOutput.ResetClockOrigin();

                Console.WriteLine($"  SDL3 video: {winW}×{winH} window, {videoOutput.OutputFormat}" +
                    (profile.AdaptiveVSync ? " [VSync=Adaptive]" : ""));
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

        // ── 8c. Build MediaPlayer graph (builder API) ──────────────────────────

        var routerOptions = new AVRouterOptions
        {
            // Faster re-lock after live-source timestamp discontinuities
            // (pattern/profile/FPS switches) while staying in scheduled mode.
            VideoPtsDiscontinuityResetThreshold = TimeSpan.FromMilliseconds(250),
            // NDI clock + NDI video PTS already share a source domain. Disabling
            // integrator-based drift correction in this mode prevents slow
            // origin-walk that can accumulate pending/reuse over time.
            VideoPullDriftCorrectionGain = useNdiClock ? 0.0 : 0.05,
            VideoPushDriftCorrectionGain = useNdiClock ? 0.0 : 0.08,
        };

        var playerBuilder = MediaPlayer.Create()
            .WithRouterOptions(routerOptions)
            .WithAudioOutput(output)
            .WithAudioInput(audioChannel);

        // Drift correction manipulates route time offsets. Keep it for cross-domain
        // masters (e.g. PortAudio), but skip it when NDI clock is master because
        // video PTS and clock already share the same source domain.
        if (!livePreviewMode && !useNdiClock)
        {
            playerBuilder.WithAutoAvDriftCorrection(new AvDriftCorrectionOptions
            {
                InitialDelay = TimeSpan.FromSeconds(30),
                Interval = TimeSpan.FromSeconds(30),
                MinDriftMs = 20,
                IgnoreOutlierDriftMs = 250,
                CorrectionGain = 0.50,
                MaxStepMs = 40,
                MaxAbsOffsetMs = 250
            });
        }

        // Register the NDI source clock at External priority so it beats the
        // PortAudio output's auto-registered Hardware clock. NDIClock.Position
        // is stamped from incoming NDI frame timestamps — the same domain as
        // VideoFrame.Pts, so the router's PTS↔clock drift tracker seeds
        // correctly from the first frame instead of seeing a multi-day skew.
        if (useNdiClock)
            playerBuilder.WithClock(avSource.Clock, ClockPriority.External);

        if (videoOutput != null && videoChannel != null)
        {
            var videoRouteOptions = new VideoRouteOptions
            {
                LiveMode = livePreviewMode || profile.BypassVideoPtsScheduling,
                // Live NDI monitoring should prefer newest frames over queueing
                // stale ones; this keeps pattern/scene switches responsive.
                OverflowPolicy = VideoOverflowPolicy.DropOldest,
                Capacity = Math.Max(2, profile.VideoPreBufferFrames + 1)
            };
            playerBuilder.WithVideoOutput(videoOutput).WithVideoInput(videoChannel, videoRouteOptions);
        }

        using var player = playerBuilder.Build();

        // ── 9. Start ─────────────────────────────────────────────────────────

        Console.Write("Starting… ");
        try
        {
            // If we took the video path above, only the video capture thread is running.
            // Start the audio capture thread now — the NDI source is confirmed to be
            // streaming real content, so the audio ring will fill with real audio from T=0.
            // If we took the audio-only path (no video), Start() is called for the first
            // time here; it is idempotent so calling it twice is safe.
            avSource.StartAudioCapture();

            // Pre-buffer: wait for BOTH audio and video simultaneously so both rings start
            // from the same NDI timestamp, giving near-zero A/V offset at startup.
            Console.Write($"buffering (audio={profile.AudioPreBufferChunks}, video={profile.VideoPreBufferFrames})… ");
            using var preCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(
                    avSource.WaitForAudioBufferAsync(profile.AudioPreBufferChunks, preCts.Token),
                    videoChannel != null
                        ? avSource.WaitForVideoBufferAsync(profile.VideoPreBufferFrames, preCts.Token)
                        : Task.CompletedTask
                );
            }
            catch (OperationCanceledException) { /* timed out — proceed */ }

            await player.PlayAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\n  {ex.Message}");
            avSource.Stop();
            videoOutput?.Dispose();
            return;
        }
        Console.WriteLine("OK");

        Console.WriteLine($"\n  Playing NDI source: '{sourceName}'");
        Console.WriteLine("  Auto-reconnect: enabled (source can go offline and reconnect).");
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
                var state    = avSource.State;
                var aPos     = audioChannel.Position;
                var bufAvail = audioChannel.BufferAvailable;
                var line     = $"  [{DateTime.Now:HH:mm:ss}]  state={state}  audio={aPos:mm\\:ss\\.fff}  buf={bufAvail}";
                if (videoChannel != null)
                    line += $"  video={videoChannel.Position:mm\\:ss\\.fff}";
                if (avSource.TryGetAvDrift(out var drift))
                    line += $"  drift={drift.TotalMilliseconds,7:F1}ms";
                if (videoOutput != null)
                {
                    var snap = videoOutput.GetDiagnosticsSnapshot();
                    line += $"  displayed={snap.PresentedFrames}  unique={snap.UniqueFrames}" +
                            $"  dropped={snap.DroppedFrames}  reuse={snap.TextureReuseDraws}  black={snap.BlackFrames}";
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
        await player.StopAsync();
        avSource.Stop();
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

static bool PickYesNo(string label, bool defaultYes)
{
    string hint = defaultYes ? "Y/n" : "y/N";
    Console.Write($"{label} [{hint}]: ");
    string raw = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(raw)) return defaultYes;
    if (raw is "y" or "yes" or "true" or "1") return true;
    if (raw is "n" or "no" or "false" or "0") return false;
    return defaultYes;
}

static NDIEndpointPreset PickNdiPreset()
{
    Console.Write("NDI receive preset [Safe/Balanced/LowLatency/UltraLow] (default Balanced): ");
    string raw = (Console.ReadLine() ?? string.Empty).Trim();
    if (raw.Equals("safe", StringComparison.OrdinalIgnoreCase)) return NDIEndpointPreset.Safe;
    if (raw.Equals("low", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("low-latency", StringComparison.OrdinalIgnoreCase)) return NDIEndpointPreset.LowLatency;
    if (raw.Equals("ultra", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("ultralow", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("ultralowlatency", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("ultra-low", StringComparison.OrdinalIgnoreCase)) return NDIEndpointPreset.UltraLowLatency;
    return NDIEndpointPreset.Balanced;
}

static NDISourceOptions BuildSourceOptions(
    NDIEndpointPreset preset,
    int channels,
    NDIClockPolicy clockPolicy)
{
    var presetOptions = NDISourceOptions.ForPreset(preset, channels: channels);
    return new NDISourceOptions
    {
        SampleRate = presetOptions.SampleRate,
        Channels = presetOptions.Channels,
        QueueBufferDepth = presetOptions.QueueBufferDepth,
        AudioBufferDepth = presetOptions.AudioBufferDepth,
        VideoBufferDepth = presetOptions.VideoBufferDepth,
        LowLatency = presetOptions.LowLatency,
        AudioFramesPerCapture = presetOptions.AudioFramesPerCapture,
        EnableVideo = presetOptions.EnableVideo,
        ReceiverSettings = presetOptions.ReceiverSettings,
        ReconnectPolicy = presetOptions.ReconnectPolicy,
        MaxForwardPtsJumpMs = presetOptions.MaxForwardPtsJumpMs,
        FinderSettings = presetOptions.FinderSettings,
        ClockPolicy = clockPolicy,
    };
}
