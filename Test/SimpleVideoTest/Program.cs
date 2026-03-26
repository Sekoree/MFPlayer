using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.OpenGL.SDL3;
using SDL3;

namespace SimpleVideoTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var input = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 10;

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
        if (uri is null)
        {
            Console.Error.WriteLine($"Input file not found: {input}");
            return 2;
        }

        Console.WriteLine($"Input: {uri}");

        try
        {
            using var media = new FFMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri,
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

            var source = media.VideoSource;
            if (source is null)
            {
                Console.Error.WriteLine("No video source in media.");
                return 3;
            }

            var srcStart = source.Start();
            if (srcStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"Video source start failed: {srcStart}");
                return 3;
            }

            using var view = new SDL3VideoView();
            var viewInit = view.Initialize(new SDL3VideoViewOptions
            {
                Width = 1280,
                Height = 720,
                WindowTitle = "SimpleVideoTest",
                WindowFlags = SDL.WindowFlags.Resizable,
                ShowOnInitialize = true,
                BringToFrontOnShow = true,
                PreserveAspectRatio = true,
            });
            if (viewInit != MediaResult.Success)
            {
                Console.Error.WriteLine($"SDL3 view init failed: {viewInit}");
                return 4;
            }

            var viewStart = view.Start(new VideoOutputConfig());
            if (viewStart != MediaResult.Success)
            {
                Console.Error.WriteLine($"SDL3 view start failed: {viewStart}");
                return 4;
            }

            Console.WriteLine($"Playing ~{seconds:0.#}s video. Ctrl+C to stop.");

            var fps = source.StreamInfo.FrameRate.GetValueOrDefault(30);
            var delayMs = fps > 0 ? Math.Clamp((int)Math.Round(1000.0 / fps), 1, 33) : 16;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            var pushed = 0L;
            var lastStatus = DateTime.UtcNow;

            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var read = source.ReadFrame(out var frame);
                if (read != MediaResult.Success)
                {
                    Console.WriteLine($"ReadFrame ended: code={read}");
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

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                {
                    Console.WriteLine($"pos={source.PositionSeconds:0.###}s frame={source.CurrentFrameIndex} pushed={pushed}");
                    lastStatus = DateTime.UtcNow;
                }

                Thread.Sleep(delayMs);
            }

            _ = view.Stop();
            _ = source.Stop();

            Console.WriteLine($"Done. Pushed={pushed} frames, pos={source.PositionSeconds:0.###}s");
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
        Console.WriteLine("SimpleVideoTest — decode video → SDL3 window");
        Console.WriteLine("Usage: SimpleVideoTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>   Input file path");
        Console.WriteLine("  --seconds <n>    Playback duration (default: 10)");
    }
}

