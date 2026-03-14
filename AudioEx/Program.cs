using FFmpeg.AutoGen;
using Ownaudio.Core;
using Ownaudio.Native;
using OwnaudioNET.Mixing;
using SDL3;
using Seko.OwnAudioSharp.Video;
using Seko.OwnAudioSharp.Video.Decoders;
using Seko.OwnAudioSharp.Video.Probing;
using Seko.OwnAudioSharp.Video.Sources;

namespace AudioEx;

internal static class Program
{
    public static void Main(string[] args)
    {
        string testFile = "/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4";
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
                    QueueCapacity = 6,
                    //EnableDriftCorrection = true,
                    //DriftCorrectionDeadZoneSeconds = 0.006,
                    //DriftCorrectionRate = 0.03,
                    //MaxCorrectionStepSeconds = 0.003,
                    DecoderOptions = new FFVideoDecoderOptions
                    {
                        EnableHardwareDecoding = true,
                        ThreadCount = GetSafeVideoThreadCount()
                    }
                }, streamIndex: videoStream.Index);
            }

            using var mixer = new AudioMixer(engine);
            mixer.AddSource(audioSource);
            audioSource.AttachToClock(mixer.MasterClock);
            videoSource?.AttachToClock(mixer.MasterClock);


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
                Console.Error.WriteLine("SDL_Init failed.");
                return;
            }

            if (!SDL.CreateWindowAndRenderer("MFPlayer", 1280, 720, SDL.WindowFlags.Resizable, out var window,
                    out var renderer))
            {
                SDL.Quit();
                return;
            }

            var info = videoSource.StreamInfo;
            Console.WriteLine($"Stream: {info}");

            var texture = SDL.CreateTexture(
                renderer,
                SDL.PixelFormat.ABGR8888,
                SDL.TextureAccess.Streaming,
                Math.Max(1, info.Width),
                Math.Max(1, info.Height));

            if (texture == nint.Zero)
            {
                SDL.DestroyRenderer(renderer);
                SDL.DestroyWindow(window);
                SDL.Quit();
                return;
            }

            // --- Frame sharing (zero-allocation fast path) ---
            var frameLock = new Lock();
            VideoFrame? latestFrame = null;
            var hasFrame = false;

            videoSource.FrameReadyFast += (frame, _) =>
            {
                VideoFrame? previous;
                lock (frameLock)
                {
                    previous = latestFrame;
                    latestFrame = frame.AddRef();
                    hasFrame = true;
                }

                previous?.Dispose();
            };

            // --- Stats ---
            var lastDecodedFrames = 0L;
            var lastPresentedFrames = 0L;
            var lastDroppedFrames = 0L;
            var lastStatsLogTime = DateTime.UtcNow;

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
                                    var wasRunning = mixer.IsRunning;
                                    mixer.Pause();
                                    audioSource.Seek(Math.Max(0.0, audioSource.Position - 1.0));
                                    videoSource?.Seek(Math.Max(0.0, videoSource.CurrentFramePtsSeconds - 1.0));
                                    mixer.MasterClock.SeekTo(audioSource.Position);
                                    if (wasRunning)
                                        mixer.Start();
                                    break;

                                case SDL.Keycode.Right:
                                    var wasRunning2 = mixer.IsRunning;
                                    mixer.Pause();
                                    audioSource.Seek(audioSource.Position + 1.0);
                                    videoSource?.Seek(videoSource.CurrentFramePtsSeconds + 1.0);
                                    mixer.MasterClock.SeekTo(audioSource.Position);
                                    if (wasRunning2)
                                        mixer.Start();
                                    break;

                                case SDL.Keycode.Escape:
                                    loop = false;
                                    break;
                            }

                            break;
                    }
                }

                // Drive frame advancement against the shared master clock.
                videoSource?.RequestNextFrame(out _);

                // Render current frame.
                SDL.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                SDL.RenderClear(renderer);

                VideoFrame? frameToRender = null;
                lock (frameLock)
                {
                    if (hasFrame)
                        frameToRender = latestFrame?.AddRef();
                }

                if (frameToRender != null)
                {
                    try
                    {
                        SDL.UpdateTexture(texture, nint.Zero, frameToRender.RgbaData, frameToRender.Stride);
                        SDL.GetRenderOutputSize(renderer, out var outputWidth, out var outputHeight);
                        var destination = GetAspectFitRect(outputWidth, outputHeight, frameToRender.Width,
                            frameToRender.Height);
                        SDL.RenderTexture(renderer, texture, nint.Zero, in destination);
                    }
                    finally
                    {
                        frameToRender.Dispose();
                    }
                }

                SDL.RenderPresent(renderer);

                // Stop loop when both streams are exhausted.
                if (videoSource.IsEndOfStream && audioSource.IsEndOfStream)
                {
                    Console.WriteLine("[Playback] End of stream reached.");
                    loop = false;
                }

                // Per-second stats.
                var now = DateTime.UtcNow;
                if ((now - lastStatsLogTime).TotalSeconds >= 1)
                {
                    var decodedFrames = videoSource.DecodedFrameCount;
                    var presentedFrames = videoSource.PresentedFrameCount;
                    var droppedFrames = videoSource.DroppedFrameCount;
                    var masterTs = mixer.MasterClock.CurrentTimestamp;
                    var audioTs = audioSource.Position;
                    var videoTs = videoSource.CurrentFramePtsSeconds;
                    var corrMs = videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                    var expectedVideo = masterTs - videoSource.StartOffset;
                    var expectedAudio = masterTs - audioSource.StartOffset;
                    var audioMasterDrift = (audioTs - expectedAudio) * 1000.0;
                    var videoMasterDrift = double.IsNaN(videoTs) ? double.NaN : (videoTs - expectedVideo) * 1000.0;
                    var avDrift = double.IsNaN(videoTs) ? double.NaN : (videoTs - audioTs) * 1000.0;

                    Console.Write(
                        $"[Video] target={videoSource.StreamInfo.FrameRate:F1} fps | " +
                        $"presented={presentedFrames - lastPresentedFrames} | " +
                        $"decoded={decodedFrames - lastDecodedFrames} | " +
                        $"dropped={droppedFrames - lastDroppedFrames} | " +
                        $"queue={videoSource.QueueDepth} | hw={videoSource.IsHardwareDecoding} | " +
                        $"master={masterTs:F3}s | audio={audioTs:F3}s | video={videoTs:F3}s | " +
                        $"a-m={audioMasterDrift:F1}ms | v-m={videoMasterDrift:F1}ms | " +
                        $"a-v={avDrift:F1}ms | corr={corrMs:F1}ms");

                    // Move cursor back to overwrite the last message.
                    Console.Write('\r');

                    lastDecodedFrames = decodedFrames;
                    lastPresentedFrames = presentedFrames;
                    lastDroppedFrames = droppedFrames;
                    lastStatsLogTime = now;
                }

                SDL.Delay(1);
            }

            // --- Cleanup ---
            lock (frameLock)
            {
                latestFrame?.Dispose();
                latestFrame = null;
            }

            SDL.DestroyTexture(texture);
            SDL.DestroyRenderer(renderer);
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
        var suggested = Math.Max(2, Environment.ProcessorCount / 4);
        return Math.Min(8, suggested);
    }

    private static SDL.FRect GetAspectFitRect(int outputWidth, int outputHeight, int sourceWidth, int sourceHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new SDL.FRect
            {
                X = 0,
                Y = 0,
                W = outputWidth,
                H = outputHeight
            };
        }

        var outputAspect = outputWidth / (float)outputHeight;
        var sourceAspect = sourceWidth / (float)sourceHeight;

        float width;
        float height;
        if (sourceAspect > outputAspect)
        {
            width = outputWidth;
            height = outputWidth / sourceAspect;
        }
        else
        {
            height = outputHeight;
            width = outputHeight * sourceAspect;
        }

        return new SDL.FRect
        {
            X = (outputWidth - width) * 0.5f,
            Y = (outputHeight - height) * 0.5f,
            W = width,
            H = height
        };
    }
}