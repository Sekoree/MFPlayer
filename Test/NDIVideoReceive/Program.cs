using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.NDI.Clock;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Runtime;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

namespace NDIVideoReceive;

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
            if (options.ListHostApis || options.ListAudioDevices)
            {
                return ListAudioRuntime(options);
            }

            var rErr = NDIRuntime.Create(out var runtimeInst);
            if (rErr != 0) { Console.Error.WriteLine($"NDI init failed: {rErr}"); return 1; }
            using var _runtime = runtimeInst!;
            Console.WriteLine($"NDI runtime version: {NDIRuntime.Version}");

            var fErr = NDIFinder.Create(out var finderInst);
            if (fErr != 0) { Console.Error.WriteLine($"NDI finder create failed: {fErr}"); return 2; }
            using var finder = finderInst!;
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

            var recvErr = NDIReceiver.Create(out var receiverInst, new NDIReceiverSettings
            {
                ColorFormat = NdiRecvColorFormat.RgbxRgba,
                Bandwidth = NdiRecvBandwidth.Highest,
                AllowVideoFields = false,
                ReceiverName = "MFPlayer NDIVideoReceive",
            });
            if (recvErr != 0) { Console.Error.WriteLine($"NDI receiver create failed: {recvErr}"); return 7; }
            using var receiver = receiverInst!;
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
                VideoFallbackMode = NDIVideoFallbackMode.PresentLastFrameUntilTimeout,
                VideoJitterBufferFrames = options.VideoJitterFrames,
                AudioJitterBufferMs = options.AudioJitterMs,
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

            var timelineClock = new NDIExternalTimelineClock();
            var avMixer = new AVMixer(timelineClock, ClockType.External);

            var addMixerAudio = avMixer.AddAudioSource(audioSource);
            var addMixerVideo = avMixer.AddVideoSource(videoSource);
            var setActiveVideo = avMixer.SetActiveVideoSource(videoSource);
            if (addMixerAudio != MediaResult.Success ||
                addMixerVideo != MediaResult.Success ||
                setActiveVideo != MediaResult.Success)
            {
                Console.WriteLine(
                    $"Mixer attach failed: avAudio={addMixerAudio}, avVideo={addMixerVideo}, activeVideo={setActiveVideo}");
                return 7;
            }


            using var audioEngine = new PortAudioEngine();
            var audioEngineInit = audioEngine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(options.HostApi) ? null : options.HostApi,
                FramesPerBuffer = Math.Max(64, options.AudioReadFrames),
                SampleRate = options.AudioSampleRate,
                OutputChannelCount = Math.Max(1, options.OutputChannels),
            });
            if (audioEngineInit != MediaResult.Success)
            {
                Console.WriteLine($"PortAudio engine init failed: {audioEngineInit}");
                return 9;
            }

            var hostApis = audioEngine.GetHostApis();
            if (hostApis.Count > 0)
            {
                Console.WriteLine("Audio host APIs: " + string.Join(", ", hostApis.Select(api => api.IsDefault ? $"{api.Id}*" : api.Id)));
            }

            var audioOutputs = audioEngine.GetOutputDevices();
            if (audioOutputs.Count == 0)
            {
                Console.WriteLine("No audio output devices available from PortAudioEngine.");
                return 9;
            }

            var createOutputCode = audioEngine.CreateOutputByIndex(options.AudioDeviceIndex, out var audioOutput);
            if (createOutputCode != MediaResult.Success || audioOutput is null)
            {
                Console.WriteLine($"CreateOutputByIndex failed: {createOutputCode}");
                return 9;
            }

            var outputStartCode = audioOutput.Start(new AudioOutputConfig());
            if (outputStartCode != MediaResult.Success)
            {
                Console.WriteLine($"Audio output start failed: {outputStartCode}");
                return 9;
            }

            Console.WriteLine($"Audio output: {audioOutput.Device.Name} ({audioOutput.Device.Id.Value}, hostApi={audioOutput.Device.HostApi})");

            var view = new SDL3VideoView();
            try
            {
                var viewInit = view.Initialize(new SDL3VideoViewOptions
                {
                    Width = 1280,
                    Height = 720,
                    WindowTitle = $"NDIVideoReceive - {selected.Value.Name}",
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

                var viewStart = view.Start(new VideoOutputConfig());
                if (viewStart != MediaResult.Success)
                {
                    Console.WriteLine($"SDL3 view start failed: {viewStart}");
                    return 8;
                }

                var showCode = view.ShowAndBringToFront();
                if (showCode != MediaResult.Success)
                {
                    Console.WriteLine($"SDL3 view show/focus failed: {showCode}");
                }

                var sourceChannels = Math.Max(1, options.OutputChannels);
                var routeMap = BuildStereoRouteMap(sourceChannels);
                var mixerConfig = new AVMixerConfig
                {
                    SyncMode = options.SyncMode,
                    AudioReadFrames = Math.Max(240, options.AudioReadFrames),
                    SourceChannelCount = sourceChannels,
                    OutputSampleRate = options.AudioSampleRate,
                    RouteMap = routeMap,
                    VideoDecodeQueueCapacity = Math.Max(2, options.VideoJitterFrames),
                };

                avMixer.AddAudioOutput(audioOutput);
                avMixer.AddVideoOutput(view);

                var playbackStart = avMixer.StartPlayback(mixerConfig);
                if (playbackStart != MediaResult.Success)
                {
                    Console.WriteLine($"Mixer playback start failed: {playbackStart}");
                    return 7;
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

                var lastStatus = DateTime.UtcNow;
                Console.WriteLine($"Preview running. Press Ctrl+C to stop (previewSeconds={options.PreviewSeconds}).");
                while (!cancel.IsCancellationRequested && DateTime.UtcNow < previewUntil)
                {
                    if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                    {
                        var snapshot = avMixer.GetDebugInfo();
                        var outputStats = avMixer.GetVideoOutputDiagnostics();
                        var videoDiagnostics = videoSource.Diagnostics;
                        if (snapshot.HasValue)
                        {
                            var s = snapshot.Value;
                            var outSummary = outputStats.Count == 0
                                ? "none"
                                : string.Join(" | ", outputStats.Select(o =>
                                    $"{o.OutputId.ToString()[..8]} q={o.QueueDepth}/{Math.Max(1, o.QueueCapacity)} drop={o.EnqueueDrops + o.StaleDrops} fail={o.PushFailures}"));
                            Console.WriteLine(
                                $"Preview stats: pushed={s.VideoPushed}, pushFail={s.VideoPushFailures}, noFrame={s.VideoNoFrame}, lateDrop={s.VideoLateDrops}, trimDrop={s.VideoQueueTrimDrops}, qDepth={s.VideoQueueDepth}, " +
                                $"workerQ={s.VideoWorkerQueueDepth}, workerQMax={s.VideoWorkerMaxQueueDepth}, workerDrop={s.VideoWorkerEnqueueDrops + s.VideoWorkerStaleDrops}, workerFail={s.VideoWorkerPushFailures}, " +
                                $"coalesceDrop={s.VideoCoalescedDrops}, " +
                                $"audioPushFail={s.AudioPushFailures}, audioReadFail={s.AudioReadFailures}, audioEmptyRead={s.AudioEmptyReads}, audioFrames={s.AudioPushedFrames}, " +
                                $"syncMode={options.SyncMode}, sourceFrame={videoSource.CurrentFrameIndex}, " +
                                $"queue={videoDiagnostics.QueueDepth}/{videoDiagnostics.JitterBufferFrames}, " +
                                $"inFmt={videoDiagnostics.IncomingPixelFormat}, outFmt={videoDiagnostics.OutputPixelFormat}, conv={videoDiagnostics.ConversionPath}, " +
                                $"fallback={videoDiagnostics.FallbackFramesPresented}, out={outSummary}");
                        }

                        lastStatus = DateTime.UtcNow;
                    }

                    Thread.Sleep(10);
                }

                _ = avMixer.StopPlayback();
            }
            finally
            {
                _ = audioOutput.Stop();
                audioOutput.Dispose();

                _ = view.Stop();
                view.Dispose();
            }

            var diagnosticsCode = engine.GetDiagnosticsSnapshot(out var snapshotAfterRun);
            var diagnosticsSemantic = diagnosticsCode == MediaResult.Success
                ? "ok"
                : ErrorCodeRanges.ResolveSharedSemantic(diagnosticsCode).ToString();

            Console.WriteLine($"Diagnostics: code={diagnosticsCode}, semantic={diagnosticsSemantic}");
            Console.WriteLine($"Engine diagnostics: audioCaptured={snapshotAfterRun.Audio.FramesCaptured}, videoCaptured={snapshotAfterRun.VideoSource.FramesCaptured}");

            _ = avMixer.StopPlayback();
            _ = audioEngine.Stop();
            _ = audioEngine.Terminate();
            _ = engine.Terminate();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled exception: {ex.Message}");
            return 10;
        }
    }

    private static int ListAudioRuntime(Options options)
    {
        using var audioEngine = new PortAudioEngine();
        var init = audioEngine.Initialize(new AudioEngineConfig
        {
            PreferredHostApi = string.IsNullOrWhiteSpace(options.HostApi) ? null : options.HostApi,
        });
        if (init != MediaResult.Success)
        {
            Console.WriteLine($"PortAudio engine init failed: {init}");
            return 11;
        }

        if (options.ListHostApis)
        {
            Console.WriteLine("Host APIs:");
            var hostApis = audioEngine.GetHostApis();
            for (var i = 0; i < hostApis.Count; i++)
            {
                var marker = hostApis[i].IsDefault ? "*" : " ";
                Console.WriteLine($"  {marker} {hostApis[i].Id} ({hostApis[i].Name})");
            }
        }

        if (options.ListAudioDevices)
        {
            Console.WriteLine("Output devices (default first):");
            var outputs = audioEngine.GetOutputDevices();
            var defaultOutput = audioEngine.GetDefaultOutputDevice();
            for (var i = 0; i < outputs.Count; i++)
            {
                var isDefault = defaultOutput.HasValue && outputs[i].Id == defaultOutput.Value.Id;
                Console.WriteLine($"  {(isDefault ? "*" : " ")} [{i}] {outputs[i].Name} ({outputs[i].HostApi}, {outputs[i].Id.Value})");
            }

            Console.WriteLine("Input devices (default first):");
            var inputs = audioEngine.GetInputDevices();
            var defaultInput = audioEngine.GetDefaultInputDevice();
            for (var i = 0; i < inputs.Count; i++)
            {
                var isDefault = defaultInput.HasValue && inputs[i].Id == defaultInput.Value.Id;
                Console.WriteLine($"  {(isDefault ? "*" : " ")} [{i}] {inputs[i].Name} ({inputs[i].HostApi}, {inputs[i].Id.Value})");
            }
        }

        _ = audioEngine.Terminate();
        return 0;
    }

    private static int[] BuildStereoRouteMap(int sourceChannelCount)
    {
        return sourceChannelCount <= 1 ? [0, 0] : [0, 1];
    }

    private static NdiDiscoveredSource[] DiscoverSources(NDIFinder finder, int discoverySeconds)
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

        public int PreviewSeconds { get; private set; } = 10000;

        public string? SourceName { get; private set; }

        public bool StretchToFill { get; private set; }

        public int VideoJitterFrames { get; private set; } = 3;

        public int AudioJitterMs { get; private set; } = 80;

        public int AudioReadFrames { get; private set; } = 480;

        public int AudioSampleRate { get; private set; } = 48_000;

        public int OutputChannels { get; private set; } = 2;

        public int AudioDeviceIndex { get; private set; } = -1;

        public AVSyncMode SyncMode { get; private set; } = AVSyncMode.Realtime;


        public string? HostApi { get; private set; }

        public bool ListHostApis { get; private set; }

        public bool ListAudioDevices { get; private set; }

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
                    case "--audio-sample-rate" when int.TryParse(value, out var sampleRate) && sampleRate > 0:
                        options.AudioSampleRate = sampleRate;
                        i++;
                        break;
                    case "--audio-output-channels" when int.TryParse(value, out var outputChannels) && outputChannels > 0:
                        options.OutputChannels = outputChannels;
                        i++;
                        break;
                    case "--audio-device-index" when int.TryParse(value, out var audioDeviceIndex):
                        options.AudioDeviceIndex = audioDeviceIndex;
                        i++;
                        break;
                    case "--sync-mode":
                        options.SyncMode = ParseSyncMode(value, options.SyncMode);
                        i++;
                        break;
                    case "--host-api":
                        options.HostApi = value;
                        i++;
                        break;
                    case "--list-host-apis":
                        options.ListHostApis = true;
                        break;
                    case "--list-audio-devices":
                        options.ListAudioDevices = true;
                        break;
                    case "--stretch":
                        options.StretchToFill = true;
                        break;
                }
            }

            return options;
        }

        private static AVSyncMode ParseSyncMode(string raw, AVSyncMode fallback)
        {
            return raw?.Trim().ToLowerInvariant() switch
            {
                "realtime" or "stable" => AVSyncMode.Realtime,
                "synced" or "sync" or "hybrid" or "strict" or "strictav" or "strict-av" => AVSyncMode.Synced,
                _ => fallback,
            };
        }

        public static void PrintUsage()
        {
            Console.WriteLine("NDIVideoReceive (S.Media migration harness)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  NDIVideoReceive [--discover-seconds <int>] [--source-name <contains>] [--list-sources]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --discover-seconds <int>   Discovery timeout window (default: 10)");
            Console.WriteLine("  --source-name <contains>   Preferred source name match");
            Console.WriteLine("  --list-sources             List discovered sources and exit");
            Console.WriteLine("  --preview-seconds <int>    Preview loop duration (0 = run until Ctrl+C, default: 10)");
            Console.WriteLine("  --video-jitter-frames <n>  Video jitter buffer depth in frames (default: 3)");
            Console.WriteLine("  --audio-jitter-ms <n>      Audio jitter target in milliseconds (default: 80)");
            Console.WriteLine("  --audio-read-frames <n>    Audio frames per read/push batch (default: 480)");
            Console.WriteLine("  --audio-sample-rate <n>    Audio output sample rate (default: 48000)");
            Console.WriteLine("  --audio-output-channels <n> Audio output channel count (default: 2)");
            Console.WriteLine("  --audio-device-index <n>   PortAudio output index (-1 = default, default: -1)");
            Console.WriteLine("  --sync-mode <mode>         Video sync mode: stable|hybrid|strict (default: stable)");
            Console.WriteLine("  --host-api <id>            Restrict PortAudio discovery to one host API");
            Console.WriteLine("  --list-host-apis           List PortAudio host APIs and exit");
            Console.WriteLine("  --list-audio-devices       List PortAudio input/output devices and exit");
            Console.WriteLine("  --stretch                  Disable aspect-ratio preservation (fill window)");
        }
    }
}
