# S.Media Test Applications Plan

> 9 end-to-end test applications that exercise the full S.Media API surface.

Created: 2026-03-26

## Test Video Files

| Alias | Path | Notes |
|-------|------|-------|
| **Heavy** | `/home/seko/Videos/おねがいダーリン_0611.mov` | 4K60 ProRes YUV422P10LE — stress-test |
| **Light** | `/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4` | Lighter h264/h265 |

All apps accept `--input <path>` (or use env `SMEDIA_TEST_INPUT` as fallback).
Heavy/light selection is up to the user at runtime.

---

## 1. SimpleAudioTest

**Goal**: Decode audio from a file → PortAudio output.

**Pipeline**: `FFMediaItem.Open(uri)` → `AudioSource.Start()` → `PortAudioEngine` → `CreateOutputByIndex` → read loop (`ReadSamples` → `PushFrame`).

**Features**:
- `--list-devices` / `--list-host-apis` to browse audio hardware
- `--host-api <id>` and `--device-index <n>` to select output
- `--seconds <n>` playback duration (default 10)
- Uses the new `FFMediaItem.Open()` convenience factory

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.PortAudio

---

## 2. SimpleVideoTest

**Goal**: Decode video from a file → SDL3 standalone window.

**Pipeline**: `FFMediaItem.Open(uri)` (video only) → `VideoSource.Start()` → `SDL3VideoView.Initialize` → read loop (`ReadFrame` → `PushFrame`).

**Features**:
- Standalone SDL3 window (1280×720, resizable)
- Frame-rate-aware delay between pushes
- `--seconds <n>` playback duration (default 10)
- Ctrl+C to stop

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.OpenGL.SDL3

---

## 3. AudioMixerTest

**Goal**: Play 2+ audio files sequentially using `AudioMixer` with source offset.

**Pipeline**: Open both media items → create audio sources → `AudioMixer.AddSource(source1, 0)` + `AddSource(source2, source1.DurationSeconds)` → PortAudio output → manual read/push loop driven by the mixer clock.

**Features**:
- `--input <path1>` `--input2 <path2>` (defaults to same file played twice with offset)
- Prints mixer position + active source info every second
- `--seconds <n>` total playback duration

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.PortAudio

---

## 4. VideoMixerTest

**Goal**: Play 2 video files sequentially using `VideoMixer` with source timing.

**Pipeline**: Open both media items → create video sources → `VideoMixer.AddSource` → `SetActiveSource` → SDL3 output → read loop that switches active source when first source reaches EOF/duration.

**Features**:
- Two input files, second plays after first
- SDL3 standalone window
- Prints frame position per second

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.OpenGL.SDL3

---

## 5. MediaPlayerTest

**Goal**: A/V playback via the simplified `MediaPlayer` + SDL3.

**Pipeline**: `FFMediaItem.Open(uri)` → `AudioVideoMixer` → `MediaPlayer(mixer)` → add PortAudio output + SDL3 output → `player.Play(media)` → `StartPlayback` + `TickVideoPresentation` loop.

**Features**:
- Full A/V sync via `AudioVideoMixerConfig`
- Ctrl+C to stop, prints debug info every second
- Uses `MediaPlayer.Play(IMediaItem)` one-call API

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.PortAudio, S.Media.OpenGL.SDL3

---

## 6. AVMixerTest

**Goal**: Same as MediaPlayerTest but uses `AudioVideoMixer` directly (no `MediaPlayer` wrapper).

**Pipeline**: `FFMediaItem.Open(uri)` → manually attach audio/video sources to `AudioVideoMixer` → add PortAudio + SDL3 outputs → `StartPlayback` → `TickVideoPresentation` loop.

**Features**:
- Demonstrates the full mixer API without MediaPlayer abstraction
- Debug info every second
- `--sync-mode <stable|hybrid|strict>` option

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.PortAudio, S.Media.OpenGL.SDL3

---

## 7. MultiViewTest

**Goal**: Avalonia app with 4 cloned video outputs — stress test.

**Pipeline**: `FFMediaItem.Open(uri)` (video only) → 4 × `AvaloniaVideoOutput` → 4 × `AvaloniaOpenGLHostControl` in a 2×2 grid → background read loop pushing frames to all 4 outputs.

**Features**:
- Avalonia desktop app with 2×2 grid layout
- Hotkeys: Space=pause, Left/Right=seek, H=HUD toggle, Esc=close
- Status bar with frame position + state
- Tests GL rendering stress with multiple simultaneous outputs

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.OpenGL, S.Media.OpenGL.Avalonia, Avalonia

---

## 8. NDIReceiveTest

**Goal**: Discover NDI source → play via `AudioVideoMixer` + SDL3.

**Pipeline**: `NDIFinder.WaitForSources` → `NDIReceiver.Connect` → `NDIEngine.CreateAudioSource/CreateVideoSource` → `AudioVideoMixer` → add PortAudio + SDL3 outputs → `StartPlayback` + tick loop.

**Features**:
- `--list-sources` to discover and exit
- `--source-name <contains>` to pick a source
- Prints debug + NDI diagnostics per second

**Dependencies**: S.Media.Core, S.Media.NDI, S.Media.PortAudio, S.Media.OpenGL.SDL3, NDILib

---

## 9. NDISendTest

**Goal**: Video file → `AudioVideoMixer` → NDI output (can be consumed by NDI clients).

**Pipeline**: `FFMediaItem.Open(uri)` → attach sources to `AudioVideoMixer` → add `NDIVideoOutput` from `NDIEngine.CreateOutput` → `StartPlayback` + tick loop.

**Features**:
- `--sender-name <name>` (default "MFPlayer NDISendTest")
- Prints push success/failure stats per second
- Ctrl+C to stop

**Dependencies**: S.Media.Core, S.Media.FFmpeg, S.Media.NDI, NDILib

---

## Project Structure

All apps live under `Test/`:
```
Test/
  SimpleAudioTest/
  SimpleVideoTest/
  AudioMixerTest/
  VideoMixerTest/
  MediaPlayerTest/
  AVMixerTest/
  MultiViewTest/         (Avalonia app — WinExe)
  NDIReceiveTest/
  NDISendTest/
```

Console apps use `<OutputType>Exe</OutputType>`, MultiViewTest uses `<OutputType>WinExe</OutputType>`.
All target `net10.0` with `<Nullable>enable</Nullable>`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
All added to the solution under the `Test` solution folder.

---

## Implementation Status

| # | App | Status |
|---|-----|--------|
| 1 | SimpleAudioTest | ✅ Done |
| 2 | SimpleVideoTest | ✅ Done |
| 3 | AudioMixerTest | ✅ Done |
| 4 | VideoMixerTest | ✅ Done |
| 5 | MediaPlayerTest | ✅ Done |
| 6 | AVMixerTest | ✅ Done |
| 7 | MultiViewTest | ✅ Done |
| 8 | NDIReceiveTest | ✅ Done |
| 9 | NDISendTest | ✅ Done |

All 9 apps build successfully (0 warnings, 0 errors). 265 existing tests pass (4 skipped).

