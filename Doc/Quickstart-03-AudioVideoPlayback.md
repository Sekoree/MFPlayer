# Quickstart 03 — Audio + Video Playback

> FFmpeg decoder → AVMixer / MediaPlayer → PortAudio + SDL3

This guide shows two approaches for synchronized A/V playback:

1. **`MediaPlayer`** — high-level one-liner (recommended for most cases)
2. **`AVMixer`** — lower-level control over sources, sync modes, and routing

Both connect to the same output stack: **PortAudio** for audio and **SDL3** for video.

---

## Prerequisites

| Package | Purpose |
|---------|---------|
| `S.Media.FFmpeg` | FFmpeg-based decoding |
| `S.Media.PortAudio` | PortAudio audio output engine |
| `S.Media.OpenGL.SDL3` | SDL3/OpenGL video output |
| `S.Media.Core` | Shared types, `AVMixer`, `MediaPlayer` |

Native libraries **FFmpeg**, **PortAudio**, and **SDL3** must be loadable at runtime.

---

## Approach 1: MediaPlayer (Recommended)

`MediaPlayer` wraps `AVMixer` and handles source attachment, starting, and the pump loop internally. You just call `Play(media)`.

```csharp
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

// 1. Initialize runtimes.
FFmpegRuntime.EnsureInitialized();

// 2. Open media (audio + video via shared decode context).
var code = FFmpegMediaItem.Create("file:///path/to/movie.mp4", out var media);
if (code != MediaResult.Success)
    throw new InvalidOperationException($"Open failed: {code}");
using var _ = media!;

// 3. Set up audio output.
using var audioEngine = new PortAudioEngine();
audioEngine.Initialize(new AudioEngineConfig());
audioEngine.Start();
audioEngine.CreateOutputByIndex(-1, out var audioOutput);
audioOutput!.Start(new AudioOutputConfig());

// 4. Set up video output.
var view = new SDL3VideoView();
view.Initialize(new SDL3VideoViewOptions
{
    Width = 1280, Height = 720,
    WindowTitle   = "A/V Player",
    WindowFlags   = SDL.WindowFlags.Resizable,
    ShowOnInitialize  = true,
    PreserveAspectRatio = true,
});
view.Start(new VideoOutputConfig());

// 5. Create MediaPlayer, attach outputs, configure routing.
var player = new MediaPlayer();
player.AddAudioOutput(audioOutput);
player.AddVideoOutput(view);

var channels = Math.Max(1,
    media.AudioSource?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2);
player.PlaybackConfig = AVMixerConfig.ForSourceToStereo(channels);

// 6. Play! Sources are started and pump threads launched automatically.
var playCode = player.Play(media);
if (playCode != MediaResult.Success)
    throw new InvalidOperationException($"Play failed: {playCode}");

Console.WriteLine("Playing… press Ctrl+C to stop.");

// 7. Wait for desired duration (or user interrupt).
Thread.Sleep(TimeSpan.FromSeconds(30));

// 8. Clean up.
player.StopPlayback();
audioOutput.Stop();
audioOutput.Dispose();
view.Stop();
view.Dispose();
audioEngine.Stop();
```

---

## Approach 2: AVMixer (Manual Control)

Use `AVMixer` directly when you need fine-grained control over sync modes, source attachment timing, or custom routing.

