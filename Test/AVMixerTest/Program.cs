using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using TestShared;

namespace AVMixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        FFmpegRuntime.EnsureInitialized();

        var a = CommonTestArgs.Parse(args);
        var syncModeStr = TestHelpers.GetArg(args, "--sync-mode") ?? "audioled";

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

            var audioSource = media.AudioSource;
            var videoSource = media.VideoSource;
            if (audioSource is null || videoSource is null)
            {
                Console.Error.WriteLine("Media must have both audio and video sources.");
                return 3;
            }

            if (audioSource.Start() != MediaResult.Success) { Console.Error.WriteLine("Audio source start failed."); return 3; }
            if (videoSource.Start() != MediaResult.Success) { Console.Error.WriteLine("Video source start failed."); return 3; }

            var (audioEngine, audioOutput) = TestHelpers.InitAudioOutput(a.HostApi, a.DeviceIndex);
            using var _ae = audioEngine;

            Console.WriteLine($"Audio output: {audioOutput.Device.Name}");

            using var view = TestHelpers.InitVideoView("AVMixerTest");
            _ = view.ShowAndBringToFront();

            // AVMixer directly (no MediaPlayer)
            var mixer = new AVMixer();
            var syncMode = ParseSyncMode(syncModeStr);

            var addA = mixer.AddAudioSource(audioSource);
            var addV = mixer.AddVideoSource(videoSource);
            var setActive = mixer.SetActiveVideoSource(videoSource);
            if (addA != MediaResult.Success || addV != MediaResult.Success || setActive != MediaResult.Success)
            {
                Console.Error.WriteLine($"Mixer attach failed: audio={addA}, video={addV}, active={setActive}");
                return 5;
            }

            mixer.AddAudioOutput(audioOutput);
            mixer.AddVideoOutput(view);

            var channels = Math.Max(1, audioSource.StreamInfo.ChannelCount.GetValueOrDefault(2));
            var srcRate = audioSource.StreamInfo.SampleRate.GetValueOrDefault(0);
            Console.WriteLine($"Source: {channels}ch @ {srcRate}Hz");

            var effectiveChannels = Math.Max(1, channels);
            var playbackConfig = new AVMixerConfig
            {
                SourceChannelCount = effectiveChannels,
                RouteMap = effectiveChannels == 1 ? [0, 0] : [0, 1],
                SyncMode = syncMode,
                OutputSampleRate = srcRate > 0 ? srcRate : 0,
            };

            // StartPlayback starts the clock (Start()) and the pump threads in one call.
            var startPlayback = mixer.StartPlayback(playbackConfig);
            if (startPlayback != MediaResult.Success)
            {
                Console.Error.WriteLine($"StartPlayback failed: {startPlayback}");
                return 5;
            }

            Console.WriteLine($"Playing ~{a.Seconds:0.#}s via AVMixer (sync={syncMode}). Ctrl+C to stop.");

            TestHelpers.RunWithDeadline(a.Seconds, () =>
            {
                Thread.Sleep(8);
                return true;
            }, () =>
            {
                var info = mixer.GetDebugInfo();
                if (info.HasValue)
                {
                    var d = info.Value;
                    var perOutput = mixer.GetVideoOutputDiagnostics();
                    var firstOutput = perOutput.Count > 0 ? perOutput[0] : default;
                    Console.Write(
                        $"\rpos={mixer.PositionSeconds:0.###}s | vPushed={d.VideoPushed} vPushFail={d.VideoPushFailures} vDrop={d.VideoLateDrops} vQ={d.VideoQueueDepth} vNoFrame={d.VideoNoFrame} " +
                        $"wQ={d.VideoWorkerQueueDepth} wQMax={d.VideoWorkerMaxQueueDepth} wDrop={d.VideoWorkerEnqueueDrops + d.VideoWorkerStaleDrops} wFail={d.VideoWorkerPushFailures} " +
                        $"outQ={firstOutput.QueueDepth}/{Math.Max(1, firstOutput.QueueCapacity)} outDrop={firstOutput.EnqueueDrops + firstOutput.StaleDrops} " +
                        $"aFrames={d.AudioPushedFrames} aFail={d.AudioPushFailures} " +
                        $"sync={syncMode}    ");
                }
            });

            _ = mixer.StopPlayback();
            audioOutput.Stop();
            audioOutput.Dispose();
            _ = view.Stop();
            _ = audioSource.Stop();
            _ = videoSource.Stop();
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

    private static AVSyncMode ParseSyncMode(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "synced" or "sync" or "hybrid" or "strict" or "strictav" or "strict-av" => AVSyncMode.Synced,
        "audioled" or "audio-led" or "audio" => AVSyncMode.AudioLed,
        _ => AVSyncMode.Realtime,
    };


    private static void PrintUsage()
    {
        Console.WriteLine("AVMixerTest — A/V playback via AVMixer directly + SDL3");
        Console.WriteLine("Usage: AVMixerTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>         Input file path");
        Console.WriteLine("  --host-api <id>        Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>     Audio output device index (-1 = default)");
        Console.WriteLine("  --seconds <n>          Playback duration (default: 30)");
        Console.WriteLine("  --sync-mode <mode>     Sync mode: audioled|realtime|synced (default: audioled)");
    }
}
