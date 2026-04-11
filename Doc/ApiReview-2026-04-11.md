# API & Implementation Review — 2026-04-11

Scope: Full review of `S.Media.Core`, `S.Media.FFmpeg`, `S.Media.NDI`, `S.Media.SDL3`,
`S.Media.Avalonia`, `S.Media.PortAudio`, and all test consumers.

Methodology: Read every relevant source file, cross-check plan status markers against actual
code, then catalogue ergonomics/correctness/simplification opportunities.

---

## Part 1 — Plan Status Accuracy

### VideoAccelerationPlan-2026-04-10.md

| Phase | Marked | Actual | Verdict |
|-------|--------|--------|---------|
| 0 – Baseline Stabilization | done ✅ | All items confirmed in code | **Accurate** |
| 1 – Auto Hardware Decode | in-progress, all [x] | `PreferHardwareDecoding`, auto-probe, `--sw`, diagnostics snapshot all present | **Accurate** |
| 2 – Multi-Sink Format Efficiency | in-progress, all [x] | `IVideoSinkFormatPreference`, `IVideoSinkFormatCapabilities`, hit/miss counters, NDI sink formats all confirmed | **Accurate** |
| 3 – CPU Conversion Backend | [~] | `LibYuvRuntime`, converters, fallback, benchmark project all present; libyuv native lib not yet bundled/tested | **Accurate** |
| 4 – GPU YUV Shader Path | [~] | `GLRenderer` (SDL3) has working NV12, YUV420P, and YUV422P10 shaders with range/matrix toggles; `LocalVideoOutputRoutingPolicy` routes to them. All four bullets are **functionally complete** for SDL3. | **Under-reported** — items should be `[x]` not `[~]` |
| 5 – Zero-Copy / Advanced Interop | scaffold only | No hw-frame interop code exists; doc scaffold confirmed | **Accurate** |
| 6 – API Simplification | mixed [x]/[~] | `IAVMixer`/`AVMixer`, shared shaders, endpoint adapters, presets, cloning all confirmed. "Migrate sample apps" is genuinely partial — apps use `AVMixer` but still reach through `.Mixer` directly for stats. | **Accurate** |

### VideoMixerEvolutionPlan-2026-04-10.md

| Phase | Marked | Actual | Verdict |
|-------|--------|--------|---------|
| A – Foundation | ✅ | Multi-target routing, format prefs, `IAVMixer` confirmed | **Accurate** |
| B – Mixer decoupling | all [ ] (next) | `PreferRawFramePassthrough` + `VideoSinkEndpointAdapter` ARE a partial implementation of "move conversion to endpoint boundary" for sinks. This work is done but not recorded. | **Partially inaccurate — first two bullets should be [~]** |
| C – Endpoint unification | [x][x][ ] | Push/pull endpoint interfaces and all adapters confirmed; "decide long-term API: keep both or converge" is not yet decided | **Accurate** |
| D – AV router power features | [~][x][x] | Many-to-many video routing works; audio routing also works. What's missing is shared-clock linkage between AV outputs and compositing/blend layers | **Accurate** |
| E – Cloning and profiles | all [x] | `AvaloniaOpenGlVideoCloneSink`, `NdiEndpointPreset`, diagnostics snapshots all confirmed | **Accurate** |

---

## Part 2 — Issues, Findings, and Recommended Improvements

The findings are grouped by severity/category.

---

### 2.1 Mixer Still Performs Conversion for the Leader Path

**File:** `Media/S.Media.Core/Video/VideoMixer.cs`

`VideoMixer` accepts a `VideoFormat outputFormat` in its constructor and calls `_pixelConverter.Convert(raw, outputPixelFormat)` in `PullAndConvert()` for the **leader** target. While sinks can opt out via `PreferRawFramePassthrough`, the leader (the primary render output) has no equivalent escape hatch — conversion always happens inside the mixer for it.

