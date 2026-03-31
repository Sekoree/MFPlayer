using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;

namespace AudioEx;

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

        ConfigureFfmpegRuntime(options);

        if (options.ListDevices)
        {
            return ListOutputDevices(options);
        }

        if (!TryResolveInputUri(options.Input, out var inputUri, out var error))
        {
            Console.Error.WriteLine(error);
            Options.PrintUsage();
            return 2;
        }

        Console.WriteLine($"Input: {inputUri}");

        try
        {
            using var media = new FFmpegMediaItem(
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
                Console.Error.WriteLine("FFmpegMediaItem did not expose an audio source.");
                return 3;
            }

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

            var createOutput = engine.CreateOutputByIndex(options.DeviceIndex, out var output);
            if (createOutput != MediaResult.Success || output is null)
            {
                Console.Error.WriteLine($"Failed creating output: {createOutput}");
                return 4;
            }

            var outputStart = output.Start(new AudioOutputConfig());
            if (outputStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"Output start failed: {outputStart}");
                return 4;
            }

            Console.WriteLine($"Output device: {output.Device.Name}");
            Console.WriteLine($"Running ~{options.DurationSeconds:0.##}s (chunk={options.FramesPerRead}, buffer={options.EngineBufferFrames})");

            var sourceChannels = Math.Max(1, source.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var targetFrames = Math.Max(options.FramesPerRead, (int)Math.Ceiling(options.DurationSeconds * sampleRate));
            var routeMap = sourceChannels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };
            var readBuffer = new float[options.FramesPerRead * sourceChannels];

            var totalRead = 0;
            var totalPushed = 0;
            while (totalPushed < targetFrames)
            {
                var read = source.ReadSamples(readBuffer, options.FramesPerRead, out var framesRead);
                if (read != MediaResult.Success || framesRead <= 0)
                {
                    Console.WriteLine($"Read stop: code={read}, frames={framesRead}");
                    break;
                }

                totalRead += framesRead;

                var frame = new AudioFrame(
                    Samples: readBuffer,
                    FrameCount: framesRead,
                    SourceChannelCount: sourceChannels,
                    Layout: AudioFrameLayout.Interleaved,
                    SampleRate: sampleRate,
                    PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

                var push = output.PushFrame(in frame, routeMap, sourceChannels);
                if (push != MediaResult.Success)
                {
                    Console.WriteLine($"Push stop: code={push}, semantic={ErrorCodeRanges.ResolveSharedSemantic(push)}");
                    break;
                }

                totalPushed += framesRead;
            }

            output.Stop();
            source.Stop();
            engine.Stop();

            Console.WriteLine($"Done. ReadFrames={totalRead}, PushedFrames={totalPushed}, SourcePos={source.PositionSeconds:0.###}s");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled exception: {ex.Message}");
            return 10;
        }
    }

    private static int ListOutputDevices(Options options)
    {
        using var engine = new PortAudioEngine();
        var init = engine.Initialize(new AudioEngineConfig
        {
            PreferredHostApi = string.IsNullOrWhiteSpace(options.HostApi) ? null : options.HostApi,
        });
        if (init != MediaResult.Success)
        {
            Console.Error.WriteLine($"PortAudio init failed: {init}");
            return 1;
        }

        var start = engine.Start();
        if (start != MediaResult.Success)
        {
            Console.Error.WriteLine($"PortAudio start failed: {start}");
            return 1;
        }

        Console.WriteLine("Host APIs:");
        foreach (var hostApi in engine.GetHostApis())
        {
            Console.WriteLine($"  - {hostApi.Id} ({hostApi.Name}){(hostApi.IsDefault ? " [default]" : string.Empty)} devices={hostApi.DeviceCount}");
        }

        Console.WriteLine("Output devices:");
        var devices = engine.GetOutputDevices();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            Console.WriteLine($"  [{i}] {device.Name} (host={device.HostApi})");
        }

        engine.Stop();
        return 0;
    }

    private static void ConfigureFfmpegRuntime(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfmpegRoot))
        {
            ffmpeg.RootPath = options.FfmpegRoot;
            Environment.SetEnvironmentVariable("SMEDIA_FFMPEG_ROOT", options.FfmpegRoot);
            return;
        }

        var envRoot = Environment.GetEnvironmentVariable("SMEDIA_FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            ffmpeg.RootPath = envRoot;
        }
    }

    private static bool TryResolveInputUri(string? input, out string uri, out string error)
    {
        uri = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Missing required --input argument.";
            return false;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var existingUri) && !string.IsNullOrWhiteSpace(existingUri.Scheme))
        {
            uri = existingUri.AbsoluteUri;
            return true;
        }

        var fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath))
        {
            error = $"Input file does not exist: {fullPath}";
            return false;
        }

        uri = new Uri(fullPath).AbsoluteUri;
        return true;
    }

    private sealed class Options
    {
        public string? Input { get; private set; }

        public string? HostApi { get; private set; }

        public string? FfmpegRoot { get; private set; }

        public bool ListDevices { get; private set; }

        public bool ShowHelp { get; private set; }

        public double DurationSeconds { get; private set; } = 8;

        public int FramesPerRead { get; private set; } = 1024;

        public int EngineBufferFrames { get; private set; } = 1024;

        public int DecodeThreads { get; private set; } = 0;

        public int MaxQueuedPackets { get; private set; }

        public int DeviceIndex { get; private set; } = -1;

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
                    case "--list-devices":
                        options.ListDevices = true;
                        break;
                    case "--input":
                        options.Input = value;
                        i++;
                        break;
                    case "--host-api":
                        options.HostApi = value;
                        i++;
                        break;
                    case "--ffmpeg-root":
                        options.FfmpegRoot = value;
                        i++;
                        break;
                    case "--seconds" when double.TryParse(value, out var seconds) && seconds > 0:
                        options.DurationSeconds = seconds;
                        i++;
                        break;
                    case "--frames-per-read" when int.TryParse(value, out var frames) && frames > 0:
                        options.FramesPerRead = frames;
                        i++;
                        break;
                    case "--engine-buffer-frames" when int.TryParse(value, out var bufferFrames) && bufferFrames >= 64:
                        options.EngineBufferFrames = bufferFrames;
                        i++;
                        break;
                    case "--decode-threads" when int.TryParse(value, out var decodeThreads) && decodeThreads >= 0:
                        options.DecodeThreads = decodeThreads;
                        i++;
                        break;
                    case "--max-queued-packets" when int.TryParse(value, out var maxQueuedPackets) && maxQueuedPackets >= 1:
                        options.MaxQueuedPackets = maxQueuedPackets;
                        i++;
                        break;
                    case "--device-index" when int.TryParse(value, out var deviceIndex):
                        options.DeviceIndex = deviceIndex < -1 ? -1 : deviceIndex;
                        i++;
                        break;
                }
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("AudioEx (S.Media migration harness)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  AudioEx --input <file-or-uri> [options]");
            Console.WriteLine("  AudioEx --list-devices [--host-api <id-or-name>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --input <path|uri>");
            Console.WriteLine("  --seconds <double>");
            Console.WriteLine("  --frames-per-read <int>");
            Console.WriteLine("  --engine-buffer-frames <int>");
            Console.WriteLine("  --decode-threads <int>");
            Console.WriteLine("  --max-queued-packets <int>");
            Console.WriteLine("  --host-api <id-or-name>");
            Console.WriteLine("  --device-index <int>");
            Console.WriteLine("  --ffmpeg-root <path>");
            Console.WriteLine("  --list-devices");
        }
    }
}
