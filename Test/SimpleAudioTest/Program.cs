using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using TestShared;

namespace SimpleAudioTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        //test: /home/seko/Music/EC - Still Waiting.flac

        FFmpegRuntime.EnsureInitialized();

        var a = CommonTestArgs.Parse(args);

        if (a.ShowHelp) { PrintUsage(); return 0; }
        if (a.ListDevices || a.ListHostApis) { return TestHelpers.ListAudioRuntime(a.HostApi, a.ListHostApis, a.ListDevices); }

        if (string.IsNullOrWhiteSpace(a.Input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri = TestHelpers.ResolveUri(a.Input);
        if (uri is null) { Console.Error.WriteLine($"Input file not found: {a.Input}"); return 2; }

        Console.WriteLine($"Input: {uri}");

        try
        {
            using var media = FFMediaItem.Open(uri);

            var source = media.AudioSource;
            if (source is null) { Console.Error.WriteLine("No audio source in media."); return 3; }

            var srcStart = source.Start();
            if (srcStart != MediaResult.Success) { Console.Error.WriteLine($"Audio source start failed: {srcStart}"); return 3; }

            var (engine, output) = TestHelpers.InitAudioOutput(a.HostApi, a.DeviceIndex);
            using var _ = engine;

            Console.WriteLine($"Output device: {output.Device.Name}");
            Console.WriteLine($"Playing ~{a.Seconds:0.#}s…");

            var channels = Math.Max(1, source.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var targetFrames = (int)Math.Ceiling(a.Seconds * sampleRate);
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