`SDL3VideoOutput` works around this by using `LocalVideoOutputRoutingPolicy` to select a YUV pixel format that the GL renderer supports natively, so no conversion is needed. But this is a workaround, not true decoupling: the mixer still holds a `IPixelFormatConverter` and the frame must still pass through `PullAndConvert`.

**Goal conflict:** Plan goal is "Mixer core is not tied to fixed audio/video output formats."  
**Reality:** `VideoMixer(VideoFormat outputFormat)` is still the constructor signature.

**Recommended fix:**  
Extend the leader path with the same `PreferRawFramePassthrough` mechanism used by sinks. Add a `LeaderPreferRawPassthrough` property (or remove `outputFormat`'s pixel-format coupling entirely) so the render output endpoint owns all conversion decisions. This also removes the `_pixelConverter` dependency from `VideoMixer`.

---

### 2.2 Output.Mixer Property Couples Mixer to Output

**Files:** `IVideoOutput.cs`, `IAudioOutput.cs`, `SDL3VideoOutput.cs`, `PortAudioOutput.cs`, `AvaloniaOpenGlVideoOutput.cs`

`IVideoOutput.Mixer` and `IAudioOutput.Mixer` expose the mixer as a direct property. This means:
- The output creates and owns its mixer.
- Callers must reach through the output to configure routing.
- `AVMixer` must accept the output's pre-created mixer instead of creating its own.

This also causes awkward construction in test apps:
```csharp
// Current (anti-pattern: mixer is locked to the output)
using var avMixer = new AVMixer(
    new AudioMixer(new AudioFormat(48000, 2)),
    videoOutput.Mixer,          // ← reaching into the output
    ownsAudio: true,
    ownsVideo: false);
```

**Recommended fix:**  
Remove `Mixer` from `IVideoOutput` and `IAudioOutput`. Outputs become pure endpoint objects (clock + render surface). `AVMixer` creates and owns the mixers. Outputs register themselves with the mixer as sinks/endpoints. Example target API:

```csharp
// Target ergonomics
var avMixer = AVMixer.Create(audioFormat, videoFormat);
avMixer.AddOutput(sdl3VideoOutput);     // output registers itself as a video endpoint
avMixer.AddOutput(portAudioOutput);    // output registers itself as an audio endpoint
avMixer.AddChannel(videoChannel);
await avMixer.StartAsync();
```

---

### 2.3 IVideoSinkFormatPreference Is Superseded and Should Be Removed

**Files:** `IVideoSinkFormatPreference.cs`, `IVideoSinkFormatCapabilities.cs`, `VideoMixer.cs`

`IVideoSinkFormatPreference` (single preferred format) is a strict subset of `IVideoSinkFormatCapabilities` (ordered list). `NDIVideoSink` implements **both**, and `VideoMixer.ResolveSinkPixelFormat` checks `IVideoSinkFormatCapabilities` first, then falls back to `IVideoSinkFormatPreference`. The fallback path is never actually needed.

**Recommended fix:**  
Remove `IVideoSinkFormatPreference` entirely. Any sink that only needs one format uses a single-element `PreferredPixelFormats` list in `IVideoSinkFormatCapabilities`.

---

### 2.4 IVideoColorRangeHint Is an Empty File

**File:** `Media/S.Media.Core/Video/IVideoColorRangeHint.cs`

`IVideoColorRangeHint.cs` is empty. The color range hint was merged into `IVideoColorMatrixHint` (which already has `SuggestedYuvColorRange`). The empty file is leftover dead code.

**Recommended fix:** Delete the file.

---

### 2.5 AudioOutputEndpointAdapter Allocates on Every Resampled Write

**File:** `Media/S.Media.Core/Audio/AudioOutputEndpointAdapter.cs`, line 64

```csharp
var tmp = new float[outSamples]; // ← heap allocation every call
_resampler.Resample(buffer[..srcSamples], tmp, format, _format.SampleRate);
```

When source and target sample rates differ, every `WriteBuffer` call allocates a new float array. This is called from the RT or near-RT path and will cause GC pressure.

**Recommended fix:**  
Pre-allocate a scratch buffer at construction time (sized to the expected max frame count, similar to how `AudioMixer` pre-allocates `_mixBuffer`). Grow lazily using `ArrayPool<float>` if the size changes.

---

### 2.6 Two Parallel ArrayPoolOwner Implementations

**Files:** `Video/S.Media.Avalonia/AvaloniaOpenGlVideoCloneSink.cs`, `Media/S.Media.Core/Video/BasicPixelFormatConverter.cs`

Both define private nested `ArrayPoolOwner<T>` / `ArrayPoolByteOwner` classes with identical semantics. This is duplicated infrastructure.

**Recommended fix:**  
Promote a single `ArrayPoolOwner<T>` (or `ArrayPoolMemoryOwner`) to `S.Media.Core.Media` (e.g. alongside `VideoFrame`), and remove the private copies.

---

### 2.7 AVMixer.ResolveMasterPosition Is Dead API

**File:** `Media/S.Media.Core/Mixing/IAVMixer.cs`, `AVMixer.cs`

`ResolveMasterPosition(TimeSpan audio, TimeSpan video, TimeSpan? external)` is declared, implemented, and... never called by any code. No test app, no output, no clock uses it. It is orphaned.

`AVMixer` also holds a `MasterPolicy` but never acts on it during the mix loop — it's purely for consumers to query. The actual clock used during playback is always the output's own `VideoPtsClock` or `PortAudioClock`.

**Recommended fix:**  
Either wire clock policy enforcement into `AVMixer` properly (i.e. `AVMixer` owns and drives the master clock), or remove `ResolveMasterPosition` from the public interface until it is actually connected to clock management. Having it on the interface implies a contract that doesn't exist.

---

### 2.8 SDL3VideoOutput.Dispose Calls SDL.Quit() Globally

**File:** `Video/S.Media.SDL3/SDL3VideoOutput.cs`, line 406

```csharp
SDL.Quit(); // ← global SDL shutdown!
```

Calling `SDL.Quit()` in `Dispose` globally shuts down SDL. If two `SDL3VideoOutput` instances existed in the same process, disposing the first would break the second. Even if only one window is typical today, this is a latent correctness bug for multi-window scenarios.

**Recommended fix:**  
Reference-count SDL initialization (either a static counter or a shared `SdlContext` singleton with `IDisposable`). `SDL.Quit()` is only called when the ref count drops to zero.

---

### 2.9 VirtualAudioOutput.Open Is a Misleading No-op

**File:** `Media/S.Media.Core/Audio/VirtualAudioOutput.cs`, line 75

```csharp
public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0) { }
```

`VirtualAudioOutput` initialises everything in its constructor. `Open()` silently ignores all parameters, which can confuse callers who expect it to behave like `PortAudioOutput.Open()`.

**Recommended fix:**  
Option A: Remove `VirtualAudioOutput` from `IAudioOutput` and give it its own interface (e.g. `IVirtualAudioOutput` or just a plain class with `StartAsync`/`StopAsync`). This is the cleaner long-term solution.  
Option B: Throw `NotSupportedException` or add an XML doc comment explicitly stating that `Open()` is a no-op for virtual outputs.

---

### 2.10 Duplicate Diagnostic/Parsing Helpers Across Test Apps

**Files:** All three test apps (`MFPlayer.VideoPlayer`, `MFPlayer.VideoMultiOutputPlayer`, `MFPlayer.AvaloniaVideoPlayer`)

Each test app duplicates:
- `ParseNdiPreset()` function
- `ParseYuvColorMatrix()` / `ParseYuvColorRange()` functions  
- The `Fmt(TimeSpan)` formatting helper
- Large blocks of diagnostics stats formatting

**Recommended fix:**  
Extract a shared `MFPlayer.TestCommon` project (or a single `PlayerHelpers.cs` file) with these utilities. This removes ~150+ lines of duplication and makes new sample apps trivial to add.

---

### 2.11 IAVMixer Missing Endpoint-First Registration Methods

**File:** `Media/S.Media.Core/Mixing/IAVMixer.cs`

`IAVMixer` exposes `RegisterVideoSink(IVideoSink)` and `RegisterAudioSink(IAudioSink)` but has no counterpart for the new endpoint types:
- No `RegisterVideoEndpoint(IVideoFrameEndpoint)`
- No `RegisterAudioEndpoint(IAudioBufferEndpoint)`

Callers who want to use the cleaner endpoint API must either manually wrap with `VideoEndpointSinkAdapter` or bypass `IAVMixer`.

**Recommended fix:**  
Add endpoint registration overloads to `IAVMixer` (with the adapter wrapping done internally), so the primary public API is endpoint-first.

---

### 2.12 NDIVideoSink Silently Drops Frames on Format Mismatch

**File:** `NDI/S.Media.NDI/NDIVideoSink.cs`, lines 140–143

```csharp
if (frame.PixelFormat != _targetFormat.PixelFormat)
{
    Interlocked.Increment(ref _formatDrops);
    return; // ← silent drop, no conversion
}
```

When the mixer sends a frame in a format the NDI sink doesn't expect, it drops silently. This can happen if the mixer's format negotiation goes wrong. The `_formatDrops` counter surfaces this in diagnostics, but only if the caller explicitly checks.

**Recommended fix:**  
`NDIVideoSink` should implement its own pixel format conversion as a fallback rather than dropping. It already has `IVideoSinkFormatCapabilities`, so the mixer should never send the wrong format — but defensive conversion (or at minimum, a warning log) would make debugging easier.

---

### 2.13 VideoMixer.IsSupportedSinkTargetFormat Implicitly Excludes YUV

**File:** `Media/S.Media.Core/Video/VideoMixer.cs`, line 428–429

```csharp
private static bool IsSupportedSinkTargetFormat(PixelFormat pf)
    => pf is PixelFormat.Rgba32 or PixelFormat.Bgra32;
```

YUV formats (`Nv12`, `Yuv420p`, `Yuv422p10`) are never passed through by the mixer to sinks via the format-negotiation path — only via `PreferRawFramePassthrough`. This is correct by design (the mixer's converter only handles RGBA↔BGRA and YUV→RGBA), but the constraint is undocumented at the interface level. A sink that declares `PreferredPixelFormats = [PixelFormat.Nv12]` without also setting `PreferRawFramePassthrough = true` will fall back to `Rgba32` silently.

**Recommended fix:**  
Document this constraint in `IVideoSinkFormatCapabilities`. Optionally, rename `PreferRawFramePassthrough` to something more descriptive like `BypassMixerConversion` to make the contract explicit.

---

### 2.14 AvaloniaOpenGlVideoOutput Forces Rgba32 Without YUV Shader Support

**File:** `Video/S.Media.Avalonia/AvaloniaOpenGlVideoOutput.cs`, line 107

```csharp
_outputFormat = format with { PixelFormat = PixelFormat.Rgba32 };
```

Unlike SDL3's `GLRenderer` (which supports NV12, YUV420P, YUV422P10 shaders), the Avalonia `AvaloniaGlRenderer` only supports RGBA32. So conversion happens in the decoder (via `VideoTargetPixelFormat = PixelFormat.Rgba32`) rather than at the GPU. This is a functional limitation, not a bug, but it should be:
1. Documented clearly in `AvaloniaOpenGlVideoOutput` and `AvaloniaGlRenderer`.
2. Listed as a concrete work item in `VideoAccelerationPlan` Phase 4.

---

### 2.15 Missing Cancellation Token Propagation in NDISink Stop

**Files:** `NDI/S.Media.NDI/NDIVideoSink.cs`, `NDI/S.Media.NDI/NDIAudioSink.cs`

```csharp
public Task StopAsync(CancellationToken ct = default)
{
    _running = false;
    _cts?.Cancel();
    _writeThread?.Join(TimeSpan.FromSeconds(3)); // ← blocking join, ignores ct
    return Task.CompletedTask;
}
```

`StopAsync` does a blocking `Thread.Join` up to 3 seconds and ignores the caller's `CancellationToken`. If the write thread hangs, the application will silently wait 3 seconds with no way to abort.

**Recommended fix:**  
`return Task.Run(() => _writeThread?.Join(...), ct)` (similar to how `PortAudioOutput.StopAsync` handles this), or use a `TaskCompletionSource` that the write loop signals on exit.

---

### 2.16 VideoFrame Memory Ownership Is Implicit and Error-Prone

**File:** `Media/S.Media.Core/Media/VideoFrame.cs`

`VideoFrame.MemoryOwner` is a nullable `IDisposable?`. Whether the caller must dispose it is documented in XML comments but not enforced at compile time. Several sinks (`AvaloniaOpenGlVideoCloneSink`, `NDIVideoSink`) copy the data immediately in `ReceiveFrame` — this is correct, but it's easy for new implementations to forget.

**Recommended fix:**  
Consider wrapping in a `ref`-counted smart type (e.g. `VideoFrameLease`) or using `System.Buffers.IMemoryOwner<byte>` consistently so the compiler enforces disposal. Alternatively, keep the current design but add a `[MustDispose]` Roslyn analyzer attribute or runtime debug assertion.

---

## Part 3 — Simplification Opportunities

### 3.1 Collapse IVideoSinkFormatPreference into IVideoSinkFormatCapabilities
(See §2.3 above.) One interface instead of two.

### 3.2 Unify Output and Sink Lifecycle (StartAsync/StopAsync)
`IMediaOutput` and `IVideoSink`/`IAudioSink` both have `StartAsync`/`StopAsync`/`IsRunning`. This is identical and could be a shared `IMediaEndpoint` base:
```csharp
public interface IMediaEndpoint : IDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```
`IMediaOutput`, `IVideoSink`, `IAudioSink`, `IVideoFrameEndpoint`, and `IAudioBufferEndpoint` would all extend this, removing the repeated method declarations.

### 3.3 Replace VideoMixerPullSource + VideoOutputPullSourceAdapter With One Type
Both `VideoMixerPullSource` and `VideoOutputPullSourceAdapter` are thin wrappers that call `IVideoMixer.PresentNextFrame(_clock.Position)`. They are nearly identical in purpose and can be merged into one `VideoFramePullSource(IVideoMixer mixer, IMediaClock clock)`.

### 3.4 Consolidate YUV Shader State Into a Shared YuvShaderConfig Record
Both `GLRenderer` (SDL3) and any future Avalonia YUV renderer need `YuvColorRange` + `YuvColorMatrix`. A simple shared record:
```csharp
public readonly record struct YuvShaderConfig(
    YuvColorRange Range = YuvColorRange.Auto,
    YuvColorMatrix Matrix = YuvColorMatrix.Auto);
```
would let `SDL3VideoOutput` expose one property instead of four (`YuvColorRange`, `YuvColorMatrix`, `Yuv422p10ColorRange`, `Yuv422p10ColorMatrix`). The `Yuv422p10*` aliases are confusing because the range/matrix settings now apply to ALL YUV paths, not just YUV422P10.

### 3.5 AVMixer as the Canonical Entry Point (End-User API)
The current ergonomics require the user to:
1. Create a decoder
2. Create an output (which internally creates a mixer)
3. Wrap output's mixer in an AVMixer
4. Add channels through AVMixer
5. Start output and decoder separately

The ideal ergonomics after decoupling:
```csharp
// 1. Create the mixer (owns clocks, routing, pacing)
var mixer = new AVMixer(audioFormat, videoFormat);

// 2. Add outputs (each output becomes an endpoint)
mixer.AddVideoOutput(new SDL3VideoOutput());
mixer.AddAudioOutput(new PortAudioOutput(audioDevice));
mixer.AddVideoSink(new NDIVideoSink(sender, preset: NdiEndpointPreset.Balanced));

// 3. Add channels
var decoder = FFmpegDecoder.Open(path);
mixer.AddVideoChannel(decoder.VideoChannels[0]);
mixer.AddAudioChannel(decoder.AudioChannels[0], ChannelRouteMap.Identity(2));

// 4. Start everything
await mixer.StartAsync();
```

This requires completing §2.1 (leader format decoupling) and §2.2 (removing Mixer from Output).

---

## Part 4 — Minor / Low-Priority Notes

- `IVideoColorRangeHint.cs` is an empty file — delete it.
- `IVideoMixer` XML doc says "v1" in multiple places — these should be updated to reflect the current maturity.
- `AudioMixer`'s linear-scan route lookup (`SinkRoute[] sinkRoutes`) is fine for ≤10 sinks but would benefit from a dictionary at higher counts.
- `NDIVideoSink` preallocates its buffer pool based on `targetFormat.Width/Height` defaulting to `1280×720` if zero — if frames arrive at a different resolution than the pool was sized for, `_capacityMissDrops` fires. Consider resizing the pool on first frame or making the default configurable.
- `FFmpegDecoderOptions.VideoTargetPixelFormat` defaults to `Bgra32` but the `AvaloniaVideoPlayer` explicitly overrides it to `Rgba32`. A comment explaining why the default is `Bgra32` (SDL3/NDI preference) vs `Rgba32` (Avalonia preference) would help new contributors.
- `PortAudioOutput` uses `_activeMixer ?? _mixer` for the RT callback. The `_activeMixer` field is set by `AggregateOutput.OverrideRtMixer`. This internal coupling between `PortAudioOutput` and `AggregateOutput` via `OverrideRtMixer` is a code smell; `AggregateOutput` tightly coupled to the internal mixer-swapping mechanism of `PortAudioOutput`.

---

## Summary Priority Table

| # | Issue | Severity | Effort |
|---|-------|----------|--------|
| 2.1 | Mixer still converts for leader path | High | Medium |
| 2.2 | `Output.Mixer` property — mixer/output coupling | High | Large |
| 2.3 | Remove `IVideoSinkFormatPreference` | Medium | Small |
| 2.4 | Delete empty `IVideoColorRangeHint.cs` | Low | Trivial |
| 2.5 | `AudioOutputEndpointAdapter` allocation in resample path | Medium | Small |
| 2.6 | Duplicate `ArrayPoolOwner<T>` implementations | Low | Small |
| 2.7 | `AVMixer.ResolveMasterPosition` dead API | Medium | Small |
| 2.8 | `SDL.Quit()` in `SDL3VideoOutput.Dispose` | Medium | Small |
| 2.9 | `VirtualAudioOutput.Open` no-op is misleading | Low | Small |
| 2.10 | Duplicated test app helper code | Low | Small |
| 2.11 | `IAVMixer` missing endpoint registration | Medium | Small |
| 2.12 | `NDIVideoSink` silent frame drops | Low | Small |
| 2.13 | `IsSupportedSinkTargetFormat` implicit YUV exclusion | Low | Trivial (docs) |
| 2.14 | Avalonia YUV shader gap not tracked | Low | Docs + future work |
| 2.15 | NDI sink `StopAsync` blocking join ignores CT | Medium | Small |
| 2.16 | `VideoFrame` ownership implicit | Low | Medium |
| 3.1 | Collapse `IVideoSinkFormatPreference` | Medium | Small |
| 3.2 | `IMediaEndpoint` base interface | Low | Small |
| 3.3 | Merge `VideoMixerPullSource` + `VideoOutputPullSourceAdapter` | Low | Trivial |
| 3.4 | `YuvShaderConfig` record + simplify Output YUV properties | Low | Small |
| 3.5 | `AVMixer` as canonical entry point (full decoupling) | High | Large |

