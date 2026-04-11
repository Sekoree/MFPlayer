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
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.SDL3;

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

static NdiEndpointPreset ParseNdiPreset(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("safe", StringComparison.OrdinalIgnoreCase) || s.Equals("s", StringComparison.OrdinalIgnoreCase))
        return NdiEndpointPreset.Safe;
    if (s.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) || s.Equals("low", StringComparison.OrdinalIgnoreCase) || s.Equals("l", StringComparison.OrdinalIgnoreCase))
        return NdiEndpointPreset.LowLatency;
    return NdiEndpointPreset.Balanced;
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

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Video Player   ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = "/lib";

// ── 1. Enter file path ───────────────────────────────────────────────────────

Console.Write("Video file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 2. Open decoder ──────────────────────────────────────────────────────────

Console.Write("Opening decoder… ");
FFmpegDecoder decoder;
try
{
    decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
    {
        EnableAudio = false,
        EnableVideo = true
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
    var suggestedHint = videoChannel as IVideoColorMatrixHint;
    var suggestedMatrix = suggestedHint?.SuggestedYuvColorMatrix ?? YuvColorMatrix.Auto;
    var suggestedRange = suggestedHint?.SuggestedYuvColorRange ?? YuvColorRange.Auto;

    Console.WriteLine("OK");
    Console.WriteLine($"  Video: {srcFmt}");

    var initialWindow = FitWithin(srcFmt.Width, srcFmt.Height, maxWidth: 1920, maxHeight: 1080);
    Console.WriteLine($"  Window: {initialWindow.Width}x{initialWindow.Height} (fit)");

    // ── 3. Open video output ─────────────────────────────────────────────

    Console.Write("Creating SDL3 video output… ");
    using var videoOutput = new SDL3VideoOutput();
    var selectedRange = suggestedRange;
    var selectedMatrix = suggestedMatrix;
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

    using var avMixer = new AVMixer(new AudioMixer(new AudioFormat(48000, 2)), videoOutput.Mixer, ownsAudio: true, ownsVideo: false)
    {
        MasterPolicy = IAVMixer.ClockMasterPolicy.Video
    };
    avMixer.AddVideoChannel(videoChannel);
    avMixer.SetActiveVideoChannel(videoChannel.Id);

    // Optional: route the same active channel to an NDI video sink.
    NDIRuntime? ndiRuntime = null;
    NDISender? ndiSender = null;
    NDIVideoSink? ndiSink = null;

    Console.Write("Enable NDI video sink? [y/N]: ");
    bool enableNdi = (Console.ReadLine() ?? string.Empty).Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
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

                Console.Write("NDI preset [Safe/Balanced/LowLatency] (default Balanced): ");
                var preset = ParseNdiPreset(Console.ReadLine());

                int sret = NDISender.Create(out ndiSender, senderName, clockVideo: false, clockAudio: false);
                if (sret != 0 || ndiSender == null)
                {
                    Console.WriteLine($"  NDI disabled: sender creation failed ({sret}).");
                    ndiRuntime.Dispose();
                    ndiRuntime = null;
                }
                else
                {
                    ndiSink = new NDIVideoSink(
                        ndiSender,
                        videoOutput.OutputFormat,
                        poolCount: 0,
                        maxPendingFrames: 0,
                        preset: preset,
                        name: $"NDIVideoSink({senderName})");
                    avMixer.RegisterVideoSink(ndiSink);
                    avMixer.RouteVideoChannelToSink(videoChannel.Id, ndiSink);
                    await ndiSink.StartAsync();
                    Console.WriteLine($"  NDI sink enabled: {senderName} ({preset})");
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
    await videoOutput.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Close the window or press [Ctrl+C] to stop.");
    if (!Console.IsInputRedirected)
        Console.WriteLine("Press [Enter] to stop.");
    Console.WriteLine();

    var mixer = videoOutput.Mixer as VideoMixer;
    var outputForStats = videoOutput;
    var channelForStats = videoChannel;
    var mixerForStats = mixer;
    var endpointForStats = ndiSink as IVideoSink;
    var statsTask = Task.Run(async () =>
    {
        VideoMixer.DiagnosticsSnapshot? prevMixer = null;
        SDL3VideoOutput.DiagnosticsSnapshot? prevOutput = null;
        VideoEndpointDiagnosticsSnapshot? prevEndpoint = null;
        BasicPixelFormatConverter.DiagnosticsSnapshot? prevConv = null;
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

            var ms = mixerForStats?.GetDiagnosticsSnapshot();
            var os = outputForStats.GetDiagnosticsSnapshot();
            var es = endpointForStats?.GetDiagnosticsSnapshot();
            var cs = BasicPixelFormatConverter.GetDiagnosticsSnapshot();

            if (ms.HasValue && prevMixer.HasValue && prevOutput.HasValue)
            {
                var m0 = prevMixer.Value;
                var m1 = ms.Value;
                var o0 = prevOutput.Value;
                var o1 = os;

                long renderDelta = o1.LoopIterations - o0.LoopIterations;
                long presentDelta = o1.PresentedFrames - o0.PresentedFrames;
                long blackDelta = o1.BlackFrames - o0.BlackFrames;
                long bgraDelta = o1.BgraFrames - o0.BgraFrames;
                long rgbaDelta = o1.RgbaFrames - o0.RgbaFrames;
                long nv12Delta = o1.Nv12Frames - o0.Nv12Frames;
                long y420Delta = o1.Yuv420pFrames - o0.Yuv420pFrames;
                long y422p10Delta = o1.Yuv422p10Frames - o0.Yuv422p10Frames;
                long exDelta = o1.RenderExceptions - o0.RenderExceptions;
                long holdDelta = m1.Held - m0.Held;
                long dropDelta = m1.Dropped - m0.Dropped;
                long pullDelta = m1.PullHits - m0.PullHits;
                long pullAttemptDelta = m1.PullAttempts - m0.PullAttempts;
                long samePassDelta = m1.SameFormatPassthrough - m0.SameFormatPassthrough;
                long rawPassDelta = m1.RawMarkerPassthrough - m0.RawMarkerPassthrough;
                long convDelta = m1.Converted - m0.Converted;
                long sinkFmtHitDelta = m1.SinkFormatHits - m0.SinkFormatHits;
                long sinkFmtMissDelta = m1.SinkFormatMisses - m0.SinkFormatMisses;
                long convLibYuvAttemptsDelta = prevConv.HasValue ? cs.LibYuvAttempts - prevConv.Value.LibYuvAttempts : 0;
                long convLibYuvSuccessDelta = prevConv.HasValue ? cs.LibYuvSuccesses - prevConv.Value.LibYuvSuccesses : 0;
                long convFallbackDelta = prevConv.HasValue ? cs.ManagedFallbacks - prevConv.Value.ManagedFallbacks : 0;

                string speedMark = presentDelta < Math.Max(1, (long)Math.Round(expectedFps * 0.75)) ? " slow" : "";
                string dropMark = dropDelta > 0 ? " drop" : "";
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
                    $"held={holdDelta,4} drop={dropDelta,3} pull={pullDelta,3}/{pullAttemptDelta,3} route={(samePassDelta + rawPassDelta),3}/{convDelta,3} (same/raw={samePassDelta,3}/{rawPassDelta,3}) sinkFmt={sinkFmtHitDelta,3}/{sinkFmtMissDelta,3} cvt={convLibYuvSuccessDelta,3}/{convLibYuvAttemptsDelta,3}/{convFallbackDelta,3} ex={exDelta,2}{speedMark}{dropMark}{exMark}");
                Console.WriteLine($"         fmt=bgra:{bgraDelta,3} rgba:{rgbaDelta,3} nv12:{nv12Delta,3} y420:{y420Delta,3} y422p10:{y422p10Delta,3}");
                Console.WriteLine($"         {endpointText}");
            }

            prevMixer = ms;
            prevOutput = os;
            prevEndpoint = es;
            prevConv = cs;
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
        avMixer.UnregisterVideoSink(ndiSink);
        ndiSink.Dispose();
    }
    ndiSender?.Dispose();
    ndiRuntime?.Dispose();
    Console.WriteLine("Done.");
}
