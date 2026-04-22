// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.VideoPlayer
//   1. Enter a video file path
//   2. Opens an SDL3 window and plays the video
//   3. Close the window, press Enter, or Ctrl+C to stop
// ═══════════════════════════════════════════════════════════════════════════════

using FFmpeg.AutoGen;
using NDILib;
using S.Media.FFmpeg;
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

await RunAsync();

static async Task RunAsync()
{

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Video Player   ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

// ── 1. Enter file path ───────────────────────────────────────────────────────

Console.Write("Video file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 1b. Ask about NDI early (determines whether we need audio) ───────────────

Console.Write("Enable NDI video sink? [y/N]: ");
bool enableNdi = (Console.ReadLine() ?? string.Empty).Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

// Ask the preset up-front: it also controls the router's internal tick cadence,
// which must be known before the AVRouter is constructed.
var ndiPreset = NDIEndpointPreset.LowLatency;
if (enableNdi)
{
    Console.Write("NDI preset [Safe/Balanced/LowLatency] (default LowLatency): ");
    var presetText = Console.ReadLine();
    ndiPreset = string.IsNullOrWhiteSpace(presetText)
        ? NDIEndpointPreset.LowLatency
        : ParseNDIPreset(presetText);
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
    using var videoOutput = new SDL3VideoOutput();
    YuvColorRange selectedRange;
    YuvColorMatrix selectedMatrix;
    try
    {
        videoOutput.Open("MFPlayer — Video Player",
            initialWindow.Width,
            initialWindow.Height,
            srcFmt);

        Console.Write($"YUV shader range [auto/full/limited] (default {RangeLabel(suggestedRange)}): ");
        string? rangeText = Console.ReadLine();
        selectedRange = string.IsNullOrWhiteSpace(rangeText)
            ? suggestedRange
            : ParseYuvColorRange(rangeText);
        videoOutput.YuvColorRange = selectedRange;

        Console.Write($"YUV shader matrix [auto/601/709] (default {MatrixLabel(suggestedMatrix)}): ");
        string? matrixText = Console.ReadLine();
        selectedMatrix = string.IsNullOrWhiteSpace(matrixText)
            ? suggestedMatrix
            : ParseYuvColorMatrix(matrixText);
        videoOutput.YuvColorMatrix = selectedMatrix;

        var resolvedRange = YuvAutoPolicy.ResolveRange(selectedRange);
        var resolvedMatrix = YuvAutoPolicy.ResolveMatrix(selectedMatrix, srcFmt.Width, srcFmt.Height);
        Console.WriteLine($"  YUV policy: req[{RangeLabel(selectedRange)}/{MatrixLabel(selectedMatrix)}] -> resolved[{RangeLabel(resolvedRange)}/{MatrixLabel(resolvedMatrix)}], hint[{RangeLabel(suggestedRange)}/{MatrixLabel(suggestedMatrix)}]");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    // ── 4. Wire up ───────────────────────────────────────────────────────

    using var router = new AVRouter(new AVRouterOptions
    {
        // Tighter tick cadence → frames sit in the push subscription for less time
        // before being flushed to the NDI sink.  Higher CPU at tighter values
        // (the spin-wait tail in PushVideoThreadLoop).
        InternalTickCadence = enableNdi ? RouterTickFor(ndiPreset) : TimeSpan.FromMilliseconds(10),
    });
    var videoEpId = router.RegisterEndpoint(videoOutput);
    router.SetClock(videoOutput.Clock);

    var videoInputId = router.RegisterVideoInput(videoChannel);
    router.CreateRoute(videoInputId, videoEpId);

    // Optional: route the same active channel to an NDI A/V sink.
    NDIRuntime? ndiRuntime = null;
    NDISender? ndiSender = null;
    NDIAVSink? ndiSink = null;

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
                Console.Write("NDI source name [MFPlayer NDI Video]: ");
                string senderName = (Console.ReadLine() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(senderName)) senderName = "MFPlayer NDI Video";

                var preset = ndiPreset;

                Console.Write("NDI mode [quality/performance] (default quality): ");
                var ndiMode = (Console.ReadLine() ?? string.Empty).Trim();
                bool preferPerformanceOverQuality =
                    ndiMode.Equals("performance", StringComparison.OrdinalIgnoreCase)
                    || ndiMode.Equals("perf", StringComparison.OrdinalIgnoreCase)
                    || ndiMode.Equals("p", StringComparison.OrdinalIgnoreCase);

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
                    ChannelRouteMap? routeMap = null;
                    if (decoder.AudioChannels.Count > 0)
                    {
                        var sourceAudioChannel = decoder.AudioChannels[0];
                        var srcAudio = sourceAudioChannel.SourceFormat;
                        ndiAudioFormat = new AudioFormat(48000, Math.Min(srcAudio.Channels, 2));
                        routeMap = BuildAudioRouteMap(srcAudio.Channels, ndiAudioFormat.Value.Channels);
                    }

                    ndiSink = new NDIAVSink(ndiSender, new NDIAVSinkOptions
                    {
                        VideoTargetFormat            = videoOutput.OutputFormat,
                        AudioTargetFormat            = ndiAudioFormat,
                        Preset                       = preset,
                        Name                         = $"NDIAVSink({senderName})",
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
                    var ndiEpId = router.RegisterEndpoint(ndiSink);
                    router.CreateRoute(videoInputId, ndiEpId);
                    await ndiSink.StartAsync();

                    if (decoder.AudioChannels.Count > 0 && routeMap != null)
                    {
                        var sourceAudioChannel = decoder.AudioChannels[0];
                        var audioInputId = router.RegisterAudioInput(sourceAudioChannel);
                        router.CreateRoute(audioInputId, ndiEpId, new AudioRouteOptions { ChannelMap = routeMap });
                    }

                    Console.WriteLine($"  NDI sink enabled: {senderName} ({preset}, mode={(preferPerformanceOverQuality ? "perf" : "quality")})");
                    if (decoder.AudioChannels.Count > 0)
                        Console.WriteLine("  NDI audio enabled from source audio track.");
                }
            }
        }
    }

    // ── 5. Auto-stop on window close ─────────────────────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    videoOutput.WindowClosed += () =>
    {
        Console.WriteLine("\n[Window closed]");
        if (!cts.IsCancellationRequested) cts.Cancel();
    };

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

    await videoOutput.StartAsync();
    await router.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Close the window or press [Ctrl+C] to stop.");
    if (!Console.IsInputRedirected)
        Console.WriteLine("Press [Enter] to stop.");
    Console.WriteLine();

    var outputForStats = videoOutput;
    var channelForStats = videoChannel;
    var endpointForStats = ndiSink as IVideoEndpoint;
    var ndiSinkForStats = ndiSink;
    var audioChannelForStats = decoder.AudioChannels.Count > 0 ? decoder.AudioChannels[0] : null;
    var statsTask = Task.Run(async () =>
    {
        SDL3VideoOutput.DiagnosticsSnapshot? prevOutput = null;
        VideoEndpointDiagnosticsSnapshot? prevEndpoint = null;
        NDIAVSink.AvSyncSnapshot? prevAvSync = null;
        double expectedFps = srcFmt.FrameRate > 0 ? srcFmt.FrameRate : 30.0;

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
                long blackDelta = o1.BlackFrames - o0.BlackFrames;
                long bgraDelta = o1.BgraFrames - o0.BgraFrames;
                long rgbaDelta = o1.RgbaFrames - o0.RgbaFrames;
                long nv12Delta = o1.Nv12Frames - o0.Nv12Frames;
                long y420Delta = o1.Yuv420pFrames - o0.Yuv420pFrames;
                long y422P10Delta = o1.Yuv422p10Frames - o0.Yuv422p10Frames;
                long exDelta = o1.RenderExceptions - o0.RenderExceptions;
                string speedMark = presentDelta < Math.Max(1, (long)Math.Round(expectedFps * 0.75)) ? " slow" : "";
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
                    $"fps={presentDelta,3}/{expectedFps,5:F1} r={renderDelta,4} p={presentDelta,4} b={blackDelta,3} " +
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

                    // What the receiver "sees": at-last-video-submit, how far behind was audio?
                    long pairGapMs = a1.LastVideoSubmitMs - a1.AudioMsAtLastVideoSubmit;
                    string pairStr = a1.AudioMsAtLastVideoSubmit == 0 && a1.FirstAudioSubmitMs < 0
                        ? "audio-not-started"
                        : $"{pairGapMs,+5}ms (video@{a1.LastVideoSubmitMs}ms, audio@{a1.AudioMsAtLastVideoSubmit}ms)";

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
    if (!Console.IsInputRedirected)
        _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // ── 7. Stop ──────────────────────────────────────────────────────────

    Console.Write("\nStopping… ");
    cts.Cancel();
    try { await statsTask; } catch (OperationCanceledException) { }
    await videoOutput.StopAsync();
    if (ndiSink != null)
    {
        await ndiSink.StopAsync();
        ndiSink.Dispose();
    }
    ndiSender?.Dispose();
    ndiRuntime?.Dispose();
    Console.WriteLine("Done.");
}

}



#pragma warning restore 300