```csharp
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

// 1–4: Same as above (init runtimes, open media, create outputs).
FFmpegRuntime.EnsureInitialized();

var code = FFmpegMediaItem.Create("file:///path/to/movie.mp4", out var media);
if (code != MediaResult.Success)
    throw new InvalidOperationException($"Open failed: {code}");
using var _ = media!;

var audioSource = media.AudioSource
    ?? throw new InvalidOperationException("No audio stream.");
var videoSource = media.VideoSource
    ?? throw new InvalidOperationException("No video stream.");

// Start sources manually.
audioSource.Start();
videoSource.Start();

// Set up outputs (same as Approach 1 — abbreviated here).
using var audioEngine = new PortAudioEngine();
audioEngine.Initialize(new AudioEngineConfig());
audioEngine.Start();
audioEngine.CreateOutputByIndex(-1, out var audioOutput);
audioOutput!.Start(new AudioOutputConfig());

var view = new SDL3VideoView();
view.Initialize(new SDL3VideoViewOptions
{
    Width = 1280, Height = 720,
    WindowTitle = "AVMixer Direct",
    WindowFlags = SDL.WindowFlags.Resizable,
    ShowOnInitialize = true,
    PreserveAspectRatio = true,
});
view.Start(new VideoOutputConfig());

// 5. Create AVMixer, attach sources and outputs.
var mixer = new AVMixer();
mixer.AddAudioSource(audioSource);
mixer.AddVideoSource(videoSource);
mixer.SetActiveVideoSource(videoSource);

mixer.AddAudioOutput(audioOutput);
mixer.AddVideoOutput(view);

// 6. Build config with explicit routing.
var channels = Math.Max(1, audioSource.StreamInfo.ChannelCount.GetValueOrDefault(2));
var config = new AVMixerConfig
{
    SourceChannelCount = channels,
    RouteMap           = channels == 1 ? [0, 0] : [0, 1],
    SyncMode           = AVSyncMode.AudioLed,     // audio is the master clock
    OutputSampleRate   = audioSource.StreamInfo.SampleRate.GetValueOrDefault(0),
};

// 7. Start playback — launches internal audio pump and video push threads.
mixer.StartPlayback(config);

Console.WriteLine("Playing… press Ctrl+C to stop.");
Thread.Sleep(TimeSpan.FromSeconds(30));

// 8. Clean up.
mixer.StopPlayback();
audioOutput.Stop();
audioOutput.Dispose();
view.Stop();
view.Dispose();
audioSource.Stop();
videoSource.Stop();
audioEngine.Stop();
```

---

## Key Concepts

### `MediaPlayer` vs `AVMixer`

| | `MediaPlayer` | `AVMixer` |
|---|---|---|
| Source attachment | Automatic via `Play(media)` | Manual: `AddAudioSource()`, `AddVideoSource()`, `SetActiveVideoSource()` |
| Source start/stop | Automatic | Manual: call `source.Start()` / `source.Stop()` yourself |
| Config | `player.PlaybackConfig = ...` set before `Play()` | Passed to `mixer.StartPlayback(config)` |
| Best for | Simple "open and play" | Custom sync modes, multi-source mixing, diagnostics |

### `AVMixerConfig`

| Property | Purpose |
|----------|---------|
| `SourceChannelCount` | Number of channels in the audio source |
| `RouteMap` | Channel mapping array (see Quickstart 01) |
| `SyncMode` | `AudioLed` (audio master), `Realtime` (wall clock), `Synced` (strict A/V lock) |
| `OutputSampleRate` | Target sample rate (0 = auto-detect from engine) |

The convenience factory `AVMixerConfig.ForSourceToStereo(channels)` builds a config with sensible defaults for stereo output.

### Sync Modes

| Mode | Description |
|------|-------------|
| `AudioLed` | Audio drives the clock. Video drops/repeats frames to stay in sync. **Best for media files.** |
| `Realtime` | Wall clock drives both. Good for live sources. |
| `Synced` | Strict A/V lock with drift correction. |

### Dispose Ownership

- `FFmpegMediaItem` disposes its own `AudioSource` and `VideoSource`.
- `MediaPlayer` does **not** dispose the media item — the caller retains ownership.
- Always dispose outputs and engines explicitly after stopping.

---

## See Also

- `Test/MediaPlayerTest/Program.cs` — MediaPlayer approach with diagnostics
- `Test/AVMixerTest/Program.cs` — AVMixer approach with sync-mode selection
- `Test/TestShared/TestHelpers.cs` — shared `InitAudioOutput()` and `InitVideoView()` helpers

