using FFmpeg.AutoGen;
using Ownaudio.Core;
using Ownaudio.Native;
using SDL3;
using System.Diagnostics;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.Sources;

namespace AudioEx;

internal static class Program
{
    private const double SeekStepSeconds = 5.0;

    public static void Main(string[] args)
    {
        // Capture unhandled exceptions from background threads (e.g. the FFmpeg decode
        // thread) which would otherwise kill the process silently.
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
        //string testFile = "/home/sekoree/Videos/Mesmerizer German Cover CALYTRIX Chiyo.mp4";
        string testFile = "/home/sekoree/Videos/おねがいダーリン_0611.mov";
        if (args.Length > 0)
            testFile = args[0];

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"FFmpeg version: {ffmpeg.av_version_info()}");

        // --- Audio engine setup ---
        using var engine = new NativeAudioEngine();
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
        //INFO: The clock seems to be hard tied to the soundcard quantum, so 512 buffer on the Soundcard level is a minimum for 60fps
        engine.Initialize(config);
        engine.Start();

        // --- Source setup ---
        using var audioSource = new FFAudioSource(testFile, config);
        FFVideoSource? videoSource = null;

        try
        {
            if (MediaStreamCatalog.TryGetFirstStream(testFile, MediaStreamKind.Video, out var videoStream))
            {
                videoSource = new FFVideoSource(testFile, new FFVideoSourceOptions
                {
                    UseDedicatedDecodeThread = true,
                    QueueCapacity = 30,
                    DecoderOptions = CreateDemoDecoderOptions(GetSafeVideoThreadCount())
                }, streamIndex: videoStream.Index);
            }

            using var mixer = new AVMixer(engine);
            mixer.AddAudioSource(audioSource);
            audioSource.AttachToClock(mixer.MasterClock);
            if (videoSource != null)
            {
                mixer.AddVideoSource(videoSource);
                videoSource.AttachToClock(mixer.MasterClock);
            }


            // --- Playback start ---
            mixer.Start();
            audioSource.Play();

            if (videoSource == null)
            {
                Console.WriteLine("No video stream found (or no attached cover art). Playing audio only.");
                while (!audioSource.IsEndOfStream)
                    Thread.Sleep(10);

                Console.WriteLine("[Playback] Audio end of stream reached.");
                return;
            }

            // --- SDL window ---
            if (!SDL.Init(SDL.InitFlags.Video))
            {
                Console.WriteLine($"SDL_Init failed: {SDL.GetError()}");
                return;
            }

            SDL.GLResetAttributes();
            SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
            SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
            SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
            SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

            var window = SDL.CreateWindow("MFPlayer", 1280, 720, SDL.WindowFlags.Resizable | SDL.WindowFlags.OpenGL);
            if (window == nint.Zero)
            {
                Console.WriteLine($"SDL_CreateWindow failed: {SDL.GetError()}");
                SDL.Quit();
                return;
            }

            var glContext = SDL.GLCreateContext(window);
            if (glContext == nint.Zero || !SDL.GLMakeCurrent(window, glContext))
            {
                Console.WriteLine($"GL context creation/make-current failed: {SDL.GetError()}");
                if (glContext != nint.Zero)
                    SDL.GLDestroyContext(glContext);
                SDL.DestroyWindow(window);
                SDL.Quit();
                return;
            }

            SDL.GLSetSwapInterval(1);

            var info = videoSource.StreamInfo;
            Console.WriteLine($"Stream: {info}");

            using var videoRenderer = new SdlVideoGlRenderer();
            if (!videoRenderer.Initialize(out var glError))
            {
                Console.WriteLine($"OpenGL init failed: {glError}");
                SDL.GLDestroyContext(glContext);
                SDL.DestroyWindow(window);
                SDL.Quit();
                return;
            }

            // Update renderer with initial format info
            videoRenderer.UpdateFormatInfo(videoSource.DecoderSourcePixelFormatName, 
                                           videoSource.DecoderOutputPixelFormatName,
                                           info.FrameRate);
            videoRenderer.UpdateHudDiagnostics(queueDepth: 0, uploadMsPerFrame: 0, avDriftMs: 0, isHardwareDecoding: videoSource.IsHardwareDecoding, droppedFrames: 0);

            Console.WriteLine("OpenGL renderer initialised successfully.");

            // --- Frame sharing (zero-allocation fast path) ---
            var frameLock = new Lock();
            VideoFrame? latestFrame = null;
            var hasFrame = false;
            var latestFrameVersion = 0L;
            var lastUploadedFrameVersion = -1L;
            var uploadedVideoFrameCounter = 0L;
            var uploadedVideoTicks = 0L;

            videoSource.FrameReadyFast += (frame, _) =>
            {
                VideoFrame? previous;
                lock (frameLock)
                {
                    previous = latestFrame;
                    latestFrame = frame.AddRef();
                    hasFrame = true;
                    latestFrameVersion++;
                }

                previous?.Dispose();
            };

            // --- Stats ---
            var lastDecodedFrames = 0L;
            var lastPresentedFrames = 0L;
            var lastDroppedFrames = 0L;
            var lastUploadedFrames = 0L;
            var lastUploadedTicks = 0L;
            var lastStatsLogTime = DateTime.UtcNow;
            var lastAllocatedBytes = GC.GetTotalAllocatedBytes(false);
            var lastGen0Count = GC.CollectionCount(0);
            var lastGen1Count = GC.CollectionCount(1);
            var lastGen2Count = GC.CollectionCount(2);

            // --- Seek helper ---
            var seekLock = new Lock();

            void PerformSeek(double positionSeconds)
            {
                if (!seekLock.TryEnter())
                    return;

                try
                {
                    lock (frameLock)
                    {
                        latestFrame?.Dispose();
                        latestFrame = null;
                        hasFrame = false;
                        latestFrameVersion = 0;
                        lastUploadedFrameVersion = -1;
                    }

                    mixer.Seek(positionSeconds, safeSeek: true);
                }
                finally
                {
                    seekLock.Exit();
                }
            }
            
            // --- Main loop ---
            var loop = true;
            while (loop)
            {
                while (SDL.PollEvent(out var e))
                {
                    switch ((SDL.EventType)e.Type)
                    {
                        case SDL.EventType.Quit:
                            loop = false;
                            break;

                        case SDL.EventType.KeyDown:
                            switch (e.Key.Key)
                            {
                                case SDL.Keycode.Space:
                                    if (mixer.IsRunning)
                                        mixer.Pause();
                                    else
                                        mixer.Start();
                                    break;

                                case SDL.Keycode.Left:
                                    PerformSeek(Math.Max(0.0, audioSource.Position - SeekStepSeconds));
                                    break;

                                case SDL.Keycode.Right:
                                    PerformSeek(audioSource.Position + SeekStepSeconds);
                                    break;

                                case SDL.Keycode.Escape:
                                    loop = false;
                                    break;
                                case SDL.Keycode.F11:
                                    var isFullscreen = (SDL.GetWindowFlags(window) & SDL.WindowFlags.Fullscreen) != 0;
                                    SDL.SetWindowFullscreen(window, !isFullscreen);
                                    break;
                            }

                            break;
                    }
                }

                // Drive frame advancement against the shared master clock.
                videoSource.RequestNextFrame(out _);

                SDL.GetWindowSizeInPixels(window, out var outputWidth, out var outputHeight);
                outputWidth = Math.Max(1, outputWidth);
                outputHeight = Math.Max(1, outputHeight);

                VideoFrame? frameToRender = null;
                var frameVersion = -1L;
                lock (frameLock)
                {
                    if (hasFrame)
                    {
                        frameToRender = latestFrame?.AddRef();
                        frameVersion = latestFrameVersion;
                    }
                }

                if (frameToRender != null)
                {
                    try
                    {
                        if (frameVersion != lastUploadedFrameVersion)
                        {
                            var uploadStart = Stopwatch.GetTimestamp();
                            if (videoRenderer.RenderFrame(frameToRender, outputWidth, outputHeight))
                            {
                                lastUploadedFrameVersion = frameVersion;
                                uploadedVideoFrameCounter++;
                                uploadedVideoTicks += Stopwatch.GetTimestamp() - uploadStart;
                            }
                        }
                        else
                        {
                            videoRenderer.RenderLastFrame(outputWidth, outputHeight);
                        }
                    }
                    finally
                    {
                        frameToRender.Dispose();
                    }
                }
                else
                {
                    videoRenderer.RenderLastFrame(outputWidth, outputHeight);
                }

                SDL.GLSwapWindow(window);

                // Stop loop when both streams are exhausted.
                if (videoSource.IsEndOfStream && audioSource.IsEndOfStream)
                {
                    ConsolePrintLine("[Playback] End of stream reached.");
                    loop = false;
                }

                // Per-second stats.
                var now = DateTime.UtcNow;
                if ((now - lastStatsLogTime).TotalSeconds >= 1)
                {
                    // Update renderer with latest format info
                    videoRenderer.UpdateFormatInfo(videoSource.DecoderSourcePixelFormatName,
                                                   videoSource.DecoderOutputPixelFormatName,
                                                   videoSource.StreamInfo.FrameRate);

                    var decodedFrames = videoSource.DecodedFrameCount;
                    var presentedFrames = videoSource.PresentedFrameCount;
                    var droppedFrames = videoSource.DroppedFrameCount;
                    var audioTs = audioSource.Position;
                    var videoTs = videoSource.CurrentFramePtsSeconds;
                    var avDrift = double.IsNaN(videoTs) ? double.NaN : (videoTs - audioTs) * 1000.0;
                    var totalAllocatedBytes = GC.GetTotalAllocatedBytes(false);
                    var managedHeapBytes = GC.GetTotalMemory(false);
                    var gen0Delta = GC.CollectionCount(0) - lastGen0Count;
                    var gen1Delta = GC.CollectionCount(1) - lastGen1Count;
                    var gen2Delta = GC.CollectionCount(2) - lastGen2Count;
                    var uploadedFrames = uploadedVideoFrameCounter;
                    var uploadedFrameDelta = uploadedFrames - lastUploadedFrames;
                    var uploadedTicksDelta = uploadedVideoTicks - lastUploadedTicks;
                    var uploadMs = uploadedTicksDelta * 1000.0 / Stopwatch.Frequency;
                    var uploadMsPerFrame = uploadedFrameDelta > 0 ? uploadMs / uploadedFrameDelta : 0;

                    videoRenderer.UpdateHudDiagnostics(
                        queueDepth: videoSource.QueueDepth,
                        uploadMsPerFrame: uploadMsPerFrame,
                        avDriftMs: avDrift,
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
                        $" upms={uploadMsPerFrame:F2}" +
                        $" a-v={avDrift:F1}ms" +
                        $" heap={FormatBytes(managedHeapBytes)}");

                    lastDecodedFrames = decodedFrames;
                    lastPresentedFrames = presentedFrames;
                    lastDroppedFrames = droppedFrames;
                    lastUploadedFrames = uploadedFrames;
                    lastUploadedTicks = uploadedVideoTicks;
                    lastStatsLogTime = now;
                    lastAllocatedBytes = totalAllocatedBytes;
                    lastGen0Count += gen0Delta;
                    lastGen1Count += gen1Delta;
                    lastGen2Count += gen2Delta;
                }

                SDL.Delay(1);
            }

            // --- Cleanup ---
            lock (frameLock)
            {
                latestFrame?.Dispose();
                latestFrame = null;
            }

            SDL.GLMakeCurrent(window, nint.Zero);
            SDL.GLDestroyContext(glContext);
            SDL.DestroyWindow(window);
            SDL.Quit();
        }
        finally
        {
            videoSource?.Dispose();
        }
    }

    private static int GetSafeVideoThreadCount()
    {
        var suggested = Math.Max(4, Environment.ProcessorCount / 2);
        return Math.Min(16, suggested);
    }

    private static FFVideoDecoderOptions CreateDemoDecoderOptions(int threadCount)
    {
        return new FFVideoDecoderOptions
        {
            EnableHardwareDecoding = true,
            ThreadCount = threadCount,
            PreferredOutputPixelFormats =
            [
                VideoPixelFormat.Yuv422p10le,  // ProRes 422 HQ native
                VideoPixelFormat.Yuv422p,      // ProRes 422 native
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


    private static string FormatBytes(double bytes)
    {
        var value = Math.Max(0, bytes);
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        if (value >= gb)
            return $"{value / gb:F2} GiB";

        if (value >= mb)
            return $"{value / mb:F2} MiB";

        if (value >= kb)
            return $"{value / kb:F2} KiB";

        return $"{value:F0} B";
    }

}
