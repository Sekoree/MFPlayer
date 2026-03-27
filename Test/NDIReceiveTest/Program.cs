using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Runtime;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

namespace NDIReceiveTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var sourceName = GetArg(args, "--source-name");
        var discoverySec = int.TryParse(GetArg(args, "--discover-seconds"), out var ds) && ds > 0 ? ds : 10;
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 60;
        var hostApi = GetArg(args, "--host-api");
        var deviceIndex = int.TryParse(GetArg(args, "--device-index"), out var di) ? di : -1;
        var listSources = args.Contains("--list-sources");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            using var runtime = new NDIRuntimeScope();
            Console.WriteLine($"NDI runtime version: {NDIRuntime.Version}");

            using var finder = new NDIFinder();
            var sources = DiscoverSources(finder, discoverySec);
            if (sources.Length == 0)
            {
                Console.WriteLine("No NDI sources discovered.");
                return listSources ? 0 : 3;
            }

            Console.WriteLine("Discovered sources:");
            foreach (var src in sources)
                Console.WriteLine($"  - {src.Name}");

            if (listSources) return 0;

            var selected = SelectSource(sources, sourceName);
            if (selected is null)
            {
                Console.Error.WriteLine($"No source matched '{sourceName}'.");
                return 4;
            }

            Console.WriteLine($"Connecting to: {selected.Value.Name}");

            using var receiver = new NDIReceiver(new NDIReceiverSettings
            {
                ColorFormat = NdiRecvColorFormat.RgbxRgba,
                Bandwidth = NdiRecvBandwidth.Highest,
                AllowVideoFields = false,
                ReceiverName = "MFPlayer NDIReceiveTest",
            });
            receiver.Connect(selected.Value);

            using var engine = new NDIEngine();
            var init = engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions());
            if (init != MediaResult.Success) { Console.Error.WriteLine($"NDI engine init failed: {init}"); return 5; }

            var createA = engine.CreateAudioSource(receiver, new NDISourceOptions(), out var audioSource);
            var createV = engine.CreateVideoSource(receiver, new NDISourceOptions(), out var videoSource);
            if (createA != MediaResult.Success || audioSource is null) { Console.Error.WriteLine($"CreateAudioSource failed: {createA}"); return 6; }
            if (createV != MediaResult.Success || videoSource is null) { Console.Error.WriteLine($"CreateVideoSource failed: {createV}"); return 6; }

            // Audio engine
            using var audioEngine = new PortAudioEngine();
            var audioInit = audioEngine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(hostApi) ? null : hostApi,
            });
            if (audioInit != MediaResult.Success) { Console.Error.WriteLine($"Audio engine init failed: {audioInit}"); return 7; }
            if (audioEngine.Start() != MediaResult.Success) { Console.Error.WriteLine("Audio engine start failed."); return 7; }

            var createOut = audioEngine.CreateOutputByIndex(deviceIndex, out var audioOutput);
            if (createOut != MediaResult.Success || audioOutput is null) { Console.Error.WriteLine($"Audio output failed: {createOut}"); return 7; }
            if (audioOutput.Start(new AudioOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("Audio output start failed."); return 7; }

            Console.WriteLine($"Audio output: {audioOutput.Device.Name}");

            // Video output
            using var view = new SDL3VideoView();
            var viewInit = view.Initialize(new SDL3VideoViewOptions
            {
                Width = 1280, Height = 720,
                WindowTitle = $"NDIReceiveTest - {selected.Value.Name}",
                WindowFlags = SDL.WindowFlags.Resizable,
                ShowOnInitialize = true, BringToFrontOnShow = true, PreserveAspectRatio = true,
            });
            if (viewInit != MediaResult.Success) { Console.Error.WriteLine($"SDL3 init failed: {viewInit}"); return 8; }
            if (view.Start(new VideoOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("SDL3 start failed."); return 8; }

            // AV Mixer
            var mixer = new AudioVideoMixer();
            _ = mixer.SetSyncMode(AudioVideoSyncMode.Realtime);
            _ = mixer.AddAudioSource(audioSource);
            _ = mixer.AddVideoSource(videoSource);
            _ = mixer.SetActiveVideoSource(videoSource);
            _ = mixer.Start();

            mixer.AddAudioOutput(audioOutput);
            mixer.AddVideoOutput(view);

            var startPlayback = mixer.StartPlayback(new AudioVideoMixerConfig
            {
                SourceChannelCount = 2,
                RouteMap = [0, 1],
                PresentOnCallerThread = true,
            });
            if (startPlayback != MediaResult.Success) { Console.Error.WriteLine($"StartPlayback failed: {startPlayback}"); return 9; }

            Console.WriteLine($"Preview running ~{seconds:0.#}s. Ctrl+C to stop.");

            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var lastStatus = DateTime.UtcNow;
            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var tickDelay = mixer.TickVideoPresentation();

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                {
                    var info = mixer.GetDebugInfo();
                    var vDiag = videoSource.Diagnostics;
                    if (info.HasValue)
                    {
                        var d = info.Value;
                        Console.WriteLine(
                            $"vPushed={d.VideoPushed} vDrop={d.VideoLateDrops} aFrames={d.AudioPushedFrames} " +
                            $"drift={d.DriftMs:F1}ms | ndi: q={vDiag.QueueDepth}/{vDiag.JitterBufferFrames} " +
                            $"fmt={vDiag.IncomingPixelFormat}→{vDiag.OutputPixelFormat} fallback={vDiag.FallbackFramesPresented}");
                    }
                    lastStatus = DateTime.UtcNow;
                }

                var sleepMs = Math.Max(1, (int)Math.Ceiling(tickDelay.TotalMilliseconds));
                Thread.Sleep(sleepMs);
            }

            _ = mixer.StopPlayback();
            _ = mixer.Stop();
            audioOutput.Stop();
            audioOutput.Dispose();
            _ = view.Stop();
            _ = audioEngine.Stop();
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
            if (src.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                return src;
        return null;
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
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
    }
}
