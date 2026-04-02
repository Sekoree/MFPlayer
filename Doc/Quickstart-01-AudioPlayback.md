# Quickstart 01 — Audio Playback

> FFmpeg decoder → PortAudio output

This guide shows the minimal code to decode an audio file with FFmpeg and play it through a PortAudio output device.

---

## Prerequisites

| Package | Purpose |
|---------|---------|
| `S.Media.FFmpeg` | FFmpeg-based decoding |
| `S.Media.PortAudio` | PortAudio audio output engine |
| `S.Media.Core` | Shared types (`AudioFrame`, `MediaResult`, etc.) |

Native libraries **FFmpeg** and **PortAudio** must be loadable at runtime.

---

## Full Example

```csharp
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.PortAudio.Engine;

// 1. Ensure FFmpeg native libraries are loaded.
FFmpegRuntime.EnsureInitialized();

// 2. Open a media file using the non-throwing Create() factory.
var code = FFmpegMediaItem.Create("file:///path/to/song.flac", out var media);
if (code != MediaResult.Success)
    throw new InvalidOperationException($"Open failed: {code}");
using var _ = media!;

// 3. Get the audio source and start decoding.
var source = media.AudioSource
    ?? throw new InvalidOperationException("No audio stream found.");
source.Start();

// 4. Initialize PortAudio engine → create output → start output.
using var engine = new PortAudioEngine();
engine.Initialize(new AudioEngineConfig());   // default host API
engine.Start();

engine.CreateOutputByIndex(-1, out var output); // -1 = default device
output!.Start(new AudioOutputConfig());

// 5. Read decoded samples and push them to the audio output.
var channels  = Math.Max(1, source.StreamInfo.ChannelCount.GetValueOrDefault(2));
var sampleRate = Math.Max(1, source.StreamInfo.SampleRate.GetValueOrDefault(48_000));
var routeMap  = channels <= 1 ? new[] { 0, 0 } : new[] { 0, 1 };
var buffer    = new float[1024 * channels];

while (true)
{
    var readCode = source.ReadSamples(buffer, 1024, out var framesRead);
    if (readCode != MediaResult.Success || framesRead <= 0)
        break; // end of stream or error

    var frame = new AudioFrame(
        Samples:            buffer,
        FrameCount:         framesRead,
        SourceChannelCount: channels,
        Layout:             AudioFrameLayout.Interleaved,
        SampleRate:         sampleRate,
        PresentationTime:   TimeSpan.FromSeconds(source.PositionSeconds));

    output.PushFrame(in frame, routeMap, channels);
}

// 6. Clean up.
output.Stop();
source.Stop();
engine.Stop();
```

---

## Key Concepts

### `FFmpegMediaItem.Create()`

The `Create()` factory returns an `int` error code (`0` = success) and outputs the media item via an `out` parameter. This is the recommended non-throwing pattern — avoid the removed `Open()` / `TryOpen()` methods.

The media item opens **both audio and video** by default (`OpenAudio = true`, `OpenVideo = true`) with a shared decode context. For audio-only, this still works — `.VideoSource` will simply be `null` if the file has no video.

### Route Map

The `routeMap` array maps **source channels → output channels**. For stereo output:

| Source | Route Map | Effect |
|--------|-----------|--------|
| Mono (1ch) | `{ 0, 0 }` | Source ch 0 → both L and R |
| Stereo (2ch) | `{ 0, 1 }` | Source ch 0 → L, ch 1 → R |

### `AudioStreamInfo` Nullables

`ChannelCount` and `SampleRate` on `AudioStreamInfo` are nullable — they may not be known until the first packet is decoded. Use `GetValueOrDefault()` with sensible fallbacks.

### Dispose Ownership

`FFmpegMediaItem` disposes its own `AudioSource` and `VideoSource` when disposed. Do **not** independently dispose `media.AudioSource` — just dispose the media item.

---

## See Also

- `Test/SimpleAudioTest/Program.cs` — the working test app this guide is based on
- `Test/TestShared/TestHelpers.cs` — `InitAudioOutput()` helper used by all test apps

