using FFmpeg.AutoGen;
using SDL3;
using System.Collections.Concurrent;
using System.Runtime;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;

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

        string testFile = "/run/media/sekoree/New Stuff/Other_Content/shootingstar_0611_1.mov";
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

        var videoSource = new FFVideoSource(testFile, new FFVideoSourceOptions
        {
            UseDedicatedDecodeThread = true,
            QueueCapacity = 30,
            LateDropThresholdSeconds = 0.050,
            LateDropFrameMultiplier = 3.0,
            MaxDropsPerRequest = 1,
            EnableDriftCorrection = true,
            DriftCorrectionDeadZoneSeconds = 0.006,
            DriftCorrectionRate = 0.03,
            MaxCorrectionStepSeconds = 0.003,
            DecoderOptions = CreateDemoDecoderOptions(GetSafeVideoThreadCount())
        }, streamIndex: videoStream.Index);

        try
        {
            var videoEngine = new VideoEngine();
            try
            {
                videoEngine.AddVideoSource(videoSource);

                var info = videoSource.StreamInfo;
                Console.WriteLine($"Stream: {info}");

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
                    videoEngine.AddVideoOutput(videoRenderer);
                    videoRenderer.UpdateFormatInfo(videoSource.DecoderSourcePixelFormatName,
                        videoSource.DecoderOutputPixelFormatName,
                        info.FrameRate);
                    videoRenderer.UpdateHudDiagnostics(
                        queueDepth: 0,
                        uploadMsPerFrame: 0,
                        avDriftMs: 0,
                        isHardwareDecoding: videoSource.IsHardwareDecoding,
                        droppedFrames: 0);

                    Console.WriteLine("VideoSDL renderer initialised successfully.");

                    var exitRequested = 0;
                    var controlActions = new ConcurrentQueue<Action>();
                    var seekLock = new Lock();

                    void PerformSeek(double positionSeconds)
                    {
                        if (!seekLock.TryEnter())
                            return;

                        try
                        {
                            videoEngine.Seek(positionSeconds, safeSeek: true);
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
                                    if (videoEngine.IsRunning)
                                    {
                                        videoEngine.Pause();
                                        ConsolePrintLine("[Playback] Paused.");
                                    }
                                    else
                                    {
                                        videoEngine.Start();
                                        ConsolePrintLine("[Playback] Playing.");
                                    }
                                });
                                break;

                            case SDL.Keycode.Left:
                                controlActions.Enqueue(() => PerformSeek(Math.Max(0.0, videoEngine.Position - SeekStepSeconds)));
                                break;

                            case SDL.Keycode.Right:
                                controlActions.Enqueue(() => PerformSeek(videoEngine.Position + SeekStepSeconds));
                                break;

                            case SDL.Keycode.Home:
                                controlActions.Enqueue(() => PerformSeek(0.0));
                                break;

                            case SDL.Keycode.End:
                                controlActions.Enqueue(() =>
                                {
                                    if (videoSource.SeekToEnd())
                                        PerformSeek(Math.Max(0.0, videoSource.Position + videoSource.StartOffset));
                                });
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
                    var lastPresentedFrames = 0L;
                    var lastDroppedFrames = 0L;
                    var lastUploadedFrames = 0L;
                    var lastStatsLogTime = DateTime.UtcNow;

                    videoEngine.Start();

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

                        if (videoSource.IsEndOfStream)
                        {
                            ConsolePrintLine("[Playback] End of stream reached.");
                            loop = false;
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastStatsLogTime).TotalSeconds >= 1)
                        {
                            videoRenderer.UpdateFormatInfo(videoSource.DecoderSourcePixelFormatName,
                                videoSource.DecoderOutputPixelFormatName,
                                videoSource.StreamInfo.FrameRate);

                            var decodedFrames = videoSource.DecodedFrameCount;
                            var presentedFrames = videoSource.PresentedFrameCount;
                            var droppedFrames = videoSource.DroppedFrameCount;
                            var masterTs = videoEngine.Position;
                            var videoTs = videoSource.CurrentFramePtsSeconds;
                            var videoMasterDriftMs = double.IsNaN(videoTs)
                                ? double.NaN
                                : (videoTs - (masterTs - videoSource.StartOffset)) * 1000.0;
                            var correctionOffsetMs = videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                            var diag = videoRenderer.GetDiagnosticsSnapshot();
                            var uploadedFrames = diag.FramesRendered;
                            var uploadedFrameDelta = uploadedFrames - lastUploadedFrames;

                            videoRenderer.UpdateHudDiagnostics(
                                queueDepth: videoSource.QueueDepth,
                                uploadMsPerFrame: 0,
                                avDriftMs: videoMasterDriftMs,
                                isHardwareDecoding: videoSource.IsHardwareDecoding,
                                droppedFrames: droppedFrames - lastDroppedFrames);

                            ConsoleOverwriteLine(
                                $"[Render] {videoRenderer.RenderFps:F1}fps [Video] {videoRenderer.VideoFps:F1}fps {videoRenderer.PixelFormatInfo} " +
                                $"| pres={presentedFrames - lastPresentedFrames}" +
                                $" up={uploadedFrameDelta}" +
                                $" dec={decodedFrames - lastDecodedFrames}" +
                                $" drop={droppedFrames - lastDroppedFrames}" +
                                $" q={videoSource.QueueDepth}" +
                                $" hw={videoSource.IsHardwareDecoding}" +
                                $" m={masterTs:F3}s v={videoTs:F3}s" +
                                $" v-m={videoMasterDriftMs:+0.0;-0.0}ms" +
                                $" corr={correctionOffsetMs:+0.0;-0.0}ms");

                            lastDecodedFrames = decodedFrames;
                            lastPresentedFrames = presentedFrames;
                            lastDroppedFrames = droppedFrames;
                            lastUploadedFrames = uploadedFrames;
                            lastStatsLogTime = now;
                        }

                        Thread.Sleep(videoEngine.IsRunning ? 1 : 10);
                    }

                    videoRenderer.Stop();
                }
                finally
                {
                    videoRenderer.Dispose();
                }
            }
            finally
            {
                videoEngine.Dispose();
            }
        }
        finally
        {
            videoSource.Dispose();
        }
    }

    private static int GetSafeVideoThreadCount()
    {
        var availableCores = Math.Max(1, Environment.ProcessorCount - 2);
        var suggested = Math.Max(2, availableCores / 3);
        return Math.Min(6, suggested);
    }

    private static FFVideoDecoderOptions CreateDemoDecoderOptions(int threadCount)
    {
        return new FFVideoDecoderOptions
        {
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
