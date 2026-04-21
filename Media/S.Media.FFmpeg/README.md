# S.Media.FFmpeg

FFmpeg-backed decoder and channel adapters for `S.Media.Core`.

- `FFmpegDecoder` — opens a container (file or `System.IO.Stream` via
  `StreamAvioContext`) and exposes `IAudioChannel` / `IVideoChannel` per stream.
- `FFmpegAudioChannel` / `FFmpegVideoChannel` — pull-mode ring-buffered channels that
  advertise stream PTS through `Position` / `NextExpectedPts` so the router can
  compute A/V drift in a single time domain.
- `MediaPlayer` — thin convenience façade that opens a file, wires inputs/endpoints
  through an `AVRouter`, and exposes play/pause/seek/stop with playback events.
- `FFmpegLoader` — one-time init for the native libraries.
  Use `FFmpegLoader.ResolveDefaultSearchPath()` (reads `MFPLAYER_FFMPEG_PATH` env
  var, then picks OS defaults) instead of hard-coding `ffmpeg.RootPath`.

The low-level decode worker (`FFmpegDecodeWorkers.RunAsync<T>`) is generic over
`IDecodableChannel` so audio and video share one seek-epoch-aware loop.

