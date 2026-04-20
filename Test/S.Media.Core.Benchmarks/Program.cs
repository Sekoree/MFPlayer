using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
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
        int audioFrames = ParseIntOption(args, "--audio-frames", 512);

        if (width <= 0 || height <= 0 || iterations <= 0 || audioFrames <= 0)
        {
            Console.Error.WriteLine("width/height/iterations/audio-frames must be > 0");
            return 1;
        }

        using var converter = new BasicPixelFormatConverter();

        Console.WriteLine($"[bench] size={width}x{height} iterations={iterations}");

        bool libYuvAvailable = converter.GetDiagnosticsSnapshot().LibYuvAvailable;

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

        // TODO: Audio router benchmarks (AVRouter-based) - pending new API stabilization

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
        using (converter.Convert(source, dst).MemoryOwner)
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

    private sealed class ConstantAudioChannel : IAudioChannel
    {
        private readonly float _value;
        public ConstantAudioChannel(AudioFormat format, float value)
        {
            SourceFormat = format;
            _value = value;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat { get; }
        public bool IsOpen => true;
        public bool CanSeek => false;
        public float Volume { get; set; } = 1.0f;
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 1;
        public int BufferAvailable => int.MaxValue;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { }
            remove { }
        }

        public event EventHandler? EndOfStream
        {
            add { }
            remove { }
        }

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest.Fill(_value);
            return frameCount;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class NullAudioSink : IAudioEndpoint
    {
        public string Name => nameof(NullAudioSink);
        public bool IsRunning => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat) { }
        public void Dispose() { }
    }
}

