using NdiLib;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Output;
using S.Media.NDI.Runtime;

namespace NDISendTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        var input = GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT");
        var senderName = GetArg(args, "--sender-name") ?? "MFPlayer NDISendTest";
        var seconds = double.TryParse(GetArg(args, "--seconds"), out var s) && s > 0 ? s : 60;

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
        Console.WriteLine($"NDI sender name: {senderName}");

        try
        {
            using var runtime = new NdiRuntimeScope();
            Console.WriteLine($"NDI runtime version: {NdiRuntime.Version}");

            using var media = FFMediaItem.Open(uri);

            var videoSource = media.VideoSource;
            if (videoSource is null) { Console.Error.WriteLine("No video source in media."); return 3; }
            if (videoSource.Start() != MediaResult.Success) { Console.Error.WriteLine("Video source start failed."); return 3; }

            // NDI engine + output
            using var engine = new NDIEngine();
            var init = engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions());
            if (init != MediaResult.Success) { Console.Error.WriteLine($"NDI engine init failed: {init}"); return 4; }

            var createOut = engine.CreateOutput(senderName, new NDIOutputOptions { EnableVideo = true }, out var ndiOutput);
            if (createOut != MediaResult.Success || ndiOutput is null)
            {
                Console.Error.WriteLine($"NDI CreateOutput failed: {createOut}");
                return 4;
            }

            if (ndiOutput.Start(new VideoOutputConfig()) != MediaResult.Success)
            {
                Console.Error.WriteLine("NDI output start failed.");
                return 4;
            }

            Console.WriteLine($"NDI output created: {ndiOutput.OutputName}");
            Console.WriteLine($"Sending ~{seconds:0.#}s of video via NDI. Ctrl+C to stop.");

            var fps = videoSource.StreamInfo.FrameRate.GetValueOrDefault(30);
            var delayMs = fps > 0 ? Math.Clamp((int)Math.Round(1000.0 / fps), 1, 33) : 16;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var lastStatus = DateTime.UtcNow;
            var pushed = 0L;
            var failed = 0L;

            var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var read = videoSource.ReadFrame(out var frame);
                if (read != MediaResult.Success)
                {
                    Console.WriteLine($"ReadFrame ended: code={read}");
                    break;
                }

                try
                {
                    var pushCode = ndiOutput.PushFrame(frame, frame.PresentationTime);
                    if (pushCode == MediaResult.Success)
                        pushed++;
                    else
                        failed++;
                }
                finally
                {
                    frame.Dispose();
                }

                if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
                {
                    var diag = ndiOutput.Diagnostics;
                    Console.WriteLine(
                        $"pos={videoSource.PositionSeconds:0.###}s frame={videoSource.CurrentFrameIndex} | " +
                        $"pushed={pushed} failed={failed} | ndi: vOk={diag.VideoPushSuccesses} vFail={diag.VideoPushFailures}");
                    lastStatus = DateTime.UtcNow;
                }

                Thread.Sleep(delayMs);
            }

            _ = ndiOutput.Stop();
            _ = videoSource.Stop();
            _ = engine.Terminate();

            Console.WriteLine($"Done. Pushed={pushed} frames, failed={failed}.");
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
        Console.WriteLine("NDISendTest — video file → NDI output");
        Console.WriteLine("Usage: NDISendTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>           Input file path");
        Console.WriteLine("  --sender-name <name>     NDI sender name (default: MFPlayer NDISendTest)");
        Console.WriteLine("  --seconds <n>            Send duration (default: 60)");
    }
}

