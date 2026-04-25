// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.VideoPlayer
//   1. Enter a video file path
//   2. Opens an SDL3 window and plays the video
//   3. Close the window, press Enter, or Ctrl+C to stop
// ═══════════════════════════════════════════════════════════════════════════════

using FFmpeg.AutoGen;
using NDILib;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.NDI;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.SDL3;

#pragma warning disable 300

static (int Width, int Height) FitWithin(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
{
    srcWidth = srcWidth > 0 ? srcWidth : 1280;
    srcHeight = srcHeight > 0 ? srcHeight : 720;

    double scale = Math.Min((double)maxWidth / srcWidth, (double)maxHeight / srcHeight);
    scale = Math.Min(1.0, scale);

    int width = Math.Max(320, (int)Math.Round(srcWidth * scale));
    int height = Math.Max(180, (int)Math.Round(srcHeight * scale));
    return (width, height);
}

static string Fmt(TimeSpan ts)
{
    if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
    int hours = (int)ts.TotalHours;
    return hours > 0
        ? $"{hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
        : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
}

static YuvColorMatrix GetSuggestedMatrix(IVideoChannel channel)
    => channel is IVideoColorMatrixHint hint ? hint.SuggestedYuvColorMatrix : YuvColorMatrix.Auto;

static YuvColorRange GetSuggestedRange(IVideoChannel channel)
    => channel is IVideoColorMatrixHint hint ? hint.SuggestedYuvColorRange : YuvColorRange.Auto;

static NDIEndpointPreset ParseNDIPreset(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("safe", StringComparison.OrdinalIgnoreCase) || s.Equals("s", StringComparison.OrdinalIgnoreCase))
        return NDIEndpointPreset.Safe;
    // UltraLowLatency proved too aggressive for stable long-form playback in this
    // app; fold it into LowLatency unless a future profile adds explicit safety
    // guards (larger jitter headroom, adaptive fallback, etc.).
    if (s.Equals("ultralowlatency", StringComparison.OrdinalIgnoreCase) || s.Equals("ultra", StringComparison.OrdinalIgnoreCase) || s.Equals("u", StringComparison.OrdinalIgnoreCase))
        return NDIEndpointPreset.LowLatency;
    if (s.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) || s.Equals("low", StringComparison.OrdinalIgnoreCase) || s.Equals("l", StringComparison.OrdinalIgnoreCase))
        return NDIEndpointPreset.LowLatency;
    return NDIEndpointPreset.Balanced;
}

// Map an NDI endpoint preset to a router tick cadence.  The router drains push
// endpoints and fires its internal clock at this rate, so tighter presets need
// a tighter tick or frames sit idle in the router's push subscription for up
// to one full tick before reaching the NDI sink.
static TimeSpan RouterTickFor(NDIEndpointPreset preset) => preset switch
{
    NDIEndpointPreset.LowLatency      => TimeSpan.FromMilliseconds(5),  // ~200 Hz
    _                                 => TimeSpan.FromMilliseconds(10), // 100 Hz (default)
};

static ChannelRouteMap BuildAudioRouteMap(int srcChannels, int dstChannels)
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

static YuvColorMatrix ParseYuvColorMatrix(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("709", StringComparison.OrdinalIgnoreCase) || s.Equals("bt709", StringComparison.OrdinalIgnoreCase))
        return YuvColorMatrix.Bt709;
    if (s.Equals("601", StringComparison.OrdinalIgnoreCase) || s.Equals("bt601", StringComparison.OrdinalIgnoreCase))
        return YuvColorMatrix.Bt601;
    return YuvColorMatrix.Auto;
}

static YuvColorRange ParseYuvColorRange(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("full", StringComparison.OrdinalIgnoreCase) || s.Equals("f", StringComparison.OrdinalIgnoreCase))
        return YuvColorRange.Full;
    if (s.Equals("limited", StringComparison.OrdinalIgnoreCase) || s.Equals("l", StringComparison.OrdinalIgnoreCase))
        return YuvColorRange.Limited;
    return YuvColorRange.Auto;
}

static string MatrixLabel(YuvColorMatrix m) => m switch
{
    YuvColorMatrix.Bt709 => "709",
    YuvColorMatrix.Bt601 => "601",
    _ => "auto"
};

static string RangeLabel(YuvColorRange r) => r switch
{
    YuvColorRange.Full => "full",
    YuvColorRange.Limited => "limited",
    _ => "auto"
};

var cli = ParseArgs(args);
if (cli.ShowHelp)
{
    PrintUsage();
    return;
}

await RunAsync(cli);

