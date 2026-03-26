using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;

namespace AudioMixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var input1 = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var input2 = GetArg(args, "--input2");
        var hostApi = GetArg(args, "--host-api");
        var deviceIndex = int.TryParse(GetArg(args, "--device-index"), out var di) ? di : -1;
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 30;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(input1))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri1 = ResolveUri(input1);
        if (uri1 is null) { Console.Error.WriteLine($"Input file not found: {input1}"); return 2; }

        // If no second input, replay the same file
        var uri2 = !string.IsNullOrWhiteSpace(input2) ? ResolveUri(input2) : uri1;
        if (uri2 is null) { Console.Error.WriteLine($"Input2 file not found: {input2}"); return 2; }

        Console.WriteLine($"Input 1: {uri1}");
        Console.WriteLine($"Input 2: {uri2}");

        try
        {
            using var media1 = new FFMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri1, OpenAudio = true, OpenVideo = false, UseSharedDecodeContext = true,
            });
            using var media2 = new FFMediaItem(new FFmpegOpenOptions
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

            if (source1.Start() != MediaResult.Success) { Console.Error.WriteLine("Source1 start failed."); return 3; }
            if (source2.Start() != MediaResult.Success) { Console.Error.WriteLine("Source2 start failed."); return 3; }

            var source1Duration = source1.DurationSeconds;
            var offset2 = double.IsFinite(source1Duration) && source1Duration > 0 ? source1Duration : 10;

            Console.WriteLine($"Source1 duration: {source1Duration:0.###}s → Source2 offset: {offset2:0.###}s");

            var mixer = new AudioMixer();
            var add1 = mixer.AddSource(source1, 0);
            var add2 = mixer.AddSource(source2, offset2);
            if (add1 != MediaResult.Success || add2 != MediaResult.Success)
            {
                Console.Error.WriteLine($"Mixer add failed: s1={add1}, s2={add2}");
                return 5;
            }

            var mixerStart = mixer.Start();
            if (mixerStart != MediaResult.Success) { Console.Error.WriteLine($"Mixer start failed: {mixerStart}"); return 5; }

            using var engine = new PortAudioEngine();
            var init = engine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(hostApi) ? null : hostApi,
            });
            if (init != MediaResult.Success) { Console.Error.WriteLine($"Engine init failed: {init}"); return 4; }
            if (engine.Start() != MediaResult.Success) { Console.Error.WriteLine("Engine start failed."); return 4; }

            var createOut = engine.CreateOutputByIndex(deviceIndex, out var output);
            if (createOut != MediaResult.Success || output is null) { Console.Error.WriteLine($"Create output failed: {createOut}"); return 4; }
            if (output.Start(new AudioOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("Output start failed."); return 4; }

            Console.WriteLine($"Output device: {output.Device.Name}");
            Console.WriteLine($"Playing ~{seconds:0.#}s via AudioMixer (2 sources with offset). Ctrl+C to stop.");

            var channels = Math.Max(1, source1.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var sampleRate = Math.Max(1, source1.StreamInfo.SampleRate.GetValueOrDefault(48_000));
            var routeMap = channels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };
            var buffer = new float[1024 * channels];
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var lastStatus = DateTime.UtcNow;

            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            // Read from whichever source the mixer clock position indicates
            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var clockPos = mixer.PositionSeconds;
                var activeSource = clockPos < offset2 ? source1 : source2;

                var read = activeSource.ReadSamples(buffer, 1024, out var framesRead);
                if (read != MediaResult.Success || framesRead <= 0)
                {
                    // If source1 ended, switch to source2
                    if (ReferenceEquals(activeSource, source1) && clockPos < offset2)
                    {
                        // Advance clock past offset
                        _ = mixer.Seek(offset2);
                        continue;
                    }
                    break;
                }

                var frame = new AudioFrame(
                    Samples: buffer,
                    FrameCount: framesRead,
                    SourceChannelCount: channels,
                    Layout: AudioFrameLayout.Interleaved,
                    SampleRate: sampleRate,
                    PresentationTime: TimeSpan.FromSeconds(activeSource.PositionSeconds));

                var push = output.PushFrame(in frame, routeMap, channels);
                if (push != MediaResult.Success) break;

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                {
                    var srcName = ReferenceEquals(activeSource, source1) ? "Source1" : "Source2";
                    Console.WriteLine($"active={srcName} mixerPos={clockPos:0.###}s srcPos={activeSource.PositionSeconds:0.###}s");
                    lastStatus = DateTime.UtcNow;
                }
            }

            output.Stop();
            output.Dispose();
            _ = mixer.Stop();
            source1.Stop();
            source2.Stop();
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
        Console.WriteLine("AudioMixerTest — play 2 audio files sequentially via AudioMixer offset");
        Console.WriteLine("Usage: AudioMixerTest --input <file1> [--input2 <file2>] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>         First input file");
        Console.WriteLine("  --input2 <path>        Second input file (defaults to same as --input)");
        Console.WriteLine("  --host-api <id>        Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>     Output device index (-1 = default)");
        Console.WriteLine("  --seconds <n>          Total playback duration (default: 30)");
    }
}

