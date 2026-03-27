using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;

namespace SimpleAudioTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        //test: /home/sekoree/Music/EC - Still Waiting.flac

        ffmpeg.RootPath = "/lib";
        DynamicallyLoadedBindings.Initialize();
        
        var input = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var hostApi = GetArg(args, "--host-api");
        var deviceIndex = int.TryParse(GetArg(args, "--device-index"), out var di) ? di : -1;
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 10;
        var listDevices = args.Contains("--list-devices");
        var listHostApis = args.Contains("--list-host-apis");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (listDevices || listHostApis)
        {
            return ListAudioRuntime(hostApi, listHostApis, listDevices);
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri = ResolveUri(input);
        if (uri is null)
        {
            Console.Error.WriteLine($"Input file not found: {input}");
            return 2;
        }

        Console.WriteLine($"Input: {uri}");

        //DiagnosticHelper.RunDiagnostics(uri);

        try
        {
            using var media = FFMediaItem.Open(uri);

            var source = media.AudioSource;
            if (source is null)
            {
                Console.Error.WriteLine("No audio source in media.");
                return 3;
            }

            var srcStart = source.Start();
            if (srcStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"Audio source start failed: {srcStart}");
                return 3;
            }

            using var engine = new PortAudioEngine();
            var init = engine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(hostApi) ? null : hostApi,
            });
            if (init != MediaResult.Success) { Console.Error.WriteLine($"Engine init failed: {init}"); return 4; }

            var start = engine.Start();
            if (start != MediaResult.Success) { Console.Error.WriteLine($"Engine start failed: {start}"); return 4; }

            var createOut = engine.CreateOutputByIndex(deviceIndex, out var output);
            if (createOut != MediaResult.Success || output is null) { Console.Error.WriteLine($"Create output failed: {createOut}"); return 4; }

            var outStart = output.Start(new AudioOutputConfig());
            if (outStart != MediaResult.Success) { Console.Error.WriteLine($"Output start failed: {outStart}"); return 4; }

            Console.WriteLine($"Output device: {output.Device.Name}");
            Console.WriteLine($"Playing ~{seconds:0.#}s…");

            var channels = Math.Max(1, source.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var targetFrames = (int)Math.Ceiling(seconds * sampleRate);
            var routeMap = channels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };
            var buffer = new float[1024 * channels];
            var totalPushed = 0;

            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            while (totalPushed < targetFrames && !cancel.IsCancellationRequested)
            {
                var read = source.ReadSamples(buffer, 1024, out var framesRead);
                if (read != MediaResult.Success || framesRead <= 0)
                {
                    Console.Error.WriteLine($"ReadSamples exit: result={read}, framesRead={framesRead}, totalPushed={totalPushed}");
                    break;
                }

                var frame = new AudioFrame(
                    Samples: buffer,
                    FrameCount: framesRead,
                    SourceChannelCount: channels,
                    Layout: AudioFrameLayout.Interleaved,
                    SampleRate: sampleRate,
                    PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

                var push = output.PushFrame(in frame, routeMap, channels);
                if (push != MediaResult.Success) break;

                totalPushed += framesRead;
            }

            output.Stop();
            source.Stop();
            engine.Stop();

            Console.WriteLine($"Done. Pushed={totalPushed} frames, pos={source.PositionSeconds:0.###}s");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 10;
        }
    }

    private static int ListAudioRuntime(string? hostApi, bool listApis, bool listDevices)
    {
        using var engine = new PortAudioEngine();
        var init = engine.Initialize(new AudioEngineConfig
        {
            PreferredHostApi = string.IsNullOrWhiteSpace(hostApi) ? null : hostApi,
        });
        if (init != MediaResult.Success) { Console.Error.WriteLine($"Engine init failed: {init}"); return 1; }

        if (listApis)
        {
            Console.WriteLine("Host APIs:");
            foreach (var api in engine.GetHostApis())
                Console.WriteLine($"  {(api.IsDefault ? "*" : " ")} {api.Id} ({api.Name}) devices={api.DeviceCount}");
        }

        if (listDevices)
        {
            _ = engine.Start();
            Console.WriteLine("Output devices:");
            var devices = engine.GetOutputDevices();
            for (var i = 0; i < devices.Count; i++)
                Console.WriteLine($"  [{i}] {devices[i].Name} (host={devices[i].HostApi})");
            engine.Stop();
        }

        _ = engine.Terminate();
        return 0;
    }

    private static string? ResolveUri(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && !string.IsNullOrWhiteSpace(u.Scheme) && u.Scheme != "file")
            return u.AbsoluteUri;

        var path = Path.GetFullPath(input);
        return File.Exists(path) ? new Uri(path).AbsoluteUri : null;
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SimpleAudioTest — decode audio → PortAudio output");
        Console.WriteLine("Usage: SimpleAudioTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>         Input file path");
        Console.WriteLine("  --host-api <id>        Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>     Output device index (-1 = default)");
        Console.WriteLine("  --seconds <n>          Playback duration (default: 10)");
        Console.WriteLine("  --list-devices         List output devices and exit");
        Console.WriteLine("  --list-host-apis       List host APIs and exit");
    }
}
