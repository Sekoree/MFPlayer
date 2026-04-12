# S.Media.Core Benchmarks

Lightweight benchmark harness for:

- `BasicPixelFormatConverter` conversion paths
- `AudioMixer.FillOutputBuffer()` fanout scenarios

## Run

```bash
dotnet run --project /home/seko/RiderProjects/MFPlayer/Test/S.Media.Core.Benchmarks/S.Media.Core.Benchmarks.csproj -- --width 1920 --height 1080 --iterations 120
```

Options:
- `--width` frame width (default `1920`)
- `--height` frame height (default `1080`)
- `--iterations` per-case loops (default `120`)
- `--audio-frames` frames/callback for audio mixer suite (default `512`)

The output prints total time, FPS-equivalent throughput, and approximate GiB/s.

The harness runs two passes:
- `mode=managed` (forces managed fallback paths)
- `mode=libyuv` (only when libyuv is available at runtime)

Then it runs an audio mixer suite:
- `mode=audio-mixer` (reports microseconds per callback across channel/sink fanout sizes)

