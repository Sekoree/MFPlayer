using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

namespace MediaPlayerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var input = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 30;
        var hostApi = GetArg(args, "--host-api");
        var deviceIndex = int.TryParse(GetArg(args, "--device-index"), out var di) ? di : -1;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri = ResolveUri(input);
        if (uri is null) { Console.Error.WriteLine($"Input file not found: {input}"); return 2; }

        Console.WriteLine($"Input: {uri}");

        try
        {
            using var media = FFMediaItem.Open(uri);

            // Audio engine
            using var audioEngine = new PortAudioEngine();
            var init = audioEngine.Initialize(new AudioEngineConfig
            {
                PreferredHostApi = string.IsNullOrWhiteSpace(hostApi) ? null : hostApi,
            });
            if (init != MediaResult.Success) { Console.Error.WriteLine($"Audio engine init failed: {init}"); return 4; }
            if (audioEngine.Start() != MediaResult.Success) { Console.Error.WriteLine("Audio engine start failed."); return 4; }

            var createOut = audioEngine.CreateOutputByIndex(deviceIndex, out var audioOutput);
            if (createOut != MediaResult.Success || audioOutput is null) { Console.Error.WriteLine($"Audio output failed: {createOut}"); return 4; }
            if (audioOutput.Start(new AudioOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("Audio output start failed."); return 4; }

            Console.WriteLine($"Audio output: {audioOutput.Device.Name}");

            // Video output
            using var view = new SDL3VideoView();
            var viewInit = view.Initialize(new SDL3VideoViewOptions
            {
                Width = 1280, Height = 720,
                WindowTitle = "MediaPlayerTest",
                WindowFlags = SDL.WindowFlags.Resizable,
                ShowOnInitialize = true, BringToFrontOnShow = true, PreserveAspectRatio = true,
            });
            if (viewInit != MediaResult.Success) { Console.Error.WriteLine($"SDL3 init failed: {viewInit}"); return 4; }
            if (view.Start(new VideoOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("SDL3 start failed."); return 4; }

            // MediaPlayer via AudioVideoMixer
            var mixer = new AudioVideoMixer();
            var player = new MediaPlayer(mixer);
            player.AddAudioOutput(audioOutput);
            player.AddVideoOutput(view);

            var channels = Math.Max(1, media.AudioSource?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2);
            var routeMap = channels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };

            var playCode = player.Play(media);
            if (playCode != MediaResult.Success)
            {
                Console.Error.WriteLine($"Play failed: {playCode}");
                return 5;
            }

            var startPlayback = mixer.StartPlayback(new AudioVideoMixerConfig
            {
                SourceChannelCount = channels,
                RouteMap = routeMap,
                PresentOnCallerThread = true,
            });
            if (startPlayback != MediaResult.Success)
            {
                Console.Error.WriteLine($"StartPlayback failed: {startPlayback}");
                return 5;
            }

            Console.WriteLine($"Playing ~{seconds:0.#}s via MediaPlayer. Ctrl+C to stop.");

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
                    if (info.HasValue)
                    {
                        var d = info.Value;
                        Console.WriteLine(
                            $"pos={mixer.PositionSeconds:0.###}s | vPushed={d.VideoPushed} vDrop={d.VideoLateDrops} " +
                            $"aFrames={d.AudioPushedFrames} aFail={d.AudioPushFailures} drift={d.DriftMs:F1}ms");
                    }
                    lastStatus = DateTime.UtcNow;
                }

                var sleepMs = Math.Max(1, (int)Math.Ceiling(tickDelay.TotalMilliseconds));
                Thread.Sleep(sleepMs);
            }

            _ = mixer.StopPlayback();
            _ = player.Stop();
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

