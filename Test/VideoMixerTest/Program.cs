using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.OpenGL.SDL3;
using SDL3;

namespace VideoMixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var input1 = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var input2 = GetArg(args, "--input2");
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

        var uri2 = !string.IsNullOrWhiteSpace(input2) ? ResolveUri(input2) : uri1;
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

            if (source1.Start() != MediaResult.Success) { Console.Error.WriteLine("Source1 start failed."); return 3; }
            if (source2.Start() != MediaResult.Success) { Console.Error.WriteLine("Source2 start failed."); return 3; }

            var mixer = new VideoMixer();
            var add1 = mixer.AddSource(source1);
            var add2 = mixer.AddSource(source2);
            if (add1 != MediaResult.Success || add2 != MediaResult.Success)
            {
                Console.Error.WriteLine($"Mixer add failed: s1={add1}, s2={add2}");
                return 5;
            }

            _ = mixer.SetActiveSource(source1);
            _ = mixer.Start();

            using var view = new SDL3VideoView();
            var viewInit = view.Initialize(new SDL3VideoViewOptions
            {
                Width = 1280, Height = 720,
                WindowTitle = "VideoMixerTest",
                WindowFlags = SDL.WindowFlags.Resizable,
                ShowOnInitialize = true, BringToFrontOnShow = true, PreserveAspectRatio = true,
            });
            if (viewInit != MediaResult.Success) { Console.Error.WriteLine($"SDL3 init failed: {viewInit}"); return 4; }
            if (view.Start(new VideoOutputConfig()) != MediaResult.Success) { Console.Error.WriteLine("SDL3 start failed."); return 4; }

            Console.WriteLine($"Playing ~{seconds:0.#}s via VideoMixer (2 sources). Ctrl+C to stop.");

            var fps = source1.StreamInfo.FrameRate.GetValueOrDefault(30);
            var delayMs = fps > 0 ? Math.Clamp((int)Math.Round(1000.0 / fps), 1, 33) : 16;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var lastStatus = DateTime.UtcNow;
            var pushed = 0L;
            var switchedToSource2 = false;

            var source1Duration = source1.DurationSeconds;
            var switchAt = double.IsFinite(source1Duration) && source1Duration > 0 ? source1Duration : seconds / 2;

            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var activeSource = switchedToSource2 ? source2 : source1;
                var read = activeSource.ReadFrame(out var frame);

                if (read != MediaResult.Success)
                {
                    if (!switchedToSource2)
                    {
                        Console.WriteLine($"Source1 ended (code={read}), switching to Source2.");
                        switchedToSource2 = true;
                        _ = mixer.SetActiveSource(source2);
                        continue;
                    }
                    Console.WriteLine($"Source2 ended (code={read}).");
                    break;
                }

                try
                {
                    _ = view.PushFrame(frame, frame.PresentationTime);
                    pushed++;
                }
                finally
                {
                    frame.Dispose();
                }

                // Switch when source1 position passes its duration
                if (!switchedToSource2 && source1.PositionSeconds >= switchAt)
                {
                    Console.WriteLine($"Source1 reached {switchAt:0.###}s, switching to Source2.");
                    switchedToSource2 = true;
                    _ = mixer.SetActiveSource(source2);
                }

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                {
                    var name = switchedToSource2 ? "Source2" : "Source1";
                    Console.WriteLine($"active={name} pos={activeSource.PositionSeconds:0.###}s frame={activeSource.CurrentFrameIndex} pushed={pushed}");
                    lastStatus = DateTime.UtcNow;
                }

                Thread.Sleep(delayMs);
            }

            _ = view.Stop();
            _ = mixer.Stop();
            _ = source1.Stop();
            _ = source2.Stop();

            Console.WriteLine($"Done. Pushed={pushed} frames.");
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
        Console.WriteLine("VideoMixerTest — play 2 video files sequentially via VideoMixer");
        Console.WriteLine("Usage: VideoMixerTest --input <file1> [--input2 <file2>] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>    First input file");
        Console.WriteLine("  --input2 <path>   Second input file (defaults to same as --input)");
        Console.WriteLine("  --seconds <n>     Total playback duration (default: 30)");
    }
}