static async Task RunAsync(CliOptions cli)
{

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Video Player   ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

// ── 1. Enter file path ───────────────────────────────────────────────────────

string filePath;
if (!string.IsNullOrWhiteSpace(cli.FilePath))
{
    filePath = cli.FilePath.Trim('"', ' ');
}
else if (cli.NoPrompt)
{
    Console.WriteLine("No input file provided. Use --file <path>.");
    return;
}
else
{
    Console.Write("Video file path: ");
    filePath = (Console.ReadLine() ?? "").Trim('"', ' ');
}

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 1b. Ask about NDI early (determines whether we need audio) ───────────────

bool enableNdi;
if (cli.EnableNdi.HasValue)
{
    enableNdi = cli.EnableNdi.Value;
}
else if (cli.NoPrompt)
{
    enableNdi = false;
}
else
{
    Console.Write("Enable NDI video sink? [y/N]: ");
    enableNdi = (Console.ReadLine() ?? string.Empty).Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
}

// Ask the preset up-front: it also controls the router's internal tick cadence,
// which must be known before the AVRouter is constructed.
var ndiPreset = cli.NdiPreset ?? NDIEndpointPreset.LowLatency;
if (enableNdi)
{
    if (!cli.NdiPreset.HasValue && !cli.NoPrompt)
    {
        Console.Write("NDI preset [Safe/Balanced/LowLatency] (default LowLatency): ");
        var presetText = Console.ReadLine();
        ndiPreset = string.IsNullOrWhiteSpace(presetText)
            ? NDIEndpointPreset.LowLatency
            : ParseNDIPreset(presetText);
    }
}

// ── 2. Open decoder ──────────────────────────────────────────────────────────

Console.Write("Opening decoder… ");
FFmpegDecoder decoder;
try
{
    decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
    {
        EnableAudio = enableNdi,   // only decode audio when NDI sink will consume it
        EnableVideo = true,
        // null = auto-detect: decoder outputs frames in the source's native pixel format
        // so the routing policy can select an efficient YUV shader path (no CPU conversion).
        VideoTargetPixelFormat = null
    });
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED\n  {ex.Message}");
    return;
}

if (decoder.VideoChannels.Count == 0)
{
    Console.WriteLine("No video streams in file.");
    decoder.Dispose();
    return;
}

