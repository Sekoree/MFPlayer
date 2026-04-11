using System.Diagnostics;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Core.Benchmarks;

internal static class Program
{
    private const int DefaultIterations = 120;

    private static int Main(string[] args)
    {
        int iterations = ParseIntOption(args, "--iterations", DefaultIterations);
        int width = ParseIntOption(args, "--width", 1920);
        int height = ParseIntOption(args, "--height", 1080);

        if (width <= 0 || height <= 0 || iterations <= 0)
        {
            Console.Error.WriteLine("width/height/iterations must be > 0");
            return 1;
        }

        using var converter = new BasicPixelFormatConverter();

        Console.WriteLine($"[bench] size={width}x{height} iterations={iterations}");

        bool libYuvAvailable = BasicPixelFormatConverter.GetDiagnosticsSnapshot().LibYuvAvailable;

        // Managed-only pass provides deterministic baseline.
        BasicPixelFormatConverter.LibYuvEnabled = false;
        Console.WriteLine("[bench] mode=managed");
        RunSuite(converter, width, height, iterations);

        if (libYuvAvailable)
        {
            BasicPixelFormatConverter.LibYuvEnabled = true;
            Console.WriteLine("[bench] mode=libyuv");
            RunSuite(converter, width, height, iterations);
        }
        else
        {
            Console.WriteLine("[bench] mode=libyuv unavailable (skipped)");
        }

        return 0;
    }

    private static void RunSuite(BasicPixelFormatConverter converter, int width, int height, int iterations)
    {
        RunCase(converter, CreateFrame(width, height, PixelFormat.Bgra32), PixelFormat.Rgba32, iterations, "BGRA->RGBA");
        RunCase(converter, CreateFrame(width, height, PixelFormat.Rgba32), PixelFormat.Bgra32, iterations, "RGBA->BGRA");
        RunCase(converter, CreateFrame(width, height, PixelFormat.Nv12), PixelFormat.Rgba32, iterations, "NV12->RGBA");
        RunCase(converter, CreateFrame(width, height, PixelFormat.Yuv420p), PixelFormat.Rgba32, iterations, "YUV420P->RGBA");
        RunCase(converter, CreateFrame(width, height, PixelFormat.Uyvy422), PixelFormat.Bgra32, iterations, "UYVY->BGRA");
    }

    private static void RunCase(BasicPixelFormatConverter converter, VideoFrame source, PixelFormat dst, int iterations, string label)
    {
        // Warmup
        using (var warm = converter.Convert(source, dst).MemoryOwner)
        {
        }

        long bytesPerFrame = source.Width * (long)source.Height * 4;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var converted = converter.Convert(source, dst);
            converted.MemoryOwner?.Dispose();
        }

        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds;
        double fps = iterations / sw.Elapsed.TotalSeconds;
        double gbps = (bytesPerFrame * iterations) / Math.Max(1e-9, sw.Elapsed.TotalSeconds) / (1024d * 1024d * 1024d);
        Console.WriteLine($"[bench] {label,-14} {ms,9:F1} ms total  {fps,8:F1} fps  {gbps,6:F2} GiB/s");
    }

    private static VideoFrame CreateFrame(int width, int height, PixelFormat pixelFormat)
    {
        var rng = new Random(1234);

        int bytes = pixelFormat switch
        {
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => width * height * 4,
            PixelFormat.Nv12 => width * height + width * ((height + 1) / 2),
            PixelFormat.Yuv420p =>
                width * height +
                ((width + 1) / 2) * ((height + 1) / 2) +
                ((width + 1) / 2) * ((height + 1) / 2),
            PixelFormat.Uyvy422 => width * height * 2,
            _ => width * height * 4
        };

        byte[] data = new byte[bytes];
        rng.NextBytes(data);
        return new VideoFrame(width, height, pixelFormat, data, TimeSpan.Zero);
    }

    private static int ParseIntOption(string[] args, string option, int defaultValue)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int parsed))
                return parsed;

            if (args[i].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i][(option.Length + 1)..], out parsed))
                return parsed;
        }

        return defaultValue;
    }
}

