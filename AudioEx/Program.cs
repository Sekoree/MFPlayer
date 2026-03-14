using AudioEx;
using System.Threading;
using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using SDL3;
using Seko.OwnAudioSharp.Video;
using Seko.OwnAudioSharp.Video.Decoders;
using Seko.OwnAudioSharp.Video.Sources;

namespace AudioEx;

internal class Program
{
    public static void Main(string[] args)
    {
        var testFile = "/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4";

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"ffmpeg.av_version_info: {ffmpeg.av_version_info()}");

        using var engine = AudioEngineFactory.CreateDefault();
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
        engine.Initialize(config);
        engine.Start();

        using var audioSource = new FFAudioSource(testFile, config);
        using var videoSource = new FFVideoSource(testFile, new FFVideoSourceOptions
        {
            UseDedicatedDecodeThread = true,
            QueueCapacity = 6,
            DecoderOptions = new FFVideoDecoderOptions
            {
                EnableHardwareDecoding = true,
                ThreadCount = GetSafeVideoThreadCount()
            }
        });
        using var mixer = new AudioMixer(engine);

        mixer.AddSource(audioSource);
        audioSource.AttachToClock(mixer.MasterClock);
        videoSource.AttachToClock(mixer.MasterClock);

        mixer.Start();
        audioSource.Play();

        if (!SDL.Init(SDL.InitFlags.Video))
            return;

        if (!SDL.CreateWindowAndRenderer("MFPlayer", 1280, 720, SDL.WindowFlags.Resizable, out var window, out var renderer))
        {
            SDL.Quit();
            return;
        }

        var info = videoSource.StreamInfo;
        var texture = SDL.CreateTexture(
            renderer,
            SDL.PixelFormat.ABGR8888,
            SDL.TextureAccess.Streaming,
            info.Width,
            info.Height);

        if (texture == nint.Zero)
        {
            SDL.DestroyRenderer(renderer);
            SDL.DestroyWindow(window);
            SDL.Quit();
            return;
        }

        var frameLock = new Lock();
        VideoFrame? latestFrame = null;
        var hasFrame = false;
        var lastDecodedFrames = 0L;
        var lastPresentedFrames = 0L;
        var lastDroppedFrames = 0L;
        var lastStatsLogTime = DateTime.UtcNow;

        videoSource.FrameReady += (_, e) =>
        {
            VideoFrame? previous;
            lock (frameLock)
            {
                previous = latestFrame;
                latestFrame = e.Frame.AddRef();
                hasFrame = true;
            }

            previous?.Dispose();
        };

        var loop = true;
        while (loop)
        {
            while (SDL.PollEvent(out var e))
            {
                if ((SDL.EventType)e.Type == SDL.EventType.Quit)
                    loop = false;
            }

            // Pump decoder against the shared master clock.
            videoSource.RequestNextFrame(out _);

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
                    SDL.RenderTexture(renderer, texture, nint.Zero, nint.Zero);
                }
                finally
                {
                    frameToRender.Dispose();
                }
            }

            SDL.RenderPresent(renderer);

            var now = DateTime.UtcNow;
            if ((now - lastStatsLogTime).TotalSeconds >= 1)
            {
                var decodedFrames = videoSource.DecodedFrameCount;
                var presentedFrames = videoSource.PresentedFrameCount;
                var droppedFrames = videoSource.DroppedFrameCount;
                var masterTimestamp = mixer.MasterClock.CurrentTimestamp;
                var audioTimestamp = audioSource.Position;
                var videoTimestamp = videoSource.CurrentFramePtsSeconds;
                var correctionOffsetMs = videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                var expectedVideoTimestamp = masterTimestamp - videoSource.StartOffset;
                var expectedAudioTimestamp = masterTimestamp - audioSource.StartOffset;
                var audioMasterDriftMs = (audioTimestamp - expectedAudioTimestamp) * 1000.0;
                var videoMasterDriftMs = double.IsNaN(videoTimestamp)
                    ? double.NaN
                    : (videoTimestamp - expectedVideoTimestamp) * 1000.0;
                var avDriftMs = double.IsNaN(videoTimestamp)
                    ? double.NaN
                    : (videoTimestamp - audioTimestamp) * 1000.0;

                Console.WriteLine(
                    $"[Video] target={videoSource.StreamInfo.FrameRate:F1} fps, presented={presentedFrames - lastPresentedFrames}, decoded={decodedFrames - lastDecodedFrames}, dropped={droppedFrames - lastDroppedFrames}, queue={videoSource.QueueDepth}, hw={videoSource.IsHardwareDecoding}, master={masterTimestamp:F3}s, audio={audioTimestamp:F3}s, video={videoTimestamp:F3}s, a-m={audioMasterDriftMs:F1}ms, v-m={videoMasterDriftMs:F1}ms, a-v={avDriftMs:F1}ms, corr={correctionOffsetMs:F1}ms");

                lastDecodedFrames = decodedFrames;
                lastPresentedFrames = presentedFrames;
                lastDroppedFrames = droppedFrames;
                lastStatsLogTime = now;
            }

            SDL.Delay(1);
        }

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

    private static int GetSafeVideoThreadCount()
    {
        var suggested = Math.Max(2, Environment.ProcessorCount / 4);
        return Math.Min(8, suggested);
    }
}