using (decoder)
{
    var videoChannel = decoder.VideoChannels[0];
    var srcFmt       = videoChannel.SourceFormat;
    var suggestedMatrix = GetSuggestedMatrix(videoChannel);
    var suggestedRange = GetSuggestedRange(videoChannel);

    Console.WriteLine("OK");
    Console.WriteLine($"  Video: {srcFmt}");
    if (decoder.AudioChannels.Count > 0)
        Console.WriteLine($"  Audio: {decoder.AudioChannels[0].SourceFormat}");

    var initialWindow = FitWithin(srcFmt.Width, srcFmt.Height, maxWidth: 1920, maxHeight: 1080);
    Console.WriteLine($"  Window: {initialWindow.Width}x{initialWindow.Height} (fit)");

    // ── 3. Open video output ─────────────────────────────────────────────

    Console.Write("Creating SDL3 video output… ");
    using var videoOutput = new SDL3VideoEndpoint();
    YuvColorRange selectedRange;
    YuvColorMatrix selectedMatrix;
    bool limitRenderToInputFps;
    try
    {
        if (cli.LimitRenderToInputFps.HasValue)
        {
            limitRenderToInputFps = cli.LimitRenderToInputFps.Value;
        }
        else if (cli.NoPrompt)
        {
            // Prefer source-cadence rendering in unattended mode to reduce
            // CPU/GPU use for low-FPS content.
            limitRenderToInputFps = true;
        }
        else
        {
            Console.Write("Limit local render FPS to source FPS? [Y/n]: ");
            string raw = (Console.ReadLine() ?? string.Empty).Trim();
            limitRenderToInputFps = string.IsNullOrEmpty(raw) || ParseOnOff(raw);
        }

        videoOutput.LimitRenderToInputFps = limitRenderToInputFps;
        videoOutput.Open("MFPlayer — Video Player",
            initialWindow.Width,
            initialWindow.Height,
            srcFmt);

        if (cli.YuvRange.HasValue)
        {
            selectedRange = cli.YuvRange.Value;
        }
        else if (cli.NoPrompt)
        {
            selectedRange = suggestedRange;
        }
        else
        {
            Console.Write($"YUV shader range [auto/full/limited] (default {RangeLabel(suggestedRange)}): ");
            string? rangeText = Console.ReadLine();
            selectedRange = string.IsNullOrWhiteSpace(rangeText)
                ? suggestedRange
                : ParseYuvColorRange(rangeText);
        }
        videoOutput.YuvColorRange = selectedRange;

        if (cli.YuvMatrix.HasValue)
        {
            selectedMatrix = cli.YuvMatrix.Value;
        }
        else if (cli.NoPrompt)
        {
            selectedMatrix = suggestedMatrix;
        }
        else
        {
            Console.Write($"YUV shader matrix [auto/601/709] (default {MatrixLabel(suggestedMatrix)}): ");
            string? matrixText = Console.ReadLine();
            selectedMatrix = string.IsNullOrWhiteSpace(matrixText)
                ? suggestedMatrix
                : ParseYuvColorMatrix(matrixText);
        }
        videoOutput.YuvColorMatrix = selectedMatrix;

        var resolvedRange = YuvAutoPolicy.ResolveRange(selectedRange);
        var resolvedMatrix = YuvAutoPolicy.ResolveMatrix(selectedMatrix, srcFmt.Width, srcFmt.Height);
        Console.WriteLine($"  Local render FPS limit: {(limitRenderToInputFps ? "source FPS" : "display refresh")}");
        Console.WriteLine($"  YUV policy: req[{RangeLabel(selectedRange)}/{MatrixLabel(selectedMatrix)}] -> resolved[{RangeLabel(resolvedRange)}/{MatrixLabel(resolvedMatrix)}], hint[{RangeLabel(suggestedRange)}/{MatrixLabel(suggestedMatrix)}]");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    // ── 4. Configure optional NDI sink ──────────────────────────────────

    var localVideoDelay = TimeSpan.FromMilliseconds(cli.LocalVideoDelayMs ?? 0);
    var ndiDelay = TimeSpan.FromMilliseconds(cli.NdiDelayMs ?? 0);

    // Optional: mirror the same active channel(s) to an NDI A/V sink.
    NDIRuntime? ndiRuntime = null;
    NDISender? ndiSender = null;
    NDIAVEndpoint? ndiSink = null;
    ChannelRouteMap? ndiAudioRouteMap = null;
    IAudioChannel? ndiAudioChannel = null;

    if (enableNdi)
    {
        if (!NDIRuntime.IsSupportedCpu())
        {
            Console.WriteLine("  NDI disabled: CPU does not meet NDI requirements.");
        }
        else
        {
            int rt = NDIRuntime.Create(out ndiRuntime);
            if (rt != 0 || ndiRuntime == null)
            {
                Console.WriteLine($"  NDI disabled: runtime init failed ({rt}).");
            }
            else
            {
                string senderName = cli.NdiSenderName ?? string.Empty;
                if (!cli.NoPrompt)
                    Console.Write("NDI source name [MFPlayer NDI Video]: ");
                if (string.IsNullOrWhiteSpace(senderName) && !cli.NoPrompt)
                    senderName = (Console.ReadLine() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(senderName)) senderName = "MFPlayer NDI Video";

                var preset = ndiPreset;

                bool preferPerformanceOverQuality;
                if (cli.NdiPreferPerformance.HasValue)
                {
                    preferPerformanceOverQuality = cli.NdiPreferPerformance.Value;
                }
                else if (cli.NoPrompt)
                {
                    preferPerformanceOverQuality = false;
                }
                else
                {
                    Console.Write("NDI mode [quality/performance] (default quality): ");
                    var ndiMode = (Console.ReadLine() ?? string.Empty).Trim();
                    preferPerformanceOverQuality =
                        ndiMode.Equals("performance", StringComparison.OrdinalIgnoreCase)
                        || ndiMode.Equals("perf", StringComparison.OrdinalIgnoreCase)
                        || ndiMode.Equals("p", StringComparison.OrdinalIgnoreCase);
                }

                // Disable NDI's SDK rate-clock.  With clockVideo/clockAudio=true the
                // SDK blocks each send until the next nominal frame boundary, adding
                // up to one full frame of wall-clock latency between what SDL3
                // presents locally and what NDI receivers see.  We pace the streams
                // ourselves via the router's push tick + NDIAvTimingContext (shared
                // timecodes stamped from the master clock), and the audio-warmup
                // gate below ensures both outputs start from the same origin, so
                // the SDK's per-stream limiter is pure added delay.
                int sret = NDISender.Create(out ndiSender, senderName, clockVideo: false, clockAudio: false);
                if (sret != 0 || ndiSender == null)
                {
                    Console.WriteLine($"  NDI disabled: sender creation failed ({sret}).");
                    ndiRuntime.Dispose();
                    ndiRuntime = null;
                }
                else
                {
                    AudioFormat? ndiAudioFormat = null;
                    if (decoder.AudioChannels.Count > 0)
                    {
                        ndiAudioChannel = decoder.AudioChannels[0];
                        var srcAudio = ndiAudioChannel.SourceFormat;
                        ndiAudioFormat = new AudioFormat(48000, Math.Min(srcAudio.Channels, 2));
                        ndiAudioRouteMap = BuildAudioRouteMap(srcAudio.Channels, ndiAudioFormat.Value.Channels);
                    }

                    ndiSink = new NDIAVEndpoint(ndiSender, new NDIAVSinkOptions
                    {
                        VideoTargetFormat            = videoOutput.OutputFormat,
                        AudioTargetFormat            = ndiAudioFormat,
                        Preset                       = preset,
                        Name                         = $"NDIAVEndpoint({senderName})",
                        PreferPerformanceOverQuality = preferPerformanceOverQuality,
                        AudioFramesPerBuffer         = 1024,
                        // Drift correction is queue-depth-controlled.  With
                        // clockAudio:false the NDI SDK no longer back-pressures
                        // sends, so the pending queue stays near zero forever →
                        // the corrector saturates at +maxCorrection and runs the
                        // audio resampler permanently fast (→ cumulative
                        // distortion from the last-frame-hold padding + audio
                        // pulling ahead of video).  Leave it OFF on the async
                        // send path; A↔V alignment is handled by stamped
                        // timecodes (NDIAvTimingContext), not rate nudging.
                        EnableAudioDriftCorrection   = false,
                    });
                    Console.WriteLine($"  NDI sink enabled: {senderName} ({preset}, mode={(preferPerformanceOverQuality ? "perf" : "quality")})");
                    if (decoder.AudioChannels.Count > 0)
                        Console.WriteLine("  NDI audio enabled from source audio track.");
                }
            }
        }
    }

    // ── 4b. Build player graph (builder API) ───────────────────────────

    var playerBuilder = MediaPlayer.Create()
        .WithRouterOptions(new AVRouterOptions
        {
            // Tighter tick cadence → frames sit in the push subscription for less time
            // before being flushed to the NDI sink. Higher CPU at tighter values.
            InternalTickCadence = enableNdi ? RouterTickFor(ndiPreset) : TimeSpan.FromMilliseconds(10),
        })
        .WithVideoOutput(videoOutput)
        .WithVideoInput(videoChannel)
        .WithClock(videoOutput.Clock, ClockPriority.Hardware);

    if (ndiSink != null)
        playerBuilder.WithAVOutput(ndiSink);
    if (ndiAudioChannel != null)
        playerBuilder.WithAudioInput(ndiAudioChannel);

    using var player = playerBuilder.Build();

    // Rewire routes explicitly so per-endpoint delays remain configurable.
    // The builder's default external-input auto-routes use default options.
    var router = player.Router;
    var graph = router.GetDiagnosticsSnapshot();
    foreach (var route in graph.Routes)
        router.RemoveRoute(route.Id);

    static InputId RequireInputId(RouterDiagnosticsSnapshot diag, string kind)
    {
        for (int i = 0; i < diag.Inputs.Count; i++)
        {
            if (string.Equals(diag.Inputs[i].Kind, kind, StringComparison.Ordinal))
                return diag.Inputs[i].Id;
        }

        throw new InvalidOperationException($"Expected an input of kind '{kind}' in the builder-wired graph.");
    }

    static EndpointId RequireEndpointId(RouterDiagnosticsSnapshot diag, string kind)
    {
        for (int i = 0; i < diag.Endpoints.Count; i++)
        {
            if (string.Equals(diag.Endpoints[i].Kind, kind, StringComparison.Ordinal))
                return diag.Endpoints[i].Id;
        }

        throw new InvalidOperationException($"Expected an endpoint of kind '{kind}' in the builder-wired graph.");
    }

    static bool TryFindEndpointId(RouterDiagnosticsSnapshot diag, string kind, out EndpointId id)
    {
        for (int i = 0; i < diag.Endpoints.Count; i++)
        {
            if (string.Equals(diag.Endpoints[i].Kind, kind, StringComparison.Ordinal))
            {
                id = diag.Endpoints[i].Id;
                return true;
            }
        }

        id = default;
        return false;
    }

    var videoInputId = RequireInputId(graph, "Video");
    var localVideoEndpointId = RequireEndpointId(graph, "Video");
    router.CreateRoute(videoInputId, localVideoEndpointId, new VideoRouteOptions
    {
        TimeOffset = localVideoDelay
    });

    if (ndiSink != null && TryFindEndpointId(graph, "AV", out var ndiEndpointId))
    {
        router.CreateRoute(videoInputId, ndiEndpointId, new VideoRouteOptions
        {
            TimeOffset = ndiDelay
        });

        if (ndiAudioChannel != null && ndiAudioRouteMap != null)
        {
            var audioInputId = RequireInputId(graph, "Audio");
            router.CreateRoute(audioInputId, ndiEndpointId, new AudioRouteOptions
            {
                ChannelMap = ndiAudioRouteMap,
                TimeOffset = ndiDelay
            });
        }
    }

    if (ndiDelay != TimeSpan.Zero || localVideoDelay != TimeSpan.Zero)
        Console.WriteLine($"  Route delays: local-video={localVideoDelay.TotalMilliseconds:+0;-0;0} ms, ndi={ndiDelay.TotalMilliseconds:+0;-0;0} ms");

    // ── 5. Auto-stop on window close ─────────────────────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    if (cli.AutoStopSeconds.HasValue && cli.AutoStopSeconds.Value > 0)
    {
        int autoStopSeconds = cli.AutoStopSeconds.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(autoStopSeconds), cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    Console.WriteLine($"\n[Auto stop after {autoStopSeconds}s]");
                    cts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    videoOutput.WindowClosed += () =>
    {
        Console.WriteLine("\n[Window closed]");
        if (!cts.IsCancellationRequested) cts.Cancel();
    };

    // ── 5b. Auto-stop on end-of-stream ───────────────────────────────────
    //
    // EndOfStream fires as soon as the demuxer hits EOF, but the decoder's
    // per-subscription rings (and NDI/PortAudio pending queues) still contain
    // decoded frames/samples that have not reached an endpoint yet. Cancelling
    // immediately would cut off the last few hundred ms of content. Instead,
    // mark the event and let the drain task below await the ring drainage.
    var videoEos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var audioEos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    videoChannel.EndOfStream += (_, _) => videoEos.TrySetResult();
    IAudioChannel? audioChannelForEos = decoder.AudioChannels.Count > 0 ? decoder.AudioChannels[0] : null;
    if (audioChannelForEos is not null)
        audioChannelForEos.EndOfStream += (_, _) => audioEos.TrySetResult();
    else
        audioEos.TrySetResult(); // no audio track — nothing to wait for

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.WhenAll(videoEos.Task, audioEos.Task).WaitAsync(cts.Token);

            // Drain: wait until every channel's subscription rings have
            // emptied (decoder produced everything AND consumers drained it).
            // Polling at 20 ms is fine here — the grace window is governed by
            // real content remaining, not poll granularity.
            var drainStart = System.Diagnostics.Stopwatch.StartNew();
            while (!cts.IsCancellationRequested && drainStart.Elapsed < TimeSpan.FromSeconds(10))
            {
                int vQueued = videoChannel.BufferAvailable;
                int aQueued = audioChannelForEos?.BufferAvailable ?? 0;
                if (vQueued == 0 && aQueued == 0)
                    break;
                try { await Task.Delay(20, cts.Token); }
                catch (OperationCanceledException) { break; }
            }

            if (!cts.IsCancellationRequested)
            {
                Console.WriteLine("\n[End of stream]");
                cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    });

    // ── 6. Start playback ────────────────────────────────────────────────

    decoder.Start();

    // Pre-roll: decoders (especially AAC audio with edit-list priming) can take
    // ~1 s of wall-clock before they deliver their first sample.  If we start
    // the video output + router immediately, the video decoder — which has no
    // priming — streams out right away while audio lags by the warmup interval,
    // producing:
    //   • NDI sink: a permanent +1 s "video ahead of audio" offset (fixed by
    //     in-sink PTS pacing, but at the cost of video running ~1 s behind
    //     the local preview window).
    //   • Local SDL: video playing at real-time while NDI lags behind.
    //
    // Waiting here for the audio channel to have *any* decoded samples before
    // starting the rest of the pipeline collapses that asymmetry: SDL and NDI
    // both begin at the same "audio-ready" origin, the in-sink pacing becomes
    // a no-op (audio PTS is already live when the first video frame arrives),
    // and SDL ↔ NDI stay aligned to within a few milliseconds.
    if (decoder.AudioChannels.Count > 0)
    {
        var audioCh = decoder.AudioChannels[0];
        var warmupSw = System.Diagnostics.Stopwatch.StartNew();
        int lastReported = -1;
        Console.Write("Waiting for audio decoder warmup… ");
        while (audioCh.BufferAvailable == 0 && warmupSw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
            int whole = (int)warmupSw.Elapsed.TotalSeconds;
            if (whole != lastReported)
            {
                Console.Write($"{whole}s ");
                lastReported = whole;
            }
        }
        Console.WriteLine($"ready after {warmupSw.Elapsed.TotalMilliseconds:F0} ms " +
            $"(audio.buf={audioCh.BufferAvailable} samples).");
    }

    await player.PlayAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Close the window or press [Ctrl+C] to stop.");
    if (!cli.NoPrompt && !Console.IsInputRedirected)
        Console.WriteLine("Press [Enter] to stop.");
    Console.WriteLine();

    var outputForStats = videoOutput;
    var channelForStats = videoChannel;
    var endpointForStats = ndiSink as IVideoEndpoint;
    var ndiSinkForStats = ndiSink;
    var audioChannelForStats = decoder.AudioChannels.Count > 0 ? decoder.AudioChannels[0] : null;
    var statsTask = Task.Run(async () =>
    {
        SDL3VideoEndpoint.DiagnosticsSnapshot? prevOutput = null;
        VideoEndpointDiagnosticsSnapshot? prevEndpoint = null;
        NDIAVEndpoint.AvSyncSnapshot? prevAvSync = null;
        double? prevPtsDeltaMs = null;
        var pairGapWindowMs = new Queue<double>();
        var tcDeltaWindowMs = new Queue<double>();
        double expectedFps = srcFmt.FrameRate > 0 ? srcFmt.FrameRate : 30.0;
        double expectedFrameMs = expectedFps > 0 ? 1000.0 / expectedFps : 0.0;
        const int rollingWindow = 60;
        const int ndiSampleRate = 48000;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cts.IsCancellationRequested)
                break;

            var os = outputForStats.GetDiagnosticsSnapshot();
            var es = endpointForStats?.GetDiagnosticsSnapshot();
            var avs = ndiSinkForStats?.GetAvSyncSnapshot();

            if (prevOutput.HasValue)
            {
                var o0 = prevOutput.Value;
                var o1 = os;

                long renderDelta = o1.LoopIterations - o0.LoopIterations;
                long presentDelta = o1.PresentedFrames - o0.PresentedFrames;
                long contentDelta = o1.UniqueFrames - o0.UniqueFrames;
                long blackDelta = o1.BlackFrames - o0.BlackFrames;
                long bgraDelta = o1.BgraFrames - o0.BgraFrames;
                long rgbaDelta = o1.RgbaFrames - o0.RgbaFrames;
                long nv12Delta = o1.Nv12Frames - o0.Nv12Frames;
                long y420Delta = o1.Yuv420pFrames - o0.Yuv420pFrames;
                long y422P10Delta = o1.Yuv422p10Frames - o0.Yuv422p10Frames;
                long exDelta = o1.RenderExceptions - o0.RenderExceptions;
                string speedMark = contentDelta < Math.Max(1, (long)Math.Round(expectedFps * 0.75)) ? " slow" : "";
                string exMark = exDelta > 0 ? " ex" : "";

                string endpointText = "ep=n/a";
                if (es.HasValue)
                {
                    if (prevEndpoint.HasValue)
                    {
                        var e0 = prevEndpoint.Value;
                        var e1 = es.Value;
                        endpointText =
                            $"ep=pass:{e1.PassthroughFrames - e0.PassthroughFrames,3} conv:{e1.ConvertedFrames - e0.ConvertedFrames,3} drop:{e1.DroppedFrames - e0.DroppedFrames,3} q:{e1.QueueDepth,2}";
                    }
                    else
                    {
                        endpointText =
                            $"ep=pass:{es.Value.PassthroughFrames,3} conv:{es.Value.ConvertedFrames,3} drop:{es.Value.DroppedFrames,3} q:{es.Value.QueueDepth,2}";
                    }
                }

                Console.WriteLine(
                    $"[vstats] clock={Fmt(outputForStats.Clock.Position)} src={Fmt(channelForStats.Position)} " +
                    $"fps(content={contentDelta,3}/{expectedFps,5:F1}, display={presentDelta,3}) r={renderDelta,4} p={presentDelta,4} b={blackDelta,3} " +
                    $"ex={exDelta,2}{speedMark}{exMark}");
                Console.WriteLine($"         fmt=bgra:{bgraDelta,3} rgba:{rgbaDelta,3} nv12:{nv12Delta,3} y420:{y420Delta,3} y422p10:{y422P10Delta,3}");
                Console.WriteLine($"         {endpointText}");

                // ── NDI A/V submission timing ────────────────────────────────
                if (avs.HasValue)
                {
                    var a1 = avs.Value;

                    // Per-second deltas
                    long vSubThisSec = 0, aSubThisSec = 0, aSamplesThisSec = 0;
                    if (prevAvSync.HasValue)
                    {
                        var a0 = prevAvSync.Value;
                        vSubThisSec = a1.VideoFramesSubmitted - a0.VideoFramesSubmitted;
                        aSubThisSec = a1.AudioBuffersSubmitted - a0.AudioBuffersSubmitted;
                        aSamplesThisSec = a1.AudioSamplesSubmitted - a0.AudioSamplesSubmitted;
                    }

                    string firstGap = a1.FirstSubmitGapMs == long.MinValue
                        ? "--"
                        : $"{a1.FirstSubmitGapMs,+5}ms";
                    string tcDelta = a1.LastTimecodeDeltaTicks == long.MinValue
                        ? "--"
                        : $"{TimeSpan.FromTicks(a1.LastTimecodeDeltaTicks).TotalMilliseconds,+7:F1}ms";
                    string ptsDelta = a1.LastPtsDeltaTicks == long.MinValue
                        ? "--"
                        : $"{TimeSpan.FromTicks(a1.LastPtsDeltaTicks).TotalMilliseconds,+7:F1}ms";
                    double? tcDeltaMs = a1.LastTimecodeDeltaTicks == long.MinValue
                        ? null
                        : TimeSpan.FromTicks(a1.LastTimecodeDeltaTicks).TotalMilliseconds;
                    double? ptsDeltaMs = a1.LastPtsDeltaTicks == long.MinValue
                        ? null
                        : TimeSpan.FromTicks(a1.LastPtsDeltaTicks).TotalMilliseconds;

                    // What the receiver "sees": at-last-video-submit, how far behind was audio?
                    long pairGapMs = a1.LastVideoSubmitMs - a1.AudioMsAtLastVideoSubmit;
                    string pairStr = a1.AudioMsAtLastVideoSubmit == 0 && a1.FirstAudioSubmitMs < 0
                        ? "audio-not-started"
                        : $"{pairGapMs,+5}ms (video@{a1.LastVideoSubmitMs}ms, audio@{a1.AudioMsAtLastVideoSubmit}ms)";

                    if (a1.AudioMsAtLastVideoSubmit != 0 || a1.FirstAudioSubmitMs >= 0)
                        PushRollingSample(pairGapWindowMs, pairGapMs, rollingWindow);
                    if (tcDeltaMs.HasValue)
                        PushRollingSample(tcDeltaWindowMs, tcDeltaMs.Value, rollingWindow);

                    string vTcStr = a1.LastVideoTimecodeTicks == long.MinValue ? "--"
                        : a1.LastVideoTimecodeTicks == long.MaxValue ? "SYNTHESIZE"
                        : $"{TimeSpan.FromTicks(a1.LastVideoTimecodeTicks).TotalMilliseconds:F1}ms";
                    string aTcStr = a1.LastAudioTimecodeTicks == long.MinValue ? "--"
                        : a1.LastAudioTimecodeTicks == long.MaxValue ? "SYNTHESIZE"
                        : $"{TimeSpan.FromTicks(a1.LastAudioTimecodeTicks).TotalMilliseconds:F1}ms";
                    string vPtsStr = a1.LastVideoPtsTicks == long.MinValue ? "--"
                        : $"{TimeSpan.FromTicks(Math.Max(0, a1.LastVideoPtsTicks)).TotalMilliseconds:F1}ms";
                    string aPtsStr = a1.LastAudioPtsTicks == long.MinValue ? "--"
                        : $"{TimeSpan.FromTicks(Math.Max(0, a1.LastAudioPtsTicks)).TotalMilliseconds:F1}ms";

                    string audioPosStr = audioChannelForStats != null
                        ? Fmt(audioChannelForStats.Position)
                        : "--";

                    Console.WriteLine(
                        $"         ndi v/s={vSubThisSec,3} a/s={aSubThisSec,3} ({aSamplesThisSec,5}smp) " +
                        $"1st-gap={firstGap} pair-gap={pairStr}");
                    Console.WriteLine(
                        $"         ndi tc v={vTcStr} a={aTcStr} Δtc={tcDelta}   pts v={vPtsStr} a={aPtsStr} Δpts={ptsDelta}");

                    double? audioBufferMs = aSubThisSec > 0
                        ? (aSamplesThisSec / (double)aSubThisSec) * 1000.0 / ndiSampleRate
                        : null;
                    string tcNorm = tcDeltaMs.HasValue && audioBufferMs is > 0
                        ? $"{tcDeltaMs.Value / audioBufferMs.Value,+6:F2}x"
                        : "--";
                    string ptsNorm = ptsDeltaMs.HasValue && expectedFrameMs > 0
                        ? $"{ptsDeltaMs.Value / expectedFrameMs,+6:F2}x"
                        : "--";

                    string discontinuity = "";
                    if (ptsDeltaMs.HasValue && prevPtsDeltaMs.HasValue && expectedFrameMs > 0)
                    {
                        double deltaStep = Math.Abs(ptsDeltaMs.Value - prevPtsDeltaMs.Value);
                        if (deltaStep > expectedFrameMs * 1.25)
                            discontinuity = " *pts-jump";
                    }
                    if (ptsDeltaMs.HasValue)
                        prevPtsDeltaMs = ptsDeltaMs.Value;

                    Console.WriteLine(
                        $"         ndi norm Δtc/buf={tcNorm} Δpts/frame={ptsNorm}{discontinuity} " +
                        $"pair-gap p50/p95={FormatRollingStats(pairGapWindowMs)} Δtc p50/p95={FormatRollingStats(tcDeltaWindowMs)}");
                    Console.WriteLine(
                        $"         ch audio.pos={audioPosStr} video.pos={Fmt(channelForStats.Position)} clock={Fmt(outputForStats.Clock.Position)}");
                }
            }

            prevOutput = os;
            prevEndpoint = es;
            prevAvSync = avs;
        }
    }, cts.Token);

    // Wait for Ctrl+C, Enter (interactive only), window close, or cancellation.
    if (!cli.NoPrompt && !Console.IsInputRedirected)
        _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // ── 7. Stop ──────────────────────────────────────────────────────────

    Console.Write("\nStopping… ");
    cts.Cancel();
    try { await statsTask; } catch (OperationCanceledException) { }
    await player.StopAsync();
    ndiSink?.Dispose();
    ndiSender?.Dispose();
    ndiRuntime?.Dispose();
    Console.WriteLine("Done.");
}

}

