using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using TestShared;

namespace VideoMixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        FFmpegRuntime.EnsureInitialized();
        var a = CommonTestArgs.Parse(args);
        var input2 = TestHelpers.GetArg(args, "--input2");

        if (a.ShowHelp) { PrintUsage(); return 0; }

        if (string.IsNullOrWhiteSpace(a.Input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri1 = TestHelpers.ResolveUri(a.Input);
        if (uri1 is null) { Console.Error.WriteLine($"Input file not found: {a.Input}"); return 2; }

        var uri2 = !string.IsNullOrWhiteSpace(input2) ? TestHelpers.ResolveUri(input2) : uri1;
        if (uri2 is null) { Console.Error.WriteLine($"Input2 file not found: {input2}"); return 2; }

        Console.WriteLine($"Input 1: {uri1}");
        Console.WriteLine($"Input 2: {uri2}");

        try
        {
            using var media1 = new FFMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri1, OpenAudio = false, OpenVideo = true, UseSharedDecodeContext = true,
            });
            using var media2 = new FFMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri2, OpenAudio = false, OpenVideo = true, UseSharedDecodeContext = true,
            });

            var source1 = media1.VideoSource;
            var source2 = media2.VideoSource;
            if (source1 is null || source2 is null)
            {
                Console.Error.WriteLine("One or both media items have no video source.");
                return 3;
            }

            // Use AVMixer (video-only, no audio sources/outputs)
            using var mixer = new AVMixer();
            var add1 = mixer.AddVideoSource(source1);
            var add2 = mixer.AddVideoSource(source2);
            if (add1 != MediaResult.Success || add2 != MediaResult.Success)
            {
                Console.Error.WriteLine($"Mixer add failed: s1={add1}, s2={add2}");
                return 5;
            }

            _ = mixer.SetActiveVideoSource(source1);

            using var view = TestHelpers.InitVideoView("VideoMixerTest");
            mixer.AddVideoOutput(view);

            Console.WriteLine($"Playing ~{a.Seconds:0.#}s via AVMixer (2 video sources). Ctrl+C to stop.");

            var startPlayback = mixer.StartPlayback(new AVMixerConfig
            {
                VideoOutputQueueCapacity = 4,
                SyncMode = AVSyncMode.Realtime,
            });
            if (startPlayback != MediaResult.Success)
            {
                Console.Error.WriteLine($"StartPlayback failed: {startPlayback}");
                return 5;
            }

            var source1Duration = source1.DurationSeconds;
            var switchAt = double.IsFinite(source1Duration) && source1Duration > 0 ? source1Duration : a.Seconds / 2;
            var switchedToSource2 = false;

            TestHelpers.RunWithDeadline(a.Seconds, () =>
            {
                if (!switchedToSource2 && source1.PositionSeconds >= switchAt)
                {
                    Console.WriteLine($"Source1 reached {switchAt:0.###}s, switching to Source2.");
                    switchedToSource2 = true;
                    _ = mixer.SetActiveVideoSource(source2);
                }

                Thread.Sleep(10);
                return true;
            }, () =>
            {
                var info = mixer.GetDebugInfo();
                var name = switchedToSource2 ? "Source2" : "Source1";
                var activeSource = switchedToSource2 ? source2 : source1;
                if (info.HasValue)
                {
                    var d = info.Value;
                    Console.WriteLine(
                        $"active={name} pos={activeSource.PositionSeconds:0.###}s vPushed={d.VideoPushed} vDrop={d.VideoLateDrops}");
                }
            });

            _ = mixer.StopPlayback();
            _ = view.Stop();

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
        Console.WriteLine("VideoMixerTest — play 2 video files via AVMixer with source switching");
        Console.WriteLine("Usage: VideoMixerTest --input <file1> [--input2 <file2>] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>    First input file");
        Console.WriteLine("  --input2 <path>   Second input file (defaults to same as --input)");
        Console.WriteLine("  --seconds <n>     Total playback duration (default: 30)");
    }
}
