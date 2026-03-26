using NdiLib;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Runtime;
using S.Media.OpenGL.SDL3;
using SDL3;

namespace NdiVideoReceive;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        try
        {
            using var runtime = new NdiRuntimeScope();
            Console.WriteLine($"NDI runtime version: {NdiRuntime.Version}");

            using var finder = new NdiFinder();
            var sources = DiscoverSources(finder, options.DiscoverySeconds);
            if (sources.Length == 0)
            {
                Console.WriteLine("No NDI sources discovered in the requested window.");
                return options.ListSources ? 0 : 3;
            }

            Console.WriteLine("Discovered sources:");
            foreach (var source in sources)
            {
                Console.WriteLine($"  - {source.Name}");
            }

            if (options.ListSources)
            {
                return 0;
            }

            var selected = SelectSource(sources, options.SourceName);
            if (selected is null)
            {
                Console.WriteLine($"No source matched --source-name '{options.SourceName}'.");
                return 4;
            }

            Console.WriteLine($"Connecting to: {selected.Value.Name}");

            using var receiver = new NdiReceiver(new NdiReceiverSettings
            {
                ColorFormat = NdiRecvColorFormat.RgbxRgba,
                Bandwidth = NdiRecvBandwidth.Highest,
                AllowVideoFields = false,
                ReceiverName = "MFPlayer NdiVideoReceive",
            });
            receiver.Connect(selected.Value);

            using var engine = new NDIEngine();
            var init = engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions());
            if (init != MediaResult.Success)
            {
                Console.WriteLine($"NDI engine init failed: {init}");
                return 5;
            }

            var createAudio = engine.CreateAudioSource(receiver, new NDISourceOptions(), out var audioSource);
            var createVideo = engine.CreateVideoSource(receiver, new NDISourceOptions
            {
                VideoFallbackModeOverride = NDIVideoFallbackMode.PresentLastFrameUntilTimeout,
                VideoJitterBufferFramesOverride = options.VideoJitterFrames,
                AudioJitterBufferMsOverride = options.AudioJitterMs,
            }, out var videoSource);
            if (createAudio != MediaResult.Success || audioSource is null)
            {
                Console.WriteLine($"CreateAudioSource failed: {createAudio}");
                return 6;
            }

            if (createVideo != MediaResult.Success || videoSource is null)
            {
                Console.WriteLine($"CreateVideoSource failed: {createVideo}");
                return 6;
            }

            var audioStart = audioSource.Start();
            var videoStart = videoSource.Start();
            if (audioStart != MediaResult.Success || videoStart != MediaResult.Success)
            {
                Console.WriteLine($"Start failed: audio={audioStart}, video={videoStart}");
                return 7;
            }

            var audioBuffer = new float[1024 * 2];
            var audioRead = audioSource.ReadSamples(audioBuffer, 1024, out var framesRead);
            var videoRead = videoSource.ReadFrame(out var frame);

            var view = new SDL3VideoView();
            VideoFrame? pendingVideoFrame = null;
            try
            {
                var viewInit = view.Initialize(new SDL3VideoViewOptions
                {
                    Width = videoRead == MediaResult.Success ? frame.Width : 1280,
                    Height = videoRead == MediaResult.Success ? frame.Height : 720,
                    WindowTitle = $"NdiVideoReceive - {selected.Value.Name}",
                    WindowFlags = SDL.WindowFlags.Resizable,
                    ShowOnInitialize = true,
                    BringToFrontOnShow = true,
                    PreserveAspectRatio = !options.StretchToFill,
                });

                if (viewInit != MediaResult.Success)
                {
                    Console.WriteLine($"SDL3 view initialize failed: {viewInit}");
                    return 8;
                }

                var startCode = view.Start(new VideoOutputConfig());
                if (startCode != MediaResult.Success)
                {
                    Console.WriteLine($"SDL3 view start failed: {startCode}");
                    return 8;
                }

                var showCode = view.ShowAndBringToFront();
                if (showCode != MediaResult.Success)
                {
                    Console.WriteLine($"SDL3 view show/focus failed: {showCode}");
                }

                if (videoRead == MediaResult.Success)
                {
                    pendingVideoFrame = frame;
                }

                var cancel = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancel.Cancel();
                };

                var previewUntil = options.PreviewSeconds > 0
                    ? DateTime.UtcNow.AddSeconds(options.PreviewSeconds)
                    : DateTime.MaxValue;

                var pushed = 0L;
                var pushFailures = 0L;
                var noFrames = 0L;
                var lateDrops = 0L;
                var lastStatus = DateTime.UtcNow;
                var syncBuffer = new float[Math.Max(1, options.AudioReadFrames) * 2];

                Console.WriteLine($"Preview running. Press Ctrl+C to stop (previewSeconds={options.PreviewSeconds}).");
                while (!cancel.IsCancellationRequested && DateTime.UtcNow < previewUntil)
                {
                    var audioCode = audioSource.ReadSamples(syncBuffer, options.AudioReadFrames, out var syncFramesRead);
                    if (audioCode != MediaResult.Success)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    if (pendingVideoFrame is null)
                    {
                        var code = videoSource.ReadFrame(out var videoFrame);
                        if (code != MediaResult.Success)
                        {
                            if (code == (int)MediaErrorCode.NDIVideoFallbackUnavailable)
                            {
                                noFrames++;
                                Thread.Sleep(8);
                            }
                            else
                            {
                                pushFailures++;
                                Thread.Sleep(5);
                            }

                            continue;
                        }

                        pendingVideoFrame = videoFrame;
                    }

                    if (pendingVideoFrame is null)
                    {
                        continue;
                    }

                    if (!options.DisableAvSync)
                    {
                        var audioMasterSeconds = audioSource.PositionSeconds;
                        var videoSeconds = pendingVideoFrame.PresentationTime.TotalSeconds;
                        var earlyThreshold = options.VideoEarlyHoldMs / 1000.0;
                        var lateThreshold = options.VideoLateDropMs / 1000.0;

                        if (videoSeconds > audioMasterSeconds + earlyThreshold)
                        {
                            Thread.Sleep(2);
                            continue;
                        }

                        if (videoSeconds < audioMasterSeconds - lateThreshold)
                        {
                            pendingVideoFrame.Dispose();
                            pendingVideoFrame = null;
                            lateDrops++;
                            continue;
                        }
                    }

                    try
                    {
                        var push = view.PushFrame(pendingVideoFrame, pendingVideoFrame.PresentationTime);
                        if (push == MediaResult.Success)
                        {
                            pushed++;
                        }
                        else
                        {
                            pushFailures++;
                        }
                    }
                    finally
                    {
                        pendingVideoFrame.Dispose();
                        pendingVideoFrame = null;
                    }

                    if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                    {
                        var videoDiagnostics = videoSource.Diagnostics;
                        Console.WriteLine(
                            $"Preview stats: pushed={pushed}, pushFail={pushFailures}, noFrame={noFrames}, lateDrop={lateDrops}, audioRead={syncFramesRead}, sourceFrame={videoSource.CurrentFrameIndex}, " +
                            $"queue={videoDiagnostics.QueueDepth}/{videoDiagnostics.JitterBufferFrames}, " +
                            $"inFmt={videoDiagnostics.IncomingPixelFormat}, outFmt={videoDiagnostics.OutputPixelFormat}, conv={videoDiagnostics.ConversionPath}, " +
                            $"fallback={videoDiagnostics.FallbackFramesPresented}");
                        lastStatus = DateTime.UtcNow;
                    }

                    Thread.Sleep(16);
                }
            }
            finally
            {
                if (videoRead == MediaResult.Success)
                {
                    if (!ReferenceEquals(frame, pendingVideoFrame))
                    {
                        frame.Dispose();
                    }
                }

                pendingVideoFrame?.Dispose();

                _ = view.Stop();
                view.Dispose();
            }

            var diagnosticsCode = engine.GetDiagnosticsSnapshot(out var snapshot);
            var diagnosticsSemantic = diagnosticsCode == MediaResult.Success
                ? "ok"
                : ErrorCodeRanges.ResolveSharedSemantic(diagnosticsCode).ToString();

            Console.WriteLine($"Audio read: code={audioRead}, frames={framesRead}");
            Console.WriteLine($"Video read: code={videoRead}");
            Console.WriteLine($"Diagnostics: code={diagnosticsCode}, semantic={diagnosticsSemantic}");
            Console.WriteLine($"Engine diagnostics: audioCaptured={snapshot.Audio.FramesCaptured}, videoCaptured={snapshot.Video.FramesCaptured}");

            audioSource.Stop();
            videoSource.Stop();
            engine.Terminate();

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled exception: {ex.Message}");
            return 10;
        }
    }

    private static NdiDiscoveredSource[] DiscoverSources(NdiFinder finder, int discoverySeconds)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, discoverySeconds));
        while (DateTime.UtcNow < until)
        {
            _ = finder.WaitForSources(500);
            var sources = finder.GetCurrentSources();
            if (sources.Length > 0)
            {
                return sources;
            }
        }

        return [];
    }

    private static NdiDiscoveredSource? SelectSource(IReadOnlyList<NdiDiscoveredSource> sources, string? requestedName)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return sources[0];
        }

        foreach (var source in sources)
        {
            if (source.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    private sealed class Options
    {
        public bool ShowHelp { get; private set; }

        public bool ListSources { get; private set; }

        public int DiscoverySeconds { get; private set; } = 10;

        public int PreviewSeconds { get; private set; } = 10;

        public string? SourceName { get; private set; }

        public bool StretchToFill { get; private set; }

        public int VideoJitterFrames { get; private set; } = 3;

        public int AudioJitterMs { get; private set; } = 80;

        public int AudioReadFrames { get; private set; } = 480;

        public int VideoLateDropMs { get; private set; } = 45;

        public int VideoEarlyHoldMs { get; private set; } = 15;

        public bool DisableAvSync { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                var value = i + 1 < args.Length ? args[i + 1] : string.Empty;

                switch (arg)
                {
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        break;
                    case "--list-sources":
                        options.ListSources = true;
                        break;
                    case "--discover-seconds" when int.TryParse(value, out var seconds) && seconds > 0:
                        options.DiscoverySeconds = seconds;
                        i++;
                        break;
                    case "--source-name":
                        options.SourceName = value;
                        i++;
                        break;
                    case "--preview-seconds" when int.TryParse(value, out var previewSeconds):
                        options.PreviewSeconds = Math.Max(0, previewSeconds);
                        i++;
                        break;
                    case "--video-jitter-frames" when int.TryParse(value, out var jitterFrames) && jitterFrames > 0:
                        options.VideoJitterFrames = jitterFrames;
                        i++;
                        break;
                    case "--audio-jitter-ms" when int.TryParse(value, out var jitterMs) && jitterMs > 0:
                        options.AudioJitterMs = jitterMs;
                        i++;
                        break;
                    case "--audio-read-frames" when int.TryParse(value, out var readFrames) && readFrames > 0:
                        options.AudioReadFrames = readFrames;
                        i++;
                        break;
                    case "--video-late-drop-ms" when int.TryParse(value, out var lateMs) && lateMs >= 0:
                        options.VideoLateDropMs = lateMs;
                        i++;
                        break;
                    case "--video-early-hold-ms" when int.TryParse(value, out var earlyMs) && earlyMs >= 0:
                        options.VideoEarlyHoldMs = earlyMs;
                        i++;
                        break;
                    case "--avsync-off":
                        options.DisableAvSync = true;
                        break;
                    case "--stretch":
                        options.StretchToFill = true;
                        break;
                }
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("NdiVideoReceive (S.Media migration harness)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  NdiVideoReceive [--discover-seconds <int>] [--source-name <contains>] [--list-sources]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --discover-seconds <int>   Discovery timeout window (default: 10)");
            Console.WriteLine("  --source-name <contains>   Preferred source name match");
            Console.WriteLine("  --list-sources             List discovered sources and exit");
            Console.WriteLine("  --preview-seconds <int>    Preview loop duration (0 = run until Ctrl+C, default: 10)");
            Console.WriteLine("  --video-jitter-frames <n>  Video jitter buffer depth in frames (default: 3)");
            Console.WriteLine("  --audio-jitter-ms <n>      Audio jitter target in milliseconds (default: 80)");
            Console.WriteLine("  --audio-read-frames <n>    Audio frames per sync read (default: 480)");
            Console.WriteLine("  --video-late-drop-ms <n>   Drop video if later than this behind audio (default: 45)");
            Console.WriteLine("  --video-early-hold-ms <n>  Hold video if this far ahead of audio (default: 15)");
            Console.WriteLine("  --avsync-off               Disable audio-led A/V sync gating");
            Console.WriteLine("  --stretch                  Disable aspect-ratio preservation (fill window)");
        }
    }
}
