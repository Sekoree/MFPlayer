using FFmpeg.AutoGen;
using SDL3;
using System.Collections.Concurrent;
using System.Runtime;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.SDL3;

namespace AudioEx;

internal static class Program
{
    private const double SeekStepSeconds = 5.0;

    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
            Console.Out.Flush();
        };

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex}");
            Console.Out.Flush();
        }
    }

    private static void Run(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        string testFile = "/run/media/seko/New Stuff/Other_Content/shootingstar_0611_1.mov";
        if (args.Length > 0)
            testFile = args[0];

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"FFmpeg version: {ffmpeg.av_version_info()}");

        if (!MediaStreamCatalog.TryGetFirstStream(testFile, MediaStreamKind.Video, out var videoStream))
        {
            Console.WriteLine("No video stream found.");
            return;
        }

        var decoderOptions = CreateDemoDecoderOptions(GetSafeVideoThreadCount(), videoStream.Index);
        using var videoDecoder = new FFVideoDecoder(testFile, decoderOptions);

        var streamInfo = videoDecoder.StreamInfo;
        var outputEngine = new VideoEngine(new VideoEngineConfig
        {
            FpsLimit = streamInfo.FrameRate > 0 ? streamInfo.FrameRate : null,
            PixelFormatPolicy = VideoEnginePixelFormatPolicy.Auto,
            DropRejectedFrames = false
        });

            try
            {
                Console.WriteLine($"Stream: {streamInfo}");

                var videoRenderer = new VideoSDL
                {
                    EnableHudOverlay = false,
                    KeepAspectRatio = true
                };

                try
                {
                    if (!videoRenderer.Initialize(1280, 720, "MFPlayer", out var glError))
                    {
                        Console.WriteLine($"VideoSDL init failed: {glError}");
                        return;
                    }

                    videoRenderer.Start();
                    outputEngine.AddOutput(videoRenderer);
                    videoRenderer.UpdateFormatInfo(videoDecoder.LastSourcePixelFormatName,
                        videoDecoder.LastOutputPixelFormatName,
                        streamInfo.FrameRate);
                    videoRenderer.UpdateHudDiagnostics(
                        queueDepth: 0,
                        uploadMsPerFrame: 0,
                        avDriftMs: 0,
                        isHardwareDecoding: videoDecoder.IsHardwareDecoding,
                        droppedFrames: 0);

                    Console.WriteLine("VideoSDL renderer initialised successfully.");

                    var exitRequested = 0;
                    var controlActions = new ConcurrentQueue<Action>();
                    var seekLock = new Lock();
                    var isPlaying = true;
                    var decodePrimePending = false;
                    var timelineSeconds = 0.0;

                    void PerformSeek(double positionSeconds)
                    {
                        if (!seekLock.TryEnter())
                            return;

                        try
                        {
                            var target = Math.Max(0, positionSeconds);
                            if (videoDecoder.TrySeek(TimeSpan.FromSeconds(target), out var seekError))
                            {
                                timelineSeconds = target;
                                decodePrimePending = true;
                            }
                            else
                            {
                                ConsolePrintLine($"[Playback] Seek failed: {seekError}");
                            }
                        }
                        finally
                        {
                            seekLock.Exit();
                        }
                    }

                    videoRenderer.KeyDown += key =>
                    {
                        switch (key)
                        {
                            case SDL.Keycode.Space:
                                controlActions.Enqueue(() =>
                                {
                                    isPlaying = !isPlaying;
                                    ConsolePrintLine(isPlaying ? "[Playback] Playing." : "[Playback] Paused.");
                                });
                                break;

                            case SDL.Keycode.Left:
                                controlActions.Enqueue(() => PerformSeek(timelineSeconds - SeekStepSeconds));
                                break;

                            case SDL.Keycode.Right:
                                controlActions.Enqueue(() => PerformSeek(timelineSeconds + SeekStepSeconds));
                                break;

                            case SDL.Keycode.Home:
                                controlActions.Enqueue(() => PerformSeek(0.0));
                                break;

                            case SDL.Keycode.End:
                                controlActions.Enqueue(() => PerformSeek(Math.Max(0, streamInfo.Duration.TotalSeconds - 0.001)));
                                break;

                            case SDL.Keycode.F11:
                                controlActions.Enqueue(() =>
                                {
                                    var window = videoRenderer.SdlWindowPtr;
                                    if (window == nint.Zero)
                                        return;

                                    var isFullscreen = (SDL.GetWindowFlags(window) & SDL.WindowFlags.Fullscreen) != 0;
                                    SDL.SetWindowFullscreen(window, !isFullscreen);
                                });
                                break;

                            case SDL.Keycode.H:
                                controlActions.Enqueue(() =>
                                {
                                    videoRenderer.EnableHudOverlay = !videoRenderer.EnableHudOverlay;
                                    ConsolePrintLine(videoRenderer.EnableHudOverlay
                                        ? "[Video] HUD enabled."
                                        : "[Video] HUD disabled.");
                                });
                                break;

                            case SDL.Keycode.Escape:
                                Interlocked.Exchange(ref exitRequested, 1);
                                break;
                        }
                    };


                    var lastDecodedFrames = 0L;
                    var lastSubmittedFrames = 0L;
                    var lastDroppedFrames = 0L;
                    var lastUploadedFrames = 0L;
                    var lastStatsLogTime = DateTime.UtcNow;
                    var decodedFrames = 0L;
                    var submittedFrames = 0L;
                    var droppedFrames = 0L;

                    var loop = true;
                    while (loop)
                    {
                        while (controlActions.TryDequeue(out var action))
                            action();

                        if (Interlocked.CompareExchange(ref exitRequested, 0, 0) != 0 || !videoRenderer.IsRunning)
                        {
                            loop = false;
                            continue;
                        }

                        if (isPlaying || decodePrimePending)
                        {
                            if (videoDecoder.TryDecodeNextFrame(out var frame, out var decodeError))
                            {
                                using (frame)
                                {
                                    timelineSeconds = frame.PtsSeconds;
                                    if (outputEngine.PushFrame(frame, timelineSeconds))
                                        submittedFrames++;
                                    else
                                        droppedFrames++;
                                }

                                decodedFrames++;
                                decodePrimePending = false;
                            }
                            else if (videoDecoder.IsEndOfStream)
                            {
                                ConsolePrintLine("[Playback] End of stream reached.");
                                loop = false;
                                continue;
                            }
                            else if (!string.IsNullOrWhiteSpace(decodeError))
                            {
                                ConsolePrintLine($"[Playback] Decode error: {decodeError}");
                                Thread.Sleep(5);
                            }
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastStatsLogTime).TotalSeconds >= 1)
                        {
                            videoRenderer.UpdateFormatInfo(videoDecoder.LastSourcePixelFormatName,
                                videoDecoder.LastOutputPixelFormatName,
                                streamInfo.FrameRate);

                            var masterTs = timelineSeconds;
                            var videoTs = timelineSeconds;
                            var diag = videoRenderer.GetDiagnosticsSnapshot();
                            var uploadedFrames = diag.FramesRendered;
                            var uploadedFrameDelta = uploadedFrames - lastUploadedFrames;

                            videoRenderer.UpdateHudDiagnostics(
                                queueDepth: 0,
                                uploadMsPerFrame: 0,
                                avDriftMs: 0,
                                isHardwareDecoding: videoDecoder.IsHardwareDecoding,
                                droppedFrames: droppedFrames - lastDroppedFrames);

                            ConsoleOverwriteLine(
                                $"[Render] {videoRenderer.RenderFps:F1}fps [Video] {videoRenderer.VideoFps:F1}fps {videoRenderer.PixelFormatInfo} " +
                                $"| sub={submittedFrames - lastSubmittedFrames}" +
                                $" up={uploadedFrameDelta}" +
                                $" dec={decodedFrames - lastDecodedFrames}" +
                                $" drop={droppedFrames - lastDroppedFrames}" +
                                $" q=0" +
                                $" hw={videoDecoder.IsHardwareDecoding}" +
                                $" m={masterTs:F3}s v={videoTs:F3}s" +
                                $" v-m=+0.0ms" +
                                $" corr=+0.0ms");

                            lastDecodedFrames = decodedFrames;
                            lastSubmittedFrames = submittedFrames;
                            lastDroppedFrames = droppedFrames;
                            lastUploadedFrames = uploadedFrames;
                            lastStatsLogTime = now;
                        }

                        Thread.Sleep(isPlaying ? 0 : 10);
                    }

                    videoRenderer.Stop();
                }
                finally
                {
                    outputEngine.RemoveOutput(videoRenderer);
                    videoRenderer.Dispose();
                }
            }
            finally
            {
                outputEngine.Dispose();
            }
    }

    private static int GetSafeVideoThreadCount()
    {
        var availableCores = Math.Max(1, Environment.ProcessorCount - 2);
        var suggested = Math.Max(2, availableCores / 3);
        return Math.Min(6, suggested);
    }

    private static FFVideoDecoderOptions CreateDemoDecoderOptions(int threadCount, int? streamIndex)
    {
        return new FFVideoDecoderOptions
        {
            PreferredStreamIndex = streamIndex,
            EnableHardwareDecoding = true,
            ThreadCount = threadCount,
            PreferredOutputPixelFormats =
            [
                VideoPixelFormat.Yuv422p10le,
                VideoPixelFormat.Yuv422p,
                VideoPixelFormat.Nv12,
                VideoPixelFormat.Yuv420p,
                VideoPixelFormat.Rgba32
            ],
            PreferSourcePixelFormatWhenSupported = true,
            PreferLowestConversionCost = true
        };
    }

    private static void ConsoleOverwriteLine(string message)
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch
        {
            width = 0;
        }

        var line = width > 1 ? message.PadRight(width - 1) : message;
        Console.Write("\r" + line);
    }

    private static void ConsolePrintLine(string message)
    {
        Console.WriteLine("\n" + message);
    }
}
