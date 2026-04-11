// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.VideoMultiOutputPlayer
//   1. Enter a video file path
//   2. Opens SDL3 video output (leader target)
//   3. Optionally enables NDI video sink (secondary target)
//   4. One input channel routed to both targets (no blending)
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

static NdiEndpointPreset ParseNdiPreset(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("safe", StringComparison.OrdinalIgnoreCase) || s.Equals("s", StringComparison.OrdinalIgnoreCase))
        return NdiEndpointPreset.Safe;
    if (s.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) || s.Equals("low", StringComparison.OrdinalIgnoreCase) || s.Equals("l", StringComparison.OrdinalIgnoreCase))
        return NdiEndpointPreset.LowLatency;
    return NdiEndpointPreset.Balanced;
}

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║ MFPlayer — Video Multi-Output Player    ║");
Console.WriteLine("╚══════════════════════════════════════════╝\n");

ffmpeg.RootPath = "/lib";

Console.Write("Video file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');
if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

Console.Write("Opening decoder... ");
FFmpegDecoder decoder;
try
{
    decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
    {
        EnableAudio = false,
        EnableVideo = true,
        // null = auto-detect: decoder outputs frames in the source's native pixel format.
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
    var srcFmt = videoChannel.SourceFormat;

    Console.WriteLine("OK");
    Console.WriteLine($"  Video: {srcFmt}");

    using var videoOutput = new SDL3VideoOutput();
    Console.Write("Opening SDL3 video output... ");
    try
    {
        videoOutput.Open(
            "MFPlayer - Video Multi-Output",
            srcFmt.Width > 0 ? srcFmt.Width : 1280,
            srcFmt.Height > 0 ? srcFmt.Height : 720,
            srcFmt);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    using var avMixer = new AVMixer(new AudioMixer(new AudioFormat(48000, 2)), videoOutput.Mixer, ownsAudio: true, ownsVideo: false)
    {
        MasterPolicy = IAVMixer.ClockMasterPolicy.Video
    };
    avMixer.AddVideoChannel(videoChannel);
    avMixer.SetActiveVideoChannel(videoChannel.Id);

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

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    videoOutput.WindowClosed += () =>
    {
        Console.WriteLine("\n[Window closed]");
        if (!cts.IsCancellationRequested) cts.Cancel();
    };

    decoder.Start();
    await videoOutput.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Targets: SDL3 leader" + (ndiSink != null ? " + NDI sink" : string.Empty));
    Console.WriteLine("Close window or press [Ctrl+C] to stop.");
    if (!Console.IsInputRedirected)
        Console.WriteLine("Press [Enter] to stop.");
    Console.WriteLine();

    var mixer = videoOutput.Mixer as VideoMixer;
    var outputForStats = videoOutput;
    var channelForStats = videoChannel;
    var mixerForStats = mixer;
    var statsTask = Task.Run(async () =>
    {
        BasicPixelFormatConverter.DiagnosticsSnapshot? prevConv = null;
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

            string diag = mixerForStats == null
                ? string.Empty
                : $"  held={mixerForStats.HeldFrameCount} drop={mixerForStats.DroppedStaleFrameCount} fallback={mixerForStats.FallbackConversionCount}";
            var ms = mixerForStats?.GetDiagnosticsSnapshot();
            var os = outputForStats.GetDiagnosticsSnapshot();
            var cs = BasicPixelFormatConverter.GetDiagnosticsSnapshot();
            string mixerDiag = ms.HasValue
                ? $"  m(present={ms.Value.PresentCalls} leader={ms.Value.LeaderPresented}/{ms.Value.LeaderReturnedNull} pull={ms.Value.PullHits}/{ms.Value.PullAttempts})"
                : string.Empty;
            string outDiag = $"  o(loop={os.LoopIterations} draw={os.PresentedFrames} black={os.BlackFrames} swap={os.SwapCalls} ex={os.RenderExceptions} glFail={os.GlMakeCurrentFailures})";
            string convDiag = prevConv.HasValue
                ? $"  cvt({cs.LibYuvSuccesses - prevConv.Value.LibYuvSuccesses}/{cs.LibYuvAttempts - prevConv.Value.LibYuvAttempts}/{cs.ManagedFallbacks - prevConv.Value.ManagedFallbacks})"
                : "";
            var es = ndiSink?.GetDiagnosticsSnapshot();
            string endpointDiag = es.HasValue
                ? $"  ep(pass={es.Value.PassthroughFrames} conv={es.Value.ConvertedFrames} drop={es.Value.DroppedFrames} q={es.Value.QueueDepth} qdrop={es.Value.QueueDrops})"
                : string.Empty;
            Console.WriteLine($"[vstats] clock={outputForStats.Clock.Position:mm\\:ss\\.fff} src={channelForStats.Position:mm\\:ss\\.fff}{diag}{mixerDiag}{outDiag}{convDiag}{endpointDiag}");
            prevConv = cs;
        }
    }, cts.Token);

    if (!Console.IsInputRedirected)
        _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    Console.Write("\nStopping... ");
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