static void PrintUsage()
{
    Console.WriteLine("MFPlayer.VideoPlayer options:");
    Console.WriteLine("  --file <path>                Video file path (non-interactive)");
    Console.WriteLine("  --ndi <on|off>               Enable/disable NDI sink");
    Console.WriteLine("  --limit-render-fps <on|off>  Limit local render FPS to source FPS");
    Console.WriteLine("  --ndi-preset <safe|balanced|lowlatency>");
    Console.WriteLine("  --ndi-name <name>            NDI sender/source name");
    Console.WriteLine("  --ndi-mode <quality|performance>");
    Console.WriteLine("  --ndi-delay-ms <ms>          Delay only NDI route(s) by ms");
    Console.WriteLine("  --local-video-delay-ms <ms>  Delay local SDL video route by ms");
    Console.WriteLine("  --yuv-range <auto|full|limited>");
    Console.WriteLine("  --yuv-matrix <auto|601|709>");
    Console.WriteLine("  --auto-stop-sec <seconds>    Auto-stop timer");
    Console.WriteLine("  --no-prompt                  Disable all interactive prompts");
    Console.WriteLine("  --help                       Show this help");
}

static CliOptions ParseArgs(string[] args)
{
    var cli = new CliOptions();
    int i = 0;

    while (i < args.Length)
    {
        string a = args[i];
        switch (a)
        {
            case "--help":
            case "-h":
                cli.ShowHelp = true;
                i++;
                break;

            case "--no-prompt":
                cli.NoPrompt = true;
                i++;
                break;

            case "--file":
                if (i + 1 < args.Length) cli.FilePath = args[++i];
                i++;
                break;

            case "--ndi":
                if (i + 1 < args.Length) cli.EnableNdi = ParseOnOff(args[++i]);
                i++;
                break;

            case "--limit-render-fps":
                if (i + 1 < args.Length) cli.LimitRenderToInputFps = ParseOnOff(args[++i]);
                i++;
                break;

            case "--ndi-preset":
                if (i + 1 < args.Length) cli.NdiPreset = ParseNDIPreset(args[++i]);
                i++;
                break;

            case "--ndi-name":
                if (i + 1 < args.Length) cli.NdiSenderName = args[++i];
                i++;
                break;

            case "--ndi-mode":
                if (i + 1 < args.Length)
                {
                    var mode = args[++i];
                    cli.NdiPreferPerformance =
                        mode.Equals("performance", StringComparison.OrdinalIgnoreCase)
                        || mode.Equals("perf", StringComparison.OrdinalIgnoreCase)
                        || mode.Equals("p", StringComparison.OrdinalIgnoreCase);
                }
                i++;
                break;

            case "--yuv-range":
                if (i + 1 < args.Length) cli.YuvRange = ParseYuvColorRange(args[++i]);
                i++;
                break;

            case "--ndi-delay-ms":
                if (i + 1 < args.Length && double.TryParse(args[++i], out var ndiDelayMs))
                    cli.NdiDelayMs = ndiDelayMs;
                i++;
                break;

            case "--local-video-delay-ms":
                if (i + 1 < args.Length && double.TryParse(args[++i], out var localDelayMs))
                    cli.LocalVideoDelayMs = localDelayMs;
                i++;
                break;

            case "--yuv-matrix":
                if (i + 1 < args.Length) cli.YuvMatrix = ParseYuvColorMatrix(args[++i]);
                i++;
                break;

            case "--auto-stop-sec":
                if (i + 1 < args.Length && int.TryParse(args[++i], out var sec) && sec > 0)
                    cli.AutoStopSeconds = sec;
                i++;
                break;

            default:
                // First positional arg is treated as --file for convenience.
                if (!a.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(cli.FilePath))
                    cli.FilePath = a;
                i++;
                break;
        }
    }

    return cli;
}

