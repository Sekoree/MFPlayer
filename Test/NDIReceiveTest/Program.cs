using NDILib;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Runtime;
using TestShared;

namespace NDIReceiveTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var sourceName   = TestHelpers.GetArg(args, "--source-name");
        var discoverySec = int.TryParse(TestHelpers.GetArg(args, "--discover-seconds"), out var ds) && ds > 0 ? ds : 10;
        var seconds      = double.TryParse(TestHelpers.GetArg(args, "--seconds"), out var s) && s > 0 ? s : 60;
        var hostApi      = TestHelpers.GetArg(args, "--host-api");
        var deviceIndex  = int.TryParse(TestHelpers.GetArg(args, "--device-index"), out var di) ? di : -1;
        var listSources  = args.Contains("--list-sources");

        if (args.Contains("--help") || args.Contains("-h")) { PrintUsage(); return 0; }
        if (args.Contains("--list-devices") || args.Contains("--list-host-apis"))
            return TestHelpers.ListAudioRuntime(hostApi, args.Contains("--list-host-apis"), args.Contains("--list-devices"));

        try
        {
            var rErr = NDIRuntime.Create(out var runtimeInst);
            if (rErr != 0) { Console.Error.WriteLine($"NDI init failed: {rErr}"); return 1; }
            using var _runtime = runtimeInst!;
            Console.WriteLine($"NDI runtime version: {NDIRuntime.Version}");

            var fErr = NDIFinder.Create(out var finderInst);
            if (fErr != 0) { Console.Error.WriteLine($"NDI finder create failed: {fErr}"); return 2; }
            using var finder = finderInst!;
            var sources = DiscoverSources(finder, discoverySec);
            if (sources.Length == 0)
            {
                Console.WriteLine("No NDI sources discovered.");
                return listSources ? 0 : 3;
            }

            Console.WriteLine("Discovered sources:");
            foreach (var src in sources) Console.WriteLine($"  - {src.Name}");
            if (listSources) return 0;

            var selected = SelectSource(sources, sourceName);
            if (selected is null) { Console.Error.WriteLine($"No source matched '{sourceName}'."); return 4; }
            Console.WriteLine($"Connecting to: {selected.Value.Name}");

            var recvErr = NDIReceiver.Create(out var receiverInst, new NDIReceiverSettings
            {
                ColorFormat  = NdiRecvColorFormat.RgbxRgba,
                Bandwidth    = NdiRecvBandwidth.Highest,
                AllowVideoFields = false,
                ReceiverName = "MFPlayer NDIReceiveTest",
            });
            if (recvErr != 0) { Console.Error.WriteLine($"NDI receiver create failed: {recvErr}"); return 7; }
            using var receiver = receiverInst!;
            receiver.Connect(selected.Value);

            using var engine = new NDIEngine();
            var init = engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions());
            if (init != MediaResult.Success) { Console.Error.WriteLine($"NDI engine init failed: {init}"); return 5; }

            var createA = engine.CreateAudioSource(receiver, new NDISourceOptions(), out var audioSource);
            var createV = engine.CreateVideoSource(receiver, new NDISourceOptions(), out var videoSource);
            if (createA != MediaResult.Success || audioSource is null) { Console.Error.WriteLine($"CreateAudioSource failed: {createA}"); return 6; }
            if (createV != MediaResult.Success || videoSource is null) { Console.Error.WriteLine($"CreateVideoSource failed: {createV}"); return 6; }

            var (audioEngine, audioOutput) = TestHelpers.InitAudioOutput(hostApi, deviceIndex);
            using var _ae = audioEngine;
            Console.WriteLine($"Audio output: {audioOutput.Device.Name}");

            using var view = TestHelpers.InitVideoView(title: $"NDIReceiveTest - {selected.Value.Name}");

            using var mixer = new AVMixer();
            _ = mixer.AddAudioSource(audioSource);
            _ = mixer.AddVideoSource(videoSource);
            _ = mixer.SetActiveVideoSource(videoSource);
            mixer.AddAudioOutput(audioOutput);
            mixer.AddVideoOutput(view);

            var startPlayback = mixer.StartPlayback(new AVMixerConfig
            {
                SourceChannelCount = 2,
                RouteMap           = [0, 1],
                SyncMode           = AVSyncMode.Realtime,
            });
            if (startPlayback != MediaResult.Success) { Console.Error.WriteLine($"StartPlayback failed: {startPlayback}"); return 9; }

            Console.WriteLine($"Preview running ~{seconds:0.#}s. Ctrl+C to stop.");

            TestHelpers.RunWithDeadline(seconds, () =>
            {
                Thread.Sleep(10);
                return true;
            }, () =>
            {
                var info  = mixer.GetDebugInfo();
                var vDiag = videoSource.Diagnostics;
                if (!info.HasValue) return;
                var d = info.Value;
                Console.WriteLine(
                    $"vPushed={d.VideoPushed} vDrop={d.VideoLateDrops} aFrames={d.AudioPushedFrames} " +
                    $"wQ={d.VideoWorkerQueueDepth}/{d.VideoWorkerMaxQueueDepth} wDrop={d.VideoWorkerEnqueueDrops + d.VideoWorkerStaleDrops} wFail={d.VideoWorkerPushFailures} " +
                    $"| ndi: q={vDiag.QueueDepth}/{vDiag.JitterBufferFrames} " +
                    $"fmt={vDiag.IncomingPixelFormat}→{vDiag.OutputPixelFormat} fallback={vDiag.FallbackFramesPresented}");
            });

            _ = mixer.StopPlayback();
            audioOutput.Stop();
            audioOutput.Dispose();
            _ = view.Stop();
            _ = engine.Terminate();

            Console.WriteLine("Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 10;
        }
    }

    private static NdiDiscoveredSource[] DiscoverSources(NDIFinder finder, int discoverySec)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, discoverySec));
        while (DateTime.UtcNow < until)
        {
            _ = finder.WaitForSources(500);
            var sources = finder.GetCurrentSources();
            if (sources.Length > 0) return sources;
        }
        return [];
    }

    private static NdiDiscoveredSource? SelectSource(IReadOnlyList<NdiDiscoveredSource> sources, string? name)
    {
        if (sources.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(name)) return sources[0];
        foreach (var src in sources)
            if (src.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) return src;
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NDIReceiveTest — discover NDI source → AVMixer + SDL3");
        Console.WriteLine("Usage: NDIReceiveTest [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --list-sources             Discover and list sources, then exit");
        Console.WriteLine("  --source-name <contains>   Preferred NDI source name match");
        Console.WriteLine("  --discover-seconds <n>     Discovery timeout (default: 10)");
        Console.WriteLine("  --seconds <n>              Preview duration (default: 60)");
        Console.WriteLine("  --host-api <id>            Preferred PortAudio host API");
        Console.WriteLine("  --device-index <n>         Audio output device index (-1 = default)");
        Console.WriteLine("  --list-devices             List audio output devices and exit");
        Console.WriteLine("  --list-host-apis           List host APIs and exit");
    }
}
