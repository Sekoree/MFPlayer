using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.OpenGL.SDL3;
using TestShared;

namespace SimpleVideoTest;

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
            using var media = new FFMediaItem(new FFmpegOpenOptions
            {
                InputUri = uri,
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

            var source = media.VideoSource;
            if (source is null) { Console.Error.WriteLine("No video source in media."); return 3; }

            if (source.Start() != MediaResult.Success)
            {
                Console.Error.WriteLine("Video source start failed.");
                return 3;
            }

            // The view owns its render thread; PushFrame is non-blocking.
            // Use SourceTimestamp mode so the render thread paces at the native frame rate.
            using var view = TestHelpers.InitVideoView("SimpleVideoTest", videoConfig: new VideoOutputConfig
            {
                PresentationMode       = VideoOutputPresentationMode.SourceTimestamp,
                TimestampMode = VideoTimestampMode.RebaseOnDiscontinuity,
                TimestampDiscontinuityThreshold = TimeSpan.FromMilliseconds(50),
                StaleFrameDropThreshold = TimeSpan.FromMilliseconds(200),
                MaxSchedulingWait = TimeSpan.FromMilliseconds(33),
            });

            Console.WriteLine($"Playing ~{a.Seconds:0.#}s video. Ctrl+C to stop.");

            var pushed = 0L;
            TestHelpers.RunWithDeadline(a.Seconds, () =>
            {
                var read = source.ReadFrame(out var frame);
                if (read != MediaResult.Success)
                {
                    Console.WriteLine($"ReadFrame ended: code={read}");
                    return false;
                }

                using (frame) { _ = view.PushFrame(frame, frame.PresentationTime); }
                pushed++;
                return true;
            }, () => Console.WriteLine(
                $"pos={source.PositionSeconds:0.###}s  frame={source.CurrentFrameIndex}  pushed={pushed}"));

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
