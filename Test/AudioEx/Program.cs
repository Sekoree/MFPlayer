using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using SDL3;
using System.Collections.Concurrent;
using System.Runtime;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;
using AudioPlaybackEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace AudioEx;

internal static class Program
{
    private const double SeekStepSeconds = 5.0;

    private enum ControlActionKind
    {
        TogglePlayPause,
        SeekAbsolute,
        ToggleFullscreen,
        ToggleHud
    }

    private readonly struct ControlAction
    {
        public ControlAction(ControlActionKind kind, double positionSeconds)
        {
            Kind = kind;
            PositionSeconds = positionSeconds;
        }

        public ControlActionKind Kind { get; }

        public double PositionSeconds { get; }
    }

    private sealed class ControlActionDispatcher
    {
        private readonly ConcurrentQueue<ControlAction> _controlActions;
        private readonly Func<double> _getTimelineSeconds;
        private readonly Func<double> _getActiveDurationSeconds;

        public ControlActionDispatcher(
            ConcurrentQueue<ControlAction> controlActions,
            Func<double> getTimelineSeconds,
            Func<double> getActiveDurationSeconds)
        {
            _controlActions = controlActions;
            _getTimelineSeconds = getTimelineSeconds;
            _getActiveDurationSeconds = getActiveDurationSeconds;
        }

        public void OnKeyDown(SDL.Keycode key)
        {
            switch (key)
            {
                case SDL.Keycode.Space:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.TogglePlayPause, 0));
                    break;