static void PushRollingSample(Queue<double> window, double value, int maxSamples)
{
    window.Enqueue(value);
    while (window.Count > maxSamples)
        window.Dequeue();
}

static string FormatRollingStats(Queue<double> window)
{
    if (window.Count == 0)
        return "--/--";

    var samples = window.ToArray();
    Array.Sort(samples);
    double p50 = Percentile(samples, 0.50);
    double p95 = Percentile(samples, 0.95);
    return $"{p50:+0.0;-0.0;0.0}ms/{p95:+0.0;-0.0;0.0}ms";
}

static double Percentile(double[] sortedSamples, double percentile)
{
    if (sortedSamples.Length == 0)
        return 0;

    if (percentile <= 0) return sortedSamples[0];
    if (percentile >= 1) return sortedSamples[^1];

    double index = percentile * (sortedSamples.Length - 1);
    int lo = (int)Math.Floor(index);
    int hi = (int)Math.Ceiling(index);
    if (lo == hi) return sortedSamples[lo];
    double t = index - lo;
    return sortedSamples[lo] + (sortedSamples[hi] - sortedSamples[lo]) * t;
}

static bool ParseOnOff(string value)
{
    return value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value.Equals("y", StringComparison.OrdinalIgnoreCase)
           || value.Equals("1", StringComparison.OrdinalIgnoreCase);
}

sealed class CliOptions
{
    public string? FilePath { get; set; }
    public bool? EnableNdi { get; set; }
    public NDIEndpointPreset? NdiPreset { get; set; }
    public string? NdiSenderName { get; set; }
    public bool? NdiPreferPerformance { get; set; }
    public bool? LimitRenderToInputFps { get; set; }
    public YuvColorRange? YuvRange { get; set; }
    public YuvColorMatrix? YuvMatrix { get; set; }
    public double? NdiDelayMs { get; set; }
    public double? LocalVideoDelayMs { get; set; }
    public int? AutoStopSeconds { get; set; }
    public bool NoPrompt { get; set; }
    public bool ShowHelp { get; set; }
}




#pragma warning restore 300
