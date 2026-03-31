using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using TestShared;

namespace MediaPlayerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        FFmpegRuntime.EnsureInitialized();
        var a = CommonTestArgs.Parse(args);
        if (a.ShowHelp) { PrintUsage(); return 0; }

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
            using var media = FFmpegMediaItem.Open(uri);

            var (audioEngine, audioOutput) = TestHelpers.InitAudioOutput(a.HostApi, a.DeviceIndex);
            using var _ae = audioEngine;

            Console.WriteLine($"Audio output: {audioOutput.Device.Name}");

            using var view = TestHelpers.InitVideoView("MediaPlayerTest");

            // MediaPlayer (inherits AVMixer directly)
            var player = new MediaPlayer();
            player.AddAudioOutput(audioOutput);
            player.AddVideoOutput(view);

            var channels = Math.Max(1, media.AudioSource?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2);
            player.PlaybackConfig = AVMixerConfig.ForSourceToStereo(channels);

            var playCode = player.Play(media);
            if (playCode != MediaResult.Success)
            {
                Console.Error.WriteLine($"Play failed: {playCode}");
                return 5;
            }

            Console.WriteLine($"Playing ~{a.Seconds:0.#}s via MediaPlayer. Ctrl+C to stop.");

            var lastStatus = DateTime.UtcNow;
            TestHelpers.RunWithDeadline(a.Seconds, () =>
            {
                Thread.Sleep(10);
                return true;
            }, () =>
            {
                var info = player.GetDebugInfo();
                if (info.HasValue)
                {
                    var d = info.Value;
                    var outputs = player.GetVideoOutputDiagnostics();
                    var outputSummary = outputs.Count == 0
                        ? "none"
                        : string.Join(" | ", outputs.Select(o =>
                            $"{o.OutputId.ToString()[..8]} q={o.QueueDepth}/{Math.Max(1, o.QueueCapacity)} drop={o.EnqueueDrops + o.StaleDrops} fail={o.PushFailures}"));
                    Console.WriteLine(
                        $"pos={player.PositionSeconds:0.###}s | vPushed={d.VideoPushed} vDrop={d.VideoLateDrops} " +
                        $"wQ={d.VideoWorkerQueueDepth} wQMax={d.VideoWorkerMaxQueueDepth} wDrop={d.VideoWorkerEnqueueDrops + d.VideoWorkerStaleDrops} wFail={d.VideoWorkerPushFailures} " +
                        $"aFrames={d.AudioPushedFrames} aFail={d.AudioPushFailures} | out={outputSummary}");
                }
            });

            _ = player.StopPlayback();
            audioOutput.Stop();
            audioOutput.Dispose();
            _ = view.Stop();
            _ = audioEngine.Stop();

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
        Console.WriteLine("MediaPlayerTest — A/V playback via MediaPlayer + SDL3");
        Console.WriteLine("Usage: MediaPlayerTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>         Input file path");
        Console.WriteLine("  --host-api <id>        Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>     Audio output device index (-1 = default)");
        Console.WriteLine("  --seconds <n>          Playback duration (default: 30)");
    }
}
