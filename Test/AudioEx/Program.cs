using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using SDL3;
using System.Collections.Concurrent;
using System.Runtime;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
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

    private sealed class PlaybackInputState
    {
        public double TimelineSeconds;

        public double DurationSeconds;
    }

    private sealed class PlaylistMedia : IDisposable
    {
        private bool _disposed;

        public PlaylistMedia(
            string filePath,
            FFVideoSource videoSource,
            FFAudioSource audioSource,
            FFSharedDemuxSession? sharedDemuxSession,
            double startOffsetSeconds,
            double durationSeconds)
        {
            Label = Path.GetFileName(filePath);
            VideoSource = videoSource;
            AudioSource = audioSource;
            SharedDemuxSession = sharedDemuxSession;
            StartOffsetSeconds = startOffsetSeconds;
            DurationSeconds = durationSeconds;
            EndOffsetSeconds = startOffsetSeconds + durationSeconds;
        }

        public string Label { get; }

        public FFVideoSource VideoSource { get; }

        public FFAudioSource AudioSource { get; }

        public FFSharedDemuxSession? SharedDemuxSession { get; }

        public double StartOffsetSeconds { get; }

        public double DurationSeconds { get; }

        public double EndOffsetSeconds { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            VideoSource.Dispose();
            AudioSource.Dispose();
            SharedDemuxSession?.Dispose();
            _disposed = true;
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

        //var testFile60Fps = "/home/seko/Videos/おねがいダーリン_0611.mov";
        var testFile60Fps = "/home/seko/Videos/おねがいダーリン_0611.mov";
        var testFile60Fps2 = "/home/seko/Videos/shootingstar_0611_1.mov";
        var inputFiles = args.Length > 0
            ? args.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray()
            : [testFile60Fps, testFile60Fps2];

        if (inputFiles.Length == 0)
        {
            Console.WriteLine("[AudioEx] No media input provided.");
            return;
        }

        foreach (var file in inputFiles)
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"[AudioEx] Missing media file: {file}");
                return;
            }
        }

        var ffmpegRoot = ResolveFfmpegRootPath();
        if (!string.IsNullOrWhiteSpace(ffmpegRoot))
            ffmpeg.RootPath = ffmpegRoot;

        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"FFmpeg version: {ffmpeg.av_version_info()}");

        var streamSelections = new (string FilePath, MediaStreamInfoEntry VideoStream, MediaStreamInfoEntry AudioStream)[inputFiles.Length];
        for (var i = 0; i < inputFiles.Length; i++)
        {
            var file = inputFiles[i];
            if (!MediaStreamCatalog.TryGetFirstStream(file, MediaStreamKind.Video, out var videoStream))
            {
                Console.WriteLine($"[AudioEx] No video stream found in '{file}'.");
                return;
            }

            if (!MediaStreamCatalog.TryGetFirstStream(file, MediaStreamKind.Audio, out var audioStream))
            {
                Console.WriteLine($"[AudioEx] No audio stream found in '{file}'.");
                return;
            }

            streamSelections[i] = (file, videoStream, audioStream);
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

            var useSharedDemux = !string.Equals(
                Environment.GetEnvironmentVariable("AUDIOEX_USE_SHARED_DEMUX"),
                "0",
                StringComparison.Ordinal);
            using var audioMixer = new AudioMixer(audioEngine, negotiatedBufferSize);

            var videoTransportConfig = new VideoTransportEngineConfig
            {
                PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
            }.CloneNormalized();
            videoTransportConfig.ClockSyncMode = VideoTransportClockSyncMode.AudioLed;

            var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
            using var videoTransport = new VideoTransportEngine(videoClock, videoTransportConfig, ownsClock: false);
            using var videoMixer = new VideoMixer(videoTransport, ownsEngine: false);
            var driftCorrectionConfig = new AudioVideoDriftCorrectionConfig
            {
                Enabled = true
            };
            using var playbackMixer = new AudioVideoMixer(audioMixer, videoMixer, driftCorrectionConfig, ownsAudioMixer: false, ownsVideoMixer: false);

            playbackMixer.AudioSourceError += static (_, e) => ConsolePrintLine($"[Audio] {e.Message}");
            playbackMixer.VideoSourceError += static (_, e) => ConsolePrintLine($"[Video] {e.Message}");

            var playlist = new List<PlaylistMedia>(streamSelections.Length);
            try
            {
                var timelineOffsetSeconds = 0.0;
                foreach (var selection in streamSelections)
                {
                    var decoderOptions = CreateDemoDecoderOptions(decoderThreadCount, selection.VideoStream.Index);
                    FFSharedDemuxSession? sharedDemux = null;
                    FFVideoDecoder videoDecoder;
                    FFAudioDecoder audioDecoder;

                    if (useSharedDemux)
                    {
                        sharedDemux = FFSharedDemuxSession.OpenFile(selection.FilePath, new FFSharedDemuxSessionOptions
                        {
                            InitialStreamIndices = [selection.VideoStream.Index, selection.AudioStream.Index],
                            PacketQueueCapacityPerStream = 200
                        });
                        videoDecoder = new FFVideoDecoder(sharedDemux, decoderOptions);
                        audioDecoder = new FFAudioDecoder(sharedDemux, audioConfig.SampleRate, audioConfig.Channels, selection.AudioStream.Index);
                    }
                    else
                    {
                        videoDecoder = new FFVideoDecoder(selection.FilePath, decoderOptions);
                        audioDecoder = new FFAudioDecoder(selection.FilePath, audioConfig.SampleRate, audioConfig.Channels, selection.AudioStream.Index);
                    }

                    var videoSource = new FFVideoSource(
                        videoDecoder,
                        new FFVideoSourceOptions
                        {
                            HoldLastFrameOnEndOfStream = true
                        },
                        ownsDecoder: true)
                    {
                        StartOffset = timelineOffsetSeconds
                    };

                    var audioSource = new FFAudioSource(
                        audioDecoder,
                        audioConfig,
                        ownsDecoder: true)
                    {
                        StartOffset = timelineOffsetSeconds
                    };

                    audioSource.AttachToClock(audioMixer.MasterClock);

                    if (!playbackMixer.AddAudioSource(audioSource))
                        throw new InvalidOperationException($"Failed to add audio source for '{selection.FilePath}'.");

                    if (!playbackMixer.AddVideoSource(videoSource))
                        throw new InvalidOperationException($"Failed to add video source for '{selection.FilePath}'.");

                    audioSource.Play();

                    var durationSeconds = ResolvePlaybackDurationSeconds(videoSource.Duration, audioSource.Duration);
                    var playlistItem = new PlaylistMedia(selection.FilePath, videoSource, audioSource, sharedDemux, timelineOffsetSeconds, durationSeconds);
                    playlist.Add(playlistItem);
                    timelineOffsetSeconds += durationSeconds;

                    Console.WriteLine(
                        $"[Audio] {playlistItem.Label} | stream={selection.AudioStream.Index} codec={selection.AudioStream.Codec} | {audioConfig.SampleRate}Hz/{audioConfig.Channels}ch buffer={audioConfig.BufferSize} | start={playlistItem.StartOffsetSeconds:F3}s dur={playlistItem.DurationSeconds:F3}s");
                    Console.WriteLine($"[Video] {playlistItem.Label} | {playlistItem.VideoSource.StreamInfo} | start={playlistItem.StartOffsetSeconds:F3}s");
                }

                var playlistDurationSeconds = playlist.Count == 0 ? 0 : playlist[^1].EndOffsetSeconds;
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

                    var activeMedia = playlist[0];
                    if (!playbackMixer.BindVideoOutputToSource(videoRenderer, activeMedia.VideoSource))
                        throw new InvalidOperationException($"Failed to bind output to source '{activeMedia.Label}'.");

                    var inputState = new PlaybackInputState
                    {
                        TimelineSeconds = 0,
                        DurationSeconds = playlistDurationSeconds
                    };

                    PlaylistMedia ResolveActiveMedia(double timelinePositionSeconds)
                    {
                        foreach (var item in playlist)
                        {
                            if (timelinePositionSeconds < item.EndOffsetSeconds)
                                return item;
                        }

                        return playlist[^1];
                    }

                    void SwitchActiveMedia(PlaylistMedia nextMedia)
                    {
                        if (ReferenceEquals(activeMedia, nextMedia))
                            return;

                        if (!playbackMixer.BindVideoOutputToSource(videoRenderer, nextMedia.VideoSource))
                            throw new InvalidOperationException($"Failed to bind output to source '{nextMedia.Label}'.");

                        activeMedia = nextMedia;
                    }

                    void PrimeVisibleSource()
                    {
                        try
                        {
                            if (activeMedia.VideoSource.RequestNextFrame(out _))
                                return;

                            activeMedia.VideoSource.TryGetFrameAtTime(playbackMixer.Position, out _);
                        }
                        catch
                        {
                        }
                    }

                    void UpdateRendererDiagnostics()
                    {
                        var masterTs = playbackMixer.Position;
                        var videoTs = activeMedia.VideoSource.CurrentFramePtsSeconds;
                        var expectedVideoTimestamp = masterTs - activeMedia.VideoSource.StartOffset;
                        var videoMasterDriftMs = double.IsNaN(videoTs)
                            ? 0
                            : (videoTs - expectedVideoTimestamp) * 1000.0;

                        videoRenderer.UpdateFormatInfo(
                            activeMedia.VideoSource.DecoderSourcePixelFormatName,
                            activeMedia.VideoSource.DecoderOutputPixelFormatName,
                            activeMedia.VideoSource.StreamInfo.FrameRate);
                        videoRenderer.UpdateHudDiagnostics(
                            queueDepth: activeMedia.VideoSource.QueueDepth,
                            uploadMsPerFrame: 0,
                            avDriftMs: videoMasterDriftMs,
                            isHardwareDecoding: activeMedia.VideoSource.IsHardwareDecoding,
                            droppedFrames: activeMedia.VideoSource.DroppedFrameCount);
                    }

                    PrimeVisibleSource();
                    UpdateRendererDiagnostics();
                    playbackMixer.Start();
                    Console.WriteLine($"Audio/video mixer initialised successfully for {playlist.Count} media item(s).");

                    var controlActions = new ConcurrentQueue<ControlAction>();
                    var seekLock = new Lock();
                    var playbackFinished = false;
                    videoRenderer.KeyDown += key =>
                    {
                        switch (key)
                        {
                            case SDL.Keycode.Space:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.TogglePlayPause, 0));
                                break;
                            case SDL.Keycode.Left:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, inputState.TimelineSeconds - SeekStepSeconds));
                                break;
                            case SDL.Keycode.Right:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, inputState.TimelineSeconds + SeekStepSeconds));
                                break;
                            case SDL.Keycode.Home:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, 0));
                                break;
                            case SDL.Keycode.End:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, Math.Max(0, inputState.DurationSeconds - 0.001)));
                                break;
                            case SDL.Keycode.F11:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleFullscreen, 0));
                                break;
                            case SDL.Keycode.H:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleHud, 0));
                                break;
                        }
                    };

                    void PerformSeek(double positionSeconds)
                    {
                        if (!seekLock.TryEnter())
                            return;

                        try
                        {
                            var target = Math.Clamp(positionSeconds, 0, Math.Max(0, playlistDurationSeconds));
                            playbackFinished = false;
                            playbackMixer.Seek(target);
                            SwitchActiveMedia(ResolveActiveMedia(target));
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
                    var lastUploadedPlanes = 0L;
                    var lastStridedUploadedPlanes = 0L;
                    var lastStridedUploadedFrames = 0L;
                    var lastStatsLogTime = DateTime.UtcNow;
                    var playbackTailToleranceSeconds = Math.Max(0.050, negotiatedBufferSize / (double)audioConfig.SampleRate * 2.0);

                    bool HasPlaybackFinished()
                    {
                        if (playlist.All(static item => item.VideoSource.IsEndOfStream && item.AudioSource.IsEndOfStream))
                            return true;

                        if (playlistDurationSeconds <= 0)
                            return false;

                        if (playbackMixer.Position < Math.Max(0, playlistDurationSeconds - playbackTailToleranceSeconds))
                            return false;

                        var lastItem = playlist[^1];
                        var lastAudioNearTail = lastItem.AudioSource.Duration <= 0 ||
                                                lastItem.AudioSource.Position >= Math.Max(0, lastItem.AudioSource.Duration - playbackTailToleranceSeconds);
                        return lastItem.VideoSource.IsEndOfStream && lastAudioNearTail;
                    }

                    var loop = true;
                    while (loop)
                    {
                        inputState.TimelineSeconds = playbackMixer.Position;

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

                        var nextActive = ResolveActiveMedia(playbackMixer.Position);
                        if (!ReferenceEquals(nextActive, activeMedia))
                        {
                            SwitchActiveMedia(nextActive);
                            PrimeVisibleSource();
                            UpdateRendererDiagnostics();
                        }

                        if (!playbackFinished && HasPlaybackFinished())
                        {
                            playbackMixer.Pause();
                            UpdateRendererDiagnostics();
                            ConsolePrintLine("[Playback] End of stream reached for all playlist media.");
                            playbackFinished = true;
                            loop = false;
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        if (!playbackFinished && (now - lastStatsLogTime).TotalSeconds >= 1)
                        {
                            UpdateRendererDiagnostics();

                            var decodedFrames = activeMedia.VideoSource.DecodedFrameCount;
                            var presentedFrames = activeMedia.VideoSource.PresentedFrameCount;
                            var droppedFrames = activeMedia.VideoSource.DroppedFrameCount;
                            var masterTs = playbackMixer.Position;
                            var audioTs = activeMedia.AudioSource.Position;
                            var videoTs = activeMedia.VideoSource.CurrentFramePtsSeconds;
                            var correctionOffsetMs = activeMedia.VideoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                            var expectedVideoTimestamp = masterTs - activeMedia.VideoSource.StartOffset;
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
                            var uploadedPlanes = diag.UploadPlanes;
                            var uploadedPlaneDelta = uploadedPlanes - lastUploadedPlanes;
                            var stridedUploadedPlanes = diag.StridedUploadPlanes;
                            var stridedUploadedPlaneDelta = stridedUploadedPlanes - lastStridedUploadedPlanes;
                            var stridedUploadedFrames = diag.StridedUploadFrames;
                            var stridedUploadedFrameDelta = stridedUploadedFrames - lastStridedUploadedFrames;
                            var stridedFrameRatio = uploadedFrameDelta > 0
                                ? (stridedUploadedFrameDelta / (double)uploadedFrameDelta) * 100.0
                                : 0;
                            var stridedPlaneRatio = uploadedPlaneDelta > 0
                                ? (stridedUploadedPlaneDelta / (double)uploadedPlaneDelta) * 100.0
                                : 0;

                            ConsoleOverwriteLine(
                                $"[A/V] {activeMedia.Label}" +
                                $" | tl={masterTs:F3}/{playlistDurationSeconds:F3}s" +
                                $" | render={videoRenderer.RenderFps:F1}fps src={activeMedia.VideoSource.StreamInfo.FrameRate:F1}fps fmt={videoRenderer.PixelFormatInfo}" +
                                $" | pres={presentedDelta} dec={decodedDelta} drop={droppedDelta} q={activeMedia.VideoSource.QueueDepth}" +
                                $" | up={uploadedFrameDelta} upP={uploadedPlaneDelta} strF={stridedUploadedFrameDelta} ({stridedFrameRatio:0.0}%) strP={stridedUploadedPlaneDelta} ({stridedPlaneRatio:0.0}%) hw={activeMedia.VideoSource.IsHardwareDecoding}" +
                                $" | m={masterTs:F3}s a={audioTs:F3}s v={videoTs:F3}s" +
                                $" v-m={videoMasterDriftMs:+0.0;-0.0}ms v-a={videoAudioDriftMs:+0.0;-0.0}ms corr={correctionOffsetMs:+0.0;-0.0}ms");

                            lastDecodedFrames = decodedFrames;
                            lastPresentedFrames = presentedFrames;
                            lastDroppedFrames = droppedFrames;

                            lastUploadedFrames = uploadedFrames;
                            lastUploadedPlanes = uploadedPlanes;
                            lastStridedUploadedPlanes = stridedUploadedPlanes;
                            lastStridedUploadedFrames = stridedUploadedFrames;
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
                foreach (var item in playlist)
                {
                    try
                    {
                        playbackMixer.RemoveVideoSource(item.VideoSource);
                    }
                    catch
                    {
                    }

                    try
                    {
                        playbackMixer.RemoveAudioSource(item.AudioSource);
                    }
                    catch
                    {
                    }

                    item.Dispose();
                }
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
            PreferSourcePixelFormatWhenSupported = true,
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