                case SDL.Keycode.Left:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, _getTimelineSeconds() - SeekStepSeconds));
                    break;

                case SDL.Keycode.Right:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, _getTimelineSeconds() + SeekStepSeconds));
                    break;

                case SDL.Keycode.Home:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, 0));
                    break;

                case SDL.Keycode.End:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, Math.Max(0, _getActiveDurationSeconds() - 0.001)));
                    break;

                case SDL.Keycode.F11:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleFullscreen, 0));
                    break;

                case SDL.Keycode.H:
                    _controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleHud, 0));
                    break;
            }
        }
    }

    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
            Console.Out.Flush();
        };

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex}");
            Console.Out.Flush();
        }
    }

    private static void Run()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        var testFile60Fps = "/home/sekoree/Videos/おねがいダーリン_0611.mov";
        var inputFile = testFile60Fps;

        if (!File.Exists(inputFile))
        {
            Console.WriteLine($"[AudioEx] Missing demo video: {inputFile}");
            return;
        }

        var ffmpegRoot = ResolveFfmpegRootPath();
        if (!string.IsNullOrWhiteSpace(ffmpegRoot))
            ffmpeg.RootPath = ffmpegRoot;

        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"FFmpeg version: {ffmpeg.av_version_info()}");

        if (!MediaStreamCatalog.TryGetFirstStream(inputFile, MediaStreamKind.Video, out var videoStream))
        {
            Console.WriteLine($"[AudioEx] No video stream found in '{inputFile}'.");
            return;
        }

        if (!MediaStreamCatalog.TryGetFirstStream(inputFile, MediaStreamKind.Audio, out var audioStream))
        {
            Console.WriteLine($"[AudioEx] No audio stream found in '{inputFile}'.");
            return;
        }

        var decoderThreadCount = GetSafeVideoThreadCount();
        var requestedAudioConfig = AudioConfig.Default;

        using var audioEngine = AudioPlaybackEngineFactory.CreateEngine(requestedAudioConfig);

        try
        {
            var startResult = audioEngine.Start();
            if (startResult < 0)
                throw new InvalidOperationException($"Failed to start audio engine. Error code: {startResult}");

            var negotiatedBufferSize = audioEngine.FramesPerBuffer > 0
                ? audioEngine.FramesPerBuffer
                : requestedAudioConfig.BufferSize;

            var audioConfig = new AudioConfig
            {
                SampleRate = requestedAudioConfig.SampleRate,
                Channels = requestedAudioConfig.Channels,
                BufferSize = negotiatedBufferSize
            };

            var decoderOptions = CreateDemoDecoderOptions(decoderThreadCount, videoStream.Index);
            using var videoSource = new FFVideoSource(
                new FFVideoDecoder(inputFile, decoderOptions),
                new FFVideoSourceOptions
                {
                    HoldLastFrameOnEndOfStream = true
                },
                ownsDecoder: true);

            using var audioSource = new FFAudioSource(
                new FFAudioDecoder(inputFile, audioConfig.SampleRate, audioConfig.Channels, audioStream.Index),
                audioConfig,
                ownsDecoder: true);

            using var audioMixer = new AudioMixer(audioEngine, negotiatedBufferSize);
            using var playbackMixer = AudioVideoMixerFactory.Create(audioMixer, new VideoTransportEngineConfig
            {
                PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
            });

            playbackMixer.AudioSourceError += static (_, e) => ConsolePrintLine($"[Audio] {e.Message}");
            playbackMixer.VideoSourceError += static (_, e) => ConsolePrintLine($"[Video] {e.Message}");

            audioSource.AttachToClock(audioMixer.MasterClock);

            if (!playbackMixer.AddAudioSource(audioSource))
                throw new InvalidOperationException("Failed to add the audio source to the A/V mixer.");

            if (!playbackMixer.AddVideoSource(videoSource))
                throw new InvalidOperationException("Failed to add the video source to the A/V mixer.");

            audioSource.Play();

            var mediaDurationSeconds = ResolvePlaybackDurationSeconds(videoSource.Duration, audioSource.Duration);
            var mediaLabel = Path.GetFileName(inputFile);

            Console.WriteLine($"[Audio] {mediaLabel} | stream={audioStream.Index} codec={audioStream.Codec} | {audioConfig.SampleRate}Hz/{audioConfig.Channels}ch buffer={audioConfig.BufferSize}");
            Console.WriteLine($"[Video] {mediaLabel} | {videoSource.StreamInfo}");

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
                if (!playbackMixer.AddVideoOutput(videoRenderer))
                    throw new InvalidOperationException("Failed to register VideoSDL with the A/V mixer.");

                double GetTimelineSeconds() => playbackMixer.Position;
                double GetActiveDurationSeconds() => mediaDurationSeconds;

                void PrimeVisibleSource()
                {
                    try
                    {
                        if (videoSource.RequestNextFrame(out _))
                            return;

                        videoSource.TryGetFrameAtTime(playbackMixer.Position, out _);
                    }
                    catch
                    {
                        // Best effort immediate refresh only.
                    }
                }

                void UpdateRendererDiagnostics()
                {
                    var masterTs = playbackMixer.Position;
                    var videoTs = videoSource.CurrentFramePtsSeconds;
                    var expectedVideoTimestamp = masterTs - videoSource.StartOffset;
                    var videoMasterDriftMs = double.IsNaN(videoTs)
                        ? 0
                        : (videoTs - expectedVideoTimestamp) * 1000.0;

                    videoRenderer.UpdateFormatInfo(
                        videoSource.DecoderSourcePixelFormatName,
                        videoSource.DecoderOutputPixelFormatName,
                        videoSource.StreamInfo.FrameRate);
                    videoRenderer.UpdateHudDiagnostics(
                        queueDepth: videoSource.QueueDepth,
                        uploadMsPerFrame: 0,
                        avDriftMs: videoMasterDriftMs,
                        isHardwareDecoding: videoSource.IsHardwareDecoding,
                        droppedFrames: videoSource.DroppedFrameCount);
                }

                if (!playbackMixer.BindVideoOutputToSource(videoRenderer, videoSource))
                    throw new InvalidOperationException($"Failed to bind output to source '{mediaLabel}'.");

                PrimeVisibleSource();
                UpdateRendererDiagnostics();
                playbackMixer.Start();
                Console.WriteLine("Audio/video mixer initialised successfully.");

                var controlActions = new ConcurrentQueue<ControlAction>();
                var seekLock = new Lock();
                var playbackFinished = false;
                var controlDispatcher = new ControlActionDispatcher(controlActions, GetTimelineSeconds, GetActiveDurationSeconds);
                videoRenderer.KeyDown += controlDispatcher.OnKeyDown;

                void PerformSeek(double positionSeconds)
                {
                    if (!seekLock.TryEnter())
                        return;

                    try
                    {
                        var target = Math.Max(0, positionSeconds);
                        playbackFinished = false;
                        playbackMixer.Seek(target);
                        PrimeVisibleSource();
                        UpdateRendererDiagnostics();
                    }
                    finally
                    {
                        seekLock.Exit();
                    }
                }

                var lastDecodedFrames = 0L;
                var lastPresentedFrames = 0L;
                var lastDroppedFrames = 0L;
                var lastUploadedFrames = 0L;
                var lastStatsLogTime = DateTime.UtcNow;
                var playbackTailToleranceSeconds = Math.Max(
                    0.050,
                    Math.Max(
                        negotiatedBufferSize / (double)audioConfig.SampleRate * 2.0,
                        1.0 / Math.Max(1.0, videoSource.StreamInfo.FrameRate)));

                bool HasPlaybackFinished()
                {
                    if (videoSource.IsEndOfStream && audioSource.IsEndOfStream)
                        return true;

                    if (!videoSource.IsEndOfStream || mediaDurationSeconds <= 0)
                        return false;

                    var masterNearTail = playbackMixer.Position >= Math.Max(0, mediaDurationSeconds - playbackTailToleranceSeconds);
                    var audioNearTail = audioSource.Duration <= 0 ||
                                        audioSource.Position >= Math.Max(0, audioSource.Duration - playbackTailToleranceSeconds);

                    return masterNearTail && audioNearTail;
                }

                var loop = true;
                while (loop)
                {
                    while (controlActions.TryDequeue(out var action))
                    {
                        switch (action.Kind)
                        {
                            case ControlActionKind.TogglePlayPause:
                                if (playbackMixer.IsRunning)
                                {
                                    playbackMixer.Pause();
                                    ConsolePrintLine("[Playback] Paused.");
                                }
                                else
                                {
                                    playbackMixer.Start();
                                    PrimeVisibleSource();
                                    ConsolePrintLine("[Playback] Playing.");
                                }
                                break;

                            case ControlActionKind.SeekAbsolute:
                                PerformSeek(action.PositionSeconds);
                                break;

                            case ControlActionKind.ToggleFullscreen:
                                var window = videoRenderer.SdlWindowPtr;
                                if (window != nint.Zero)
                                {
                                    var isFullscreen = (SDL.GetWindowFlags(window) & SDL.WindowFlags.Fullscreen) != 0;
                                    SDL.SetWindowFullscreen(window, !isFullscreen);
                                }

                                break;

                            case ControlActionKind.ToggleHud:
                                videoRenderer.EnableHudOverlay = !videoRenderer.EnableHudOverlay;
                                ConsolePrintLine(videoRenderer.EnableHudOverlay
                                    ? "[Video] HUD enabled."
                                    : "[Video] HUD disabled.");
                                break;
                        }
                    }

                    if (!videoRenderer.IsRunning)
                    {
                        loop = false;
                        continue;
                    }

                    if (!playbackFinished && HasPlaybackFinished())
                    {
                        playbackMixer.Pause();
                        UpdateRendererDiagnostics();
                        ConsolePrintLine("[Playback] End of stream reached for the demo media.");
                        playbackFinished = true;
                        lastStatsLogTime = DateTime.UtcNow;
                        Thread.Sleep(10);
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    if (!playbackFinished && (now - lastStatsLogTime).TotalSeconds >= 1)
                    {
                        UpdateRendererDiagnostics();

                        var decodedFrames = videoSource.DecodedFrameCount;
                        var presentedFrames = videoSource.PresentedFrameCount;
                        var droppedFrames = videoSource.DroppedFrameCount;
                        var masterTs = playbackMixer.Position;
                        var audioTs = audioSource.Position;
                        var videoTs = videoSource.CurrentFramePtsSeconds;
                        var correctionOffsetMs = videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                        var expectedVideoTimestamp = masterTs - videoSource.StartOffset;
                        var videoMasterDriftMs = double.IsNaN(videoTs)
                            ? double.NaN
                            : (videoTs - expectedVideoTimestamp) * 1000.0;
                        var videoAudioDriftMs = double.IsNaN(videoTs)
                            ? double.NaN
                            : (videoTs - audioTs) * 1000.0;

                        var decodedDelta = decodedFrames - lastDecodedFrames;
                        var presentedDelta = presentedFrames - lastPresentedFrames;
                        var droppedDelta = droppedFrames - lastDroppedFrames;

                        var diag = videoRenderer.GetDiagnosticsSnapshot();
                        var uploadedFrames = diag.FramesRendered;
                        var uploadedFrameDelta = uploadedFrames - lastUploadedFrames;

                        ConsoleOverwriteLine(
                            $"[A/V] {mediaLabel}" +
                            $" | render={videoRenderer.RenderFps:F1}fps src={videoSource.StreamInfo.FrameRate:F1}fps fmt={videoRenderer.PixelFormatInfo}" +
                            $" | pres={presentedDelta} dec={decodedDelta} drop={droppedDelta} q={videoSource.QueueDepth}" +
                            $" | up={uploadedFrameDelta} hw={videoSource.IsHardwareDecoding}" +
                            $" | m={masterTs:F3}s a={audioTs:F3}s v={videoTs:F3}s" +
                            $" v-m={videoMasterDriftMs:+0.0;-0.0}ms v-a={videoAudioDriftMs:+0.0;-0.0}ms corr={correctionOffsetMs:+0.0;-0.0}ms");

                        lastDecodedFrames = decodedFrames;
                        lastPresentedFrames = presentedFrames;
                        lastDroppedFrames = droppedFrames;

                        lastUploadedFrames = uploadedFrames;
                        lastStatsLogTime = now;
                    }

                    Thread.Sleep(playbackMixer.IsRunning ? 0 : 10);
                }

                playbackMixer.RemoveVideoOutput(videoRenderer);
                videoRenderer.Stop();
            }
            finally
            {
                videoRenderer.Dispose();
            }
        }
        finally
        {
            if (audioEngine.OwnAudioEngineStopped() == 0)
            {
                try
                {
                    audioEngine.Stop();
                }
                catch
                {
                    // Best effort during shutdown.
                }
            }
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
            QueueCapacity = 24,
            PreferredOutputPixelFormats =
            [
                VideoPixelFormat.Nv12,
                VideoPixelFormat.Yuv420p,
                VideoPixelFormat.P010le,
                VideoPixelFormat.Yuv420p10le,
                VideoPixelFormat.Rgba32,
                VideoPixelFormat.Yuv422p,
                VideoPixelFormat.Yuv422p10le
            ],
            PreferSourcePixelFormatWhenSupported = false,
            PreferLowestConversionCost = true
        };
    }

    private static double ResolvePlaybackDurationSeconds(params double[] candidates)
    {
        return candidates
            .Where(static value => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
            .DefaultIfEmpty(0)
            .Max();
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

    private static string? ResolveFfmpegRootPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        string[] candidates =
        [
            "/lib",
            "/usr/lib",
            "/usr/local/lib",
            "/usr/lib/x86_64-linux-gnu"
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
