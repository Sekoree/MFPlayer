using S.Media.Core.Audio;
using S.Media.Core.Errors;
using FFmpeg.AutoGen;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;

namespace FirstAudioPlayback.Smoke;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.Input))
        {
            if (options.ShouldListDevices)
            {
                return ListDevices(options);
            }

            Console.Error.WriteLine("Missing required --input argument.");
            PrintUsage();
            return 2;
        }

        if (!TryResolveInputUri(options.Input, out var inputUri, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 2;
        }

        Console.WriteLine($"Input: {inputUri}");
        ConfigureFfmpegRuntime(options);

        try
        {
            using var media = new FFMediaItem(
                new FFmpegOpenOptions
                {
                    InputUri = inputUri,
                    OpenAudio = true,
                    OpenVideo = false,
                    UseSharedDecodeContext = true,
                },
                new FFmpegDecodeOptions
                {
                    DecodeThreadCount = options.DecodeThreads,
                    MaxQueuedPackets = options.MaxQueuedPackets,
                });

            var source = media.AudioSource;
            if (source is null)
            {
                Console.Error.WriteLine("FFMediaItem did not expose an audio source.");
                return 3;
            }

            Console.WriteLine($"Audio stream: codec={source.StreamInfo.Codec ?? "<null>"}, sampleRate={source.StreamInfo.SampleRate?.ToString() ?? "<null>"}, channels={source.StreamInfo.ChannelCount?.ToString() ?? "<null>"}");

            var sourceStart = source.Start();
            if (sourceStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"Audio source start failed: {sourceStart}");
                return 3;
            }

            using var engine = new PortAudioEngine();
            var init = engine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(options.HostApi) ? null : options.HostApi,
                FramesPerBuffer = Math.Max(64, options.EngineBufferFrames),
            });
            if (init != MediaResult.Success)
            {
                Console.Error.WriteLine($"PortAudio engine init failed: {init}");
                return 4;
            }

            var engineStart = engine.Start();
            if (engineStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"PortAudio engine start failed: {engineStart}");
                return 4;
            }

            var hostApis = engine.GetHostApis();
            if (hostApis.Count > 0)
            {
                Console.WriteLine("Host APIs: " + string.Join(", ", hostApis.Select(api => api.IsDefault ? $"{api.Id}*" : api.Id)));
            }


            var outputs = engine.GetOutputDevices();
            if (outputs.Count == 0)
            {
                Console.Error.WriteLine("No output devices exposed by PortAudioEngine.");
                return 4;
            }

            var requestedDeviceIndex = options.DeviceIndex == -1
                ? -1
                : Math.Clamp(options.DeviceIndex, 0, outputs.Count - 1);

            var createOutput = engine.CreateOutputByIndex(requestedDeviceIndex, out var output);
            if (createOutput != MediaResult.Success || output is null)
            {
                Console.Error.WriteLine($"Failed creating output: {createOutput}");
                return 4;
            }

            var resolvedDeviceIndex = -1;
            AudioDeviceInfo? selectedDevice = null;
            for (var i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].Id != output.Device.Id)
                {
                    continue;
                }

                resolvedDeviceIndex = i;
                selectedDevice = outputs[i];
                break;
            }

            var hostApiLabel = selectedDevice?.HostApi ?? "<unknown>";
            var resolvedLabel = resolvedDeviceIndex >= 0 ? resolvedDeviceIndex.ToString() : output.Device.Id.Value;
            Console.WriteLine($"Output device: [{resolvedLabel}] {output.Device.Name}");

            var outputStart = output.Start(new AudioOutputConfig());
            if (outputStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"Output start failed: {outputStart}");
                return 4;
            }

            Console.WriteLine($"Output started: deviceId={output.Device.Id.Value}, hostApi={hostApiLabel}");

            var sourceChannels = Math.Max(1, source.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var routeMap = BuildStereoRouteMap(sourceChannels);
            Console.WriteLine($"Running ~{options.DurationSeconds:0.##}s (readChunk={options.FramesPerRead}, engineBuffer={options.EngineBufferFrames} frames)");

            var stats = RunDirectPlayback(source, output, routeMap, sourceChannels, sampleRate, options);
            Console.WriteLine($"Done. ReadFrames={stats.TotalFramesRead}, PushedFrames={stats.TotalFramesPushed}, PushFailures={stats.PushFailures}, Underflows={stats.Underflows}, SourcePos={source.PositionSeconds:0.###}s");

            output.Stop();
            source.Stop();
            engine.Stop();

            return stats.PushFailures == 0 ? 0 : 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled exception: {ex.Message}");
            return 10;
        }
    }

    private static int[] BuildStereoRouteMap(int sourceChannelCount)
    {
        if (sourceChannelCount <= 1)
        {
            return [0, 0];
        }

        return [0, 1];
    }

    private static PlaybackStats RunDirectPlayback(
        IAudioSource source,
        IAudioOutput output,
        int[] routeMap,
        int sourceChannels,
        int sampleRate,
        Options options)
    {
        var targetFrames = Math.Max(options.FramesPerRead, (int)Math.Ceiling(options.DurationSeconds * sampleRate));
        var readBuffer = new float[options.FramesPerRead * sourceChannels];
        var totalFramesRead = 0;
        var totalFramesPushed = 0;
        var pushFailures = 0;
        var underflows = 0;
        var iteration = 0;

        while (totalFramesPushed < targetFrames)
        {
            iteration++;

            var read = source.ReadSamples(readBuffer, options.FramesPerRead, out var framesRead);
            if (read != MediaResult.Success)
            {
                Console.Error.WriteLine($"Read failed at iter={iteration}: {read} (semantic={ErrorCodeRanges.ResolveSharedSemantic(read)})");
                break;
            }

            if (framesRead <= 0)
            {
                break;
            }

            totalFramesRead += framesRead;

            var frame = new AudioFrame(
                Samples: readBuffer,
                FrameCount: framesRead,
                SourceChannelCount: sourceChannels,
                Layout: AudioFrameLayout.Interleaved,
                SampleRate: sampleRate,
                PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

            var push = output.PushFrame(in frame, routeMap, sourceChannels);
            if (push == MediaResult.Success)
            {
                totalFramesPushed += framesRead;
                continue;
            }

            if (push == (int)MediaErrorCode.PortAudioUnderflow)
            {
                underflows++;
            }

            pushFailures++;
            Console.Error.WriteLine($"Push failed at iter={iteration}: {push} (semantic={ErrorCodeRanges.ResolveSharedSemantic(push)})");
            if (push == (int)MediaErrorCode.PortAudioStreamStartFailed)
            {
                Console.Error.WriteLine("Native PortAudio stream is not running. Verify host API/device selection and runtime backend availability.");
                break;
            }
        }

        return new PlaybackStats(totalFramesRead, totalFramesPushed, pushFailures, underflows);
    }

    private static bool TryResolveInputUri(string inputArg, out string inputUri, out string error)
    {
        inputUri = string.Empty;
        error = string.Empty;

        if (Uri.TryCreate(inputArg, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Scheme))
        {
            inputUri = uri.AbsoluteUri;
            return true;
        }

        var fullPath = Path.GetFullPath(inputArg);
        if (!File.Exists(fullPath))
        {
            error = $"Input file does not exist: {fullPath}";
            return false;
        }

        inputUri = new Uri(fullPath).AbsoluteUri;
        return true;
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                options.ShowHelp = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (arg)
            {
                case "--input":
                    options.Input = value;
                    i++;
                    break;
                case "--seconds":
                    if (double.TryParse(value, out var seconds) && seconds > 0)
                    {
                        options.DurationSeconds = seconds;
                    }

                    i++;
                    break;
                case "--frames-per-read":
                    if (int.TryParse(value, out var framesPerRead) && framesPerRead > 0)
                    {
                        options.FramesPerRead = framesPerRead;
                    }

                    i++;
                    break;
                case "--engine-buffer-frames":
                    if (int.TryParse(value, out var engineBufferFrames) && engineBufferFrames >= 64)
                    {
                        options.EngineBufferFrames = engineBufferFrames;
                    }

                    i++;
                    break;
                case "--decode-threads":
                    if (int.TryParse(value, out var decodeThreads) && decodeThreads >= 0)
                    {
                        options.DecodeThreads = decodeThreads;
                    }

                    i++;
                    break;
                case "--max-queued-packets":
                    if (int.TryParse(value, out var maxQueuedPackets) && maxQueuedPackets >= 1)
                    {
                        options.MaxQueuedPackets = maxQueuedPackets;
                    }

                    i++;
                    break;
                case "--device-index":
                    if (int.TryParse(value, out var deviceIndex))
                    {
                        options.DeviceIndex = deviceIndex < -1 ? -1 : deviceIndex;
                    }

                    i++;
                    break;
                case "--ffmpeg-root":
                    options.FfmpegRootPath = value;
                    i++;
                    break;
                case "--host-api":
                    options.HostApi = value;
                    i++;
                    break;
                case "--list-devices":
                    options.ShouldListDevices = true;
                    break;
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FirstAudioPlayback.Smoke");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --input <path-or-uri> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --seconds <double>             Playback loop target duration (default: 5)");
        Console.WriteLine("  --frames-per-read <int>        Frames to read per iteration (default: 512)");
        Console.WriteLine("  --engine-buffer-frames <int>   PortAudio engine frames per buffer (default: 1024)");
        Console.WriteLine("  --decode-threads <int>         FFmpeg decode thread count, 0=auto (default: 0)");
        Console.WriteLine("  --max-queued-packets <int>     FFmpeg audio queue size (default: 8)");
        Console.WriteLine("  --device-index <int>           Output device index (default: -1, -1 = discovered default output)");
        Console.WriteLine("  --host-api <id-or-name>        Optional PortAudio host API filter (e.g. alsa, jack, wasapi, coreaudio)");
        Console.WriteLine("  --list-devices                 List host APIs and output devices, then exit");
        Console.WriteLine("  --ffmpeg-root <path>           Optional FFmpeg library root path (or set SMEDIA_FFMPEG_ROOT)");
    }

    private static int ListDevices(Options options)
    {
        using var engine = new PortAudioEngine();
        var init = engine.Initialize(new AudioEngineConfig
        {
            PreferredHostApi = string.IsNullOrWhiteSpace(options.HostApi) ? null : options.HostApi,
        });

        if (init != MediaResult.Success)
        {
            Console.Error.WriteLine($"PortAudio engine init failed: {init}");
            return 4;
        }

        var hostApis = engine.GetHostApis();
        Console.WriteLine("Host APIs:");
        foreach (var hostApi in hostApis)
        {
            Console.WriteLine($"  - {hostApi.Id} ({hostApi.Name}){(hostApi.IsDefault ? " [default]" : string.Empty)} devices={hostApi.DeviceCount}");
        }

        var defaultOutput = engine.GetDefaultOutputDevice();
        var defaultInput = engine.GetDefaultInputDevice();
        Console.WriteLine($"Default output: {defaultOutput?.Name ?? "<none>"}");
        Console.WriteLine($"Default input: {defaultInput?.Name ?? "<none>"}");

        Console.WriteLine("Output devices:");
        var outputs = engine.GetOutputDevices();
        for (var i = 0; i < outputs.Count; i++)
        {
            var marker = outputs[i].IsDefaultOutput ? " [default]" : string.Empty;
            var host = string.IsNullOrWhiteSpace(outputs[i].HostApi) ? "unknown" : outputs[i].HostApi;
            Console.WriteLine($"  [{i}] {outputs[i].Name} (host={host}){marker}");
        }

        return 0;
    }

    private static void ConfigureFfmpegRuntime(Options options)
    {
        var root = string.IsNullOrWhiteSpace(options.FfmpegRootPath)
            ? Environment.GetEnvironmentVariable("SMEDIA_FFMPEG_ROOT")
            : options.FfmpegRootPath;

        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        ffmpeg.RootPath = root;
        Console.WriteLine($"FFmpeg root configured: {root}");
    }

    private sealed class Options
    {
        public bool ShowHelp { get; set; }

        public string Input { get; set; } = string.Empty;

        public double DurationSeconds { get; set; } = 5;

        public int FramesPerRead { get; set; } = 512;

        public int EngineBufferFrames { get; set; } = 1024;

        public int DecodeThreads { get; set; }

        public int MaxQueuedPackets { get; set; } = 8;

        public int DeviceIndex { get; set; } = -1;

        public string HostApi { get; set; } = string.Empty;

        public bool ShouldListDevices { get; set; }

        public string FfmpegRootPath { get; set; } = string.Empty;
    }


    private readonly record struct PlaybackStats(int TotalFramesRead, int TotalFramesPushed, int PushFailures, int Underflows);
}
