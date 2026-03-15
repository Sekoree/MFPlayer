using FFmpeg.AutoGen;
using Ownaudio.Core;
using Ownaudio.Native;
using SDL3;
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
                    DecoderOptions = new FFVideoDecoderOptions
                    {
                        EnableHardwareDecoding = true,
                        ThreadCount = GetSafeVideoThreadCount()
                    }
                }, streamIndex: videoStream.Index);
            }

            using var mixer = new AVMixer(engine);
            mixer.AddAudioSource(audioSource);
            if (videoSource != null)
                mixer.AddVideoSource(videoSource);


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
            
            // Enable vsync.
            SDL.SetWindowSurfaceVSync(window, 1);
            SDL.SetRenderVSync(renderer, 1);

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
            var presentedVideoFrameCounter = 0;

            videoSource.FrameReadyFast += (frame, _) =>
            {
                VideoFrame? previous;
                lock (frameLock)
                {
                    previous = latestFrame;
                    latestFrame = frame.AddRef();
                    hasFrame = true;
                }

                Interlocked.Increment(ref presentedVideoFrameCounter);
                previous?.Dispose();
            };

            // --- Stats ---
            var lastDecodedFrames = 0L;
            var lastPresentedFrames = 0L;
            var lastDroppedFrames = 0L;
            var lastStatsLogTime = DateTime.UtcNow;
            var fpsWindowStart = DateTime.UtcNow;
            var renderLoopFrameCounter = 0;
            var renderLoopFps = 0.0;
            var presentedVideoFps = 0.0;
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

                renderLoopFrameCounter++;
                var fpsNow = DateTime.UtcNow;
                var fpsElapsed = (fpsNow - fpsWindowStart).TotalSeconds;
                if (fpsElapsed >= 0.5)
                {
                    renderLoopFps = renderLoopFrameCounter / fpsElapsed;
                    renderLoopFrameCounter = 0;
                    var presentedVideoFrames = Interlocked.Exchange(ref presentedVideoFrameCounter, 0);
                    presentedVideoFps = presentedVideoFrames / fpsElapsed;
                    fpsWindowStart = fpsNow;
                }

                // Render current frame.
                SDL.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                SDL.RenderClear(renderer);
                SDL.GetRenderOutputSize(renderer, out var outputWidth, out var outputHeight);

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
                        var destination = GetAspectFitRect(outputWidth, outputHeight, frameToRender.Width,
                            frameToRender.Height);
                        SDL.RenderTexture(renderer, texture, nint.Zero, in destination);
                    }
                    finally
                    {
                        frameToRender.Dispose();
                    }
                }

                DrawFpsOverlay(renderer, outputWidth, renderLoopFps, presentedVideoFps);

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
                    var elapsedSeconds = Math.Max(0.001, (now - lastStatsLogTime).TotalSeconds);
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
                    var totalAllocatedBytes = GC.GetTotalAllocatedBytes(false);
                    var allocatedBytesPerSecond = (totalAllocatedBytes - lastAllocatedBytes) / elapsedSeconds;
                    var managedHeapBytes = GC.GetTotalMemory(false);
                    var gen0Delta = GC.CollectionCount(0) - lastGen0Count;
                    var gen1Delta = GC.CollectionCount(1) - lastGen1Count;
                    var gen2Delta = GC.CollectionCount(2) - lastGen2Count;

                    Console.Write(
                        $"[Video] target={videoSource.StreamInfo.FrameRate:F1} fps | " +
                        $"presented={presentedFrames - lastPresentedFrames} | " +
                        $"decoded={decodedFrames - lastDecodedFrames} | " +
                        $"dropped={droppedFrames - lastDroppedFrames} | " +
                        $"queue={videoSource.QueueDepth} | hw={videoSource.IsHardwareDecoding} | " +
                        $"master={masterTs:F3}s | audio={audioTs:F3}s | video={videoTs:F3}s | " +
                        $"a-m={audioMasterDrift:F1}ms | v-m={videoMasterDrift:F1}ms | " +
                        $"a-v={avDrift:F1}ms | corr={corrMs:F1}ms | " +
                        $"gc.alloc={FormatBytes(allocatedBytesPerSecond)}/s | " +
                        $"gc.heap={FormatBytes(managedHeapBytes)} | " +
                        $"gc.gen={gen0Delta}/{gen1Delta}/{gen2Delta}");

                    // Move cursor back to overwrite the last message.
                    Console.Write('\r');

                    lastDecodedFrames = decodedFrames;
                    lastPresentedFrames = presentedFrames;
                    lastDroppedFrames = droppedFrames;
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

    private static void DrawFpsOverlay(nint renderer, int outputWidth, double fps, double videoFps)
    {
        var line1 = $"FPS {fps:0.0}";
        var line2 = $"VID {videoFps:0.0}";
        const int scale = 3;
        const int spacing = 2;
        const int glyphWidth = 5;
        const int glyphHeight = 7;
        const int lineSpacing = 4;
        const int margin = 12;
        const int padding = 6;

        var line1Width = (line1.Length * glyphWidth * scale) + ((line1.Length - 1) * spacing);
        var line2Width = (line2.Length * glyphWidth * scale) + ((line2.Length - 1) * spacing);
        var textWidth = Math.Max(line1Width, line2Width);
        var textHeight = glyphHeight * scale * 2 + lineSpacing;

        var box = new SDL.FRect
        {
            X = Math.Max(0, outputWidth - textWidth - (padding * 2) - margin),
            Y = margin,
            W = textWidth + (padding * 2),
            H = textHeight + (padding * 2)
        };

        SDL.SetRenderDrawColor(renderer, 0, 0, 0, 160);
        SDL.RenderFillRect(renderer, in box);

        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        DrawBitmapText(renderer, line1, box.X + padding, box.Y + padding, scale, spacing);
        DrawBitmapText(renderer, line2, box.X + padding, box.Y + padding + glyphHeight * scale + lineSpacing, scale, spacing);
    }

    private static void DrawBitmapText(nint renderer, string text, float x, float y, int scale, int spacing)
    {
        var cursorX = x;
        foreach (var ch in text)
        {
            DrawBitmapGlyph(renderer, ch, cursorX, y, scale);
            cursorX += 5 * scale + spacing;
        }
    }

    private static void DrawBitmapGlyph(nint renderer, char ch, float x, float y, int scale)
    {
        var pattern = GetGlyphPattern(ch);
        for (var row = 0; row < pattern.Length; row++)
        {
            var line = pattern[row];
            for (var col = 0; col < line.Length; col++)
            {
                if (line[col] != '1')
                    continue;

                var pixel = new SDL.FRect
                {
                    X = x + col * scale,
                    Y = y + row * scale,
                    W = scale,
                    H = scale
                };

                SDL.RenderFillRect(renderer, in pixel);
            }
        }
    }

    private static string[] GetGlyphPattern(char ch)
    {
        return ch switch
        {
            '0' => ["11111", "10001", "10001", "10001", "10001", "10001", "11111"],
            '1' => ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
            '2' => ["11111", "00001", "00001", "11111", "10000", "10000", "11111"],
            '3' => ["11111", "00001", "00001", "01111", "00001", "00001", "11111"],
            '4' => ["10001", "10001", "10001", "11111", "00001", "00001", "00001"],
            '5' => ["11111", "10000", "10000", "11111", "00001", "00001", "11111"],
            '6' => ["11111", "10000", "10000", "11111", "10001", "10001", "11111"],
            '7' => ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
            '8' => ["11111", "10001", "10001", "11111", "10001", "10001", "11111"],
            '9' => ["11111", "10001", "10001", "11111", "00001", "00001", "11111"],
            'F' => ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
            'P' => ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
            'S' => ["11111", "10000", "10000", "11111", "00001", "00001", "11111"],
            'V' => ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
            'I' => ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
            'D' => ["11100", "10010", "10001", "10001", "10001", "10010", "11100"],
            '.' => ["00000", "00000", "00000", "00000", "00000", "00110", "00110"],
            ' ' => ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
            _ => ["11111", "00001", "00010", "00100", "01000", "00000", "01000"]
        };
    }
}