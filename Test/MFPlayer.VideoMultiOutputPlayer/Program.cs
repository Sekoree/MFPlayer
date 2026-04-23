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
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.SDL3;

static NDIEndpointPreset ParseNDIPreset(string? text)
{
    var s = (text ?? string.Empty).Trim();
    if (s.Equals("safe", StringComparison.OrdinalIgnoreCase) || s.Equals("s", StringComparison.OrdinalIgnoreCase))
        return NDIEndpointPreset.Safe;
    if (s.Equals("lowlatency", StringComparison.OrdinalIgnoreCase) || s.Equals("low", StringComparison.OrdinalIgnoreCase) || s.Equals("l", StringComparison.OrdinalIgnoreCase))
        return NDIEndpointPreset.LowLatency;
    return NDIEndpointPreset.Balanced;
}

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

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║ MFPlayer — Video Multi-Output Player    ║");
Console.WriteLine("╚══════════════════════════════════════════╝\n");

ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

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
        EnableAudio = true,
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
    var audioChannel = decoder.AudioChannels.Count > 0 ? decoder.AudioChannels[0] : null;
    var srcFmt = videoChannel.SourceFormat;

    Console.WriteLine("OK");
    Console.WriteLine($"  Video: {srcFmt}");
    if (audioChannel != null)
        Console.WriteLine($"  Audio: {audioChannel.SourceFormat}");

    using var videoOutput = new SDL3VideoEndpoint();
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

    using var router = new AVRouter();
    var videoEpId = router.RegisterEndpoint(videoOutput);
    router.SetClock(videoOutput.Clock);

    var videoInputId = router.RegisterVideoInput(videoChannel);
    router.CreateRoute(videoInputId, videoEpId);

    NDIRuntime? ndiRuntime = null;
    NDISender? ndiSender = null;
    NDIAVEndpoint? ndiSink = null;

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
                var preset = ParseNDIPreset(Console.ReadLine());

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
                    if (audioChannel != null)
                    {
                        var srcAudio = audioChannel.SourceFormat;
                        ndiAudioFormat = new AudioFormat(48000, Math.Min(srcAudio.Channels, 2));
                        routeMap = BuildAudioRouteMap(srcAudio.Channels, ndiAudioFormat.Value.Channels);
                    }

                    ndiSink = new NDIAVEndpoint(
                        ndiSender,
                        videoOutput.OutputFormat,
                        audioTargetFormat: ndiAudioFormat,
                        preset: preset,
                        name: $"NDIAVEndpoint({senderName})",
                        videoPoolCount: 0,
                        videoMaxPendingFrames: 0,
                        audioFramesPerBuffer: 1024);
                    var ndiEpId = router.RegisterEndpoint(ndiSink);
                    router.CreateRoute(videoInputId, ndiEpId);
                    await ndiSink.StartAsync();

                    if (audioChannel != null && routeMap != null)
                    {
                        var audioInputId = router.RegisterAudioInput(audioChannel);
                        router.CreateRoute(audioInputId, ndiEpId, new AudioRouteOptions { ChannelMap = routeMap });
                    }

                    Console.WriteLine($"  NDI sink enabled: {senderName} ({preset})");
                    if (audioChannel != null)
                        Console.WriteLine("  NDI audio enabled from source audio track.");
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
    await router.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Targets: SDL3 leader" + (ndiSink != null ? " + NDI sink" : string.Empty));
    Console.WriteLine("Close window or press [Ctrl+C] to stop.");
    if (!Console.IsInputRedirected)
        Console.WriteLine("Press [Enter] to stop.");
    Console.WriteLine();

    var outputForStats = videoOutput;
    var channelForStats = videoChannel;
    Func<VideoEndpointDiagnosticsSnapshot?> getEndpointSnapshot = static () => null;
    if (ndiSink != null)
    {
        var sinkForStats = ndiSink;
        getEndpointSnapshot = () => sinkForStats.GetDiagnosticsSnapshot();
    }
    var statsTask = Task.Run(async () =>
    {
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
            string outDiag = $"  o(loop={os.LoopIterations} draw={os.PresentedFrames} black={os.BlackFrames} swap={os.SwapCalls} ex={os.RenderExceptions} glFail={os.GlMakeCurrentFailures})";
            var es = getEndpointSnapshot();
            string endpointDiag = es.HasValue
                ? $"  ep(pass={es.Value.PassthroughFrames} conv={es.Value.ConvertedFrames} drop={es.Value.DroppedFrames} q={es.Value.QueueDepth} qdrop={es.Value.QueueDrops})"
                : string.Empty;
            Console.WriteLine($"[vstats] clock={outputForStats.Clock.Position:mm\\:ss\\.fff} src={channelForStats.Position:mm\\:ss\\.fff}{outDiag}{endpointDiag}");
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
        ndiSink.Dispose();
    }

    ndiSender?.Dispose();
    ndiRuntime?.Dispose();

    Console.WriteLine("Done.");
}
