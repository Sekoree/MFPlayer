using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using TestShared;

namespace AudioMixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        FFmpegRuntime.EnsureInitialized();
        var a = CommonTestArgs.Parse(args);
        var input2 = TestHelpers.GetArg(args, "--input2");

        if (a.ShowHelp) { PrintUsage(); return 0; }
        if (a.ListDevices || a.ListHostApis) { return TestHelpers.ListAudioRuntime(a.HostApi, a.ListHostApis, a.ListDevices); }

        if (string.IsNullOrWhiteSpace(a.Input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri1 = TestHelpers.ResolveUri(a.Input);
        if (uri1 is null) { Console.Error.WriteLine($"Input file not found: {a.Input}"); return 2; }

        // If no second input, replay the same file
        var uri2 = !string.IsNullOrWhiteSpace(input2) ? TestHelpers.ResolveUri(input2) : uri1;
        if (uri2 is null) { Console.Error.WriteLine($"Input2 file not found: {input2}"); return 2; }

        Console.WriteLine($"Input 1: {uri1}");
        Console.WriteLine($"Input 2: {uri2}");

        try
        {
            using var media1 = new FFmpegMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri1, OpenAudio = true, OpenVideo = false, UseSharedDecodeContext = true,
            });
            using var media2 = new FFmpegMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri2, OpenAudio = true, OpenVideo = false, UseSharedDecodeContext = true,
            });

            var source1 = media1.AudioSource;
            var source2 = media2.AudioSource;
            if (source1 is null || source2 is null)
            {
                Console.Error.WriteLine("One or both media items have no audio source.");
                return 3;
            }

            var source1Duration = source1.DurationSeconds;
            var offset2 = double.IsFinite(source1Duration) && source1Duration > 0 ? source1Duration : 10;

            Console.WriteLine($"Source1 duration: {source1Duration:0.###}s → Source2 offset: {offset2:0.###}s");

            // Use AVMixer (audio-only, no video sources/outputs)
            using var mixer = new AVMixer();
            var add1 = mixer.AddAudioSource(source1, 0);
            var add2 = mixer.AddAudioSource(source2, offset2);
            if (add1 != MediaResult.Success || add2 != MediaResult.Success)
            {
                Console.Error.WriteLine($"Mixer add failed: s1={add1}, s2={add2}");
                return 5;
            }

            var (engine, output) = TestHelpers.InitAudioOutput(a.HostApi, a.DeviceIndex);
            using var _ae = engine;
            mixer.AddAudioOutput(output);

            Console.WriteLine($"Output device: {output.Device.Name}");
            Console.WriteLine($"Playing ~{a.Seconds:0.#}s via AVMixer (2 audio sources with offset). Ctrl+C to stop.");

            var channels = Math.Max(1, source1.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source1.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var routeMap = channels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };

            var startPlayback = mixer.StartPlayback(new AVMixerConfig
            {
                SourceChannelCount = channels,
                OutputSampleRate = sampleRate,
                RouteMap = routeMap,
            });
            if (startPlayback != MediaResult.Success)
            {
                Console.Error.WriteLine($"StartPlayback failed: {startPlayback}");
                return 5;
            }

            TestHelpers.RunWithDeadline(a.Seconds, () =>
            {
                Thread.Sleep(100);
                return true;
            }, () =>
            {
                var info = mixer.GetDebugInfo();
                if (info.HasValue)
                {
                    var d = info.Value;
                    Console.WriteLine(
                        $"pos={mixer.PositionSeconds:0.###}s aFrames={d.AudioPushedFrames} aFail={d.AudioPushFailures} aEmpty={d.AudioEmptyReads}");
                }
            });

            _ = mixer.StopPlayback();
            output.Stop();
            output.Dispose();
            engine.Stop();

            Console.WriteLine("Done.");
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
        Console.WriteLine("AudioMixerTest — play 2 audio files via AVMixer with start offsets");
        Console.WriteLine("Usage: AudioMixerTest --input <file1> [--input2 <file2>] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>         First input file");
        Console.WriteLine("  --input2 <path>        Second input file (defaults to same as --input)");
        Console.WriteLine("  --host-api <id>        Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>     Output device index (-1 = default)");
        Console.WriteLine("  --seconds <n>          Total playback duration (default: 30)");
        Console.WriteLine("  --list-devices         List output devices and exit");
        Console.WriteLine("  --list-host-apis       List host APIs and exit");
    }
}
