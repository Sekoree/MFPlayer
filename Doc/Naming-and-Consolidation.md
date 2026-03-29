# Naming & Consolidation Review

> **Date:** March 28, 2026
> **Scope:** All projects — naming conventions, redundant classes, structural tidiness
> **These are mostly breaking renames. Safe to do in an API-breaking release branch.**

---

## Table of Contents

1. [Classes to Fold In / Remove](#1-classes-to-fold-in--remove)
2. [Names That Should Change](#2-names-that-should-change)
3. [File / Type Name Mismatches](#3-file--type-name-mismatches)
4. [OSCLib Structural Issues](#4-osclib-structural-issues)
5. [Structural Issues in S.Media.\*](#5-structural-issues-in-smedia)
6. [Summary Table](#6-summary-table)

> **Update — March 28, 2026 (pass 2):** Sections §1.7, §2.12–2.17, §3.4–3.5, §5.3 (revised), §5.5–5.7 added after full source review.

---

## 1. Classes to Fold In / Remove

### 1.1 `NDIRuntimeScope` → fold into `NDIRuntime`

> *The user specifically called this one out.*

`NDIRuntimeScope` is a pure RAII wrapper that calls `NDIRuntime.Initialize()` on construction and `NDIRuntime.Destroy()` on disposal — nothing more. Meanwhile `NDIRuntime` is a static helper that cannot be instantiated or disposed. Having two separate types for one concept ("the NDI SDK runtime lifetime") is unnecessary.

**Current state:**

```csharp
public static class NDIRuntime          // static — can't be used with `using`
public sealed class NDIRuntimeScope     // thin RAII wrapper — the only way to get a lifetime

using var scope = new NDIRuntimeScope();  // throws on failure
```

**Fix:** Convert `NDIRuntime` into a sealed instantiable class that is itself `IDisposable`. Delete `NDIRuntimeScope`.

```csharp
// NDILib/NDIRuntime.cs — replace both types
public sealed class NDIRuntime : IDisposable
{
    private bool _disposed;

    // Static queries — safe to call without an instance
    public static string Version
        => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;
    public static bool IsSupportedCpu()
        => Native.NDIlib_is_supported_CPU();

    // Factory — replaces new NDIRuntimeScope() without throwing
    public static int Create(out NDIRuntime? runtime)
    {
        runtime = null;
        if (!Native.NDIlib_initialize())
            return (int)NDIErrorCode.NDIRuntimeInitFailed;
        runtime = new NDIRuntime();
        return MediaResult.Success;
    }

    private NDIRuntime() { }

    public void Dispose()
    {
        if (_disposed) return;
        Native.NDIlib_destroy();
        _disposed = true;
    }
}
```

**Migration:**

```csharp
// BEFORE:
using var scope = new NDIRuntimeScope();    // throws on failure

// AFTER:
if (NDIRuntime.Create(out var runtime) is var r and not MediaResult.Success)
    return r;
using (runtime) { ... }
```

The static `Version` and `IsSupportedCpu()` keep their existing signatures — callers don't need to create an instance to query them.

---

### 1.2 `MixerClockTypeRules` → static method on `AudioVideoMixerConfig`

`MixerClockTypeRules` is 18 lines with exactly one method:

```csharp
public static class MixerClockTypeRules
{
    public static int ValidateClockType(ClockType clockType) { ... }
}
```

This is too small to be its own type. The single method is only called inside `AudioVideoMixerConfig` and `AudioVideoMixer`. It belongs as a private static helper there, or as an extension method on `ClockType`.

**Fix:** Move to `AudioVideoMixerConfig` as a `private static` method and delete `MixerClockTypeRules.cs`:

```csharp
// Inside AudioVideoMixerConfig:
private static int ValidateClockType(ClockType clockType) => clockType switch
{
    ClockType.External => MediaResult.Success,
    ClockType.Hybrid   => MediaResult.Success,
    _                  => (int)MediaErrorCode.MixerClockTypeInvalid,
};
```

---

### 1.3 `AudioSourceErrorEventArgs` + `VideoSourceErrorEventArgs` → one unified type

Both types are structurally identical — same three properties, same constructor signature:

```csharp
// AudioSourceErrorEventArgs:
public Guid SourceId { get; }
public int ErrorCode { get; }
public string? Message { get; }

// VideoSourceErrorEventArgs:  (line-for-line the same)
public Guid SourceId { get; }
public int ErrorCode { get; }
public string? Message { get; }
```

**Fix:** Replace both with a single `MediaSourceErrorEventArgs`:

```csharp
// S.Media.Core/Mixing/MediaSourceErrorEventArgs.cs  (new, replaces both files)
public sealed class MediaSourceErrorEventArgs : EventArgs
{
    public MediaSourceErrorEventArgs(Guid sourceId, int errorCode, string? message)
    {
        SourceId  = sourceId;
        ErrorCode = errorCode;
        Message   = message;
    }

    public Guid   SourceId  { get; }
    public int    ErrorCode { get; }
    public string? Message  { get; }
}
```

Update the mixer event signatures:

```csharp
// IAudioVideoMixer:
event EventHandler<MediaSourceErrorEventArgs>? AudioSourceError;
event EventHandler<MediaSourceErrorEventArgs>? VideoSourceError;
```

**Migration:** Rename `AudioSourceErrorEventArgs` and `VideoSourceErrorEventArgs` at all call sites. A global find/replace covers this.

---

### 1.4 `MediaErrorAllocations` — remove the int-shortcut properties

`MediaErrorAllocations` has two distinct responsibilities:
1. **Range objects** — `GenericCommon`, `Playback`, `NDIActiveNearTerm`, etc. (`ErrorCodeAllocationRange` values). These are genuinely useful for range-checking.
2. **Individual int shortcuts** — ~30 properties like `public static int MediaConcurrentOperationViolation => (int)MediaErrorCode.MediaConcurrentOperationViolation`. These are trivial casts that add nothing; callers can write `(int)MediaErrorCode.MediaConcurrentOperationViolation` directly.

The int shortcuts bloat the class and create a false impression that `MediaErrorAllocations` is the right place to look up error codes (the enum is).

**Fix:** Remove all ~30 `public static int` properties. Keep only the `ErrorCodeAllocationRange` properties and the class methods:

```csharp
public static class MediaErrorAllocations
{
    // KEEP — range objects:
    public static ErrorCodeAllocationRange GenericCommon { get; } = new(0, 999, ...);
    public static ErrorCodeAllocationRange Playback      { get; } = new(1000, 1999, ...);
    // ...all ranges...

    // DELETE — all the int shortcuts like:
    // public static int MediaConcurrentOperationViolation => (int)MediaErrorCode...
    // public static int MixerDetachStepFailed => ...
    // (etc.)
}
```

---

### 1.5 `OSCArgs` → remove, use `OSCArgument` factories directly

`OSCArgs` is a static class of thin forwarding wrappers with abbreviated method names:

```csharp
public static class OSCArgs
{
    public static OSCArgument I32(int value)    => OSCArgument.Int32(value);
    public static OSCArgument F32(float value)  => OSCArgument.Float32(value);
    public static OSCArgument Str(string value) => OSCArgument.String(value);
    // ... etc.
}
```

- The abbreviations (`I32`, `F32`, `Str`) are inconsistent with the rest of the codebase (which uses full names everywhere).
- Every method is a one-liner redirect — no logic, no added value.
- Having `OSCArgs.I32(v)` and `OSCArgument.Int32(v)` side-by-side creates confusion about which to use.

**Fix:** Delete `OSCArgs.cs`. Update all call sites to use `OSCArgument.Int32(v)`, `OSCArgument.Float32(v)`, `OSCArgument.String(v)`, etc. directly.

If shorthand is genuinely desired for compact A/V automation code, add them as extension methods on `int`, `float`, `string` instead of a static class:

```csharp
// Optional — only if shorthand is needed:
public static class OSCArgExtensions
{
    public static OSCArgument ToOSCArg(this int v)    => OSCArgument.Int32(v);
    public static OSCArgument ToOSCArg(this float v)  => OSCArgument.Float32(v);
    public static OSCArgument ToOSCArg(this string v) => OSCArgument.String(v);
}
// usage: 42.ToOSCArg(), 1.0f.ToOSCArg()
```

---

### 1.6 `FFStreamDescriptor` → make `internal`

`FFStreamDescriptor` is a public struct that describes a single FFmpeg stream (index, codec, duration, sample rate, channel count, width, height, frame rate). But `S.Media.Core` already has `AudioStreamInfo` and `VideoStreamInfo` for exactly this purpose at the public API surface.

`FFStreamDescriptor` leaks FFmpeg-layer plumbing into the public API. Callers accessing `FFSharedDecodeContext.AudioStream` get back an `FFStreamDescriptor`, then have to manually map it to `AudioStreamInfo`.

**Fix:**
1. Make `FFStreamDescriptor` `internal`.
2. Have `FFSharedDecodeContext` expose `AudioStreamInfo?` and `VideoStreamInfo?` properties that are populated from the `FFStreamDescriptor` internally.
3. Remove any public references to `FFStreamDescriptor`.

---

### 1.7 `AudioSourceState` / `VideoSourceState` / `AudioOutputState` → two shared enums

All three enums are structurally identical:

```csharp
public enum AudioSourceState  { Stopped = 0, Running = 1 }
public enum VideoSourceState  { Stopped = 0, Running = 1 }  // identical
public enum AudioOutputState  { Stopped = 0, Running = 1 }  // identical
```

There is no semantic difference between being in the `Running` state as an audio source vs. a video source — they represent the same lifecycle concept. Three separate types for the same two-value state adds friction without value (callers must import or cast between them; shared utilities cannot handle them uniformly).

**Fix:** Replace with two shared enums — one for sources, one for outputs — or a single shared enum if the distinction is not needed:

```csharp
// Option A — two enums (clear source-vs-output split):
public enum SourceState  { Stopped = 0, Running = 1 }
public enum OutputState  { Stopped = 0, Running = 1 }

// IAudioSource:  SourceState State { get; }
// IVideoSource:  SourceState State { get; }
// IAudioOutput:  OutputState State { get; }

// Option B — single enum (simplest):
public enum MediaComponentState { Stopped = 0, Running = 1 }
```

`AudioVideoMixerState` has a third value (`Paused`) so it stays separate regardless.

---

## 2. Names That Should Change

### 2.1 `FF` vs `FFmpeg` prefix — pick one

Two naming patterns coexist in `S.Media.FFmpeg`:

| `FF` prefix (short) | `FFmpeg` prefix (full) |
|---|---|
| `FFMediaItem` | `FFmpegOpenOptions` |
| `FFAudioSource` | `FFmpegDecodeOptions` |
| `FFVideoSource` | `FFmpegRuntime` |
| `FFSharedDecodeContext` | `FFmpegConfigValidator` |
| `FFStreamDescriptor` | `FFmpegDecodeSession` (proposed) |
| `FFNativeFormatMapper` | |
| `FFAudioChannelMap` | |
| `FFAudioSourceOptions` | |

The `FF` prefix reads as an obscure abbreviation to anyone unfamiliar with FFmpeg. `FFmpeg` is the well-known name.

**Recommendation:** Standardise on the `FFmpeg` prefix for all public types in `S.Media.FFmpeg`:

| Current | Proposed |
|---|---|
| `FFMediaItem` | `FFmpegMediaItem` |
| `FFAudioSource` | `FFmpegAudioSource` |
| `FFVideoSource` | `FFmpegVideoSource` |
| `FFSharedDecodeContext` | `FFmpegDecodeSession` (see §2.10) |
| `FFAudioChannelMap` | `FFmpegAudioChannelMap` |
| `FFAudioSourceOptions` | `FFmpegAudioSourceOptions` |
| `FFNativeFormatMapper` | `FFmpegFormatMapper` (internal anyway) |

Internal-only types (`FFStreamDescriptor`, `FFNativeFormatMapper`) can keep the `FF` prefix since they're not part of the public API after §1.6.

---

### 2.2 `NDIVideoOutput` → `NDIOutput`

After implementing `IAudioSink` (see `S.Media.NDI.md` §1.1), `NDIVideoOutput` handles both audio and video. The name `NDIVideoOutput` becomes actively misleading — a caller looking for an audio output won't find it.

**Fix:** Rename to `NDIOutput`:

```csharp
// BEFORE:
public sealed class NDIVideoOutput : IVideoOutput, IAudioSink

// AFTER:
public sealed class NDIOutput : IVideoOutput, IAudioSink
```

---

### 2.3 `AudioVideoMixer*` verbose prefix → `AV` shortening

The prefix `AudioVideo` repeats across many public types. It's descriptive but unwieldy, especially in code that references it frequently:

| Current (verbose) | Proposed (shorter) |
|---|---|
| `AudioVideoMixer` | `AVMixer` |
| `IAudioVideoMixer` | `IAVMixer` |
| `AudioVideoMixerConfig` | `AVMixerConfig` |
| `AudioVideoMixerState` | `AVMixerState` |
| `AudioVideoMixerDebugInfo` | `AVMixerDiagnostics` (see §2.17) |
| `AudioVideoMixerStateChangedEventArgs` | `AVMixerStateChangedEventArgs` |
| `AudioVideoSyncMode` | `AVSyncMode` |

**Consideration:** `AV` is a widely recognised abbreviation for audio/video in the broadcast domain. The test programs already use the prefix informally (`AVMixerTest`). `MediaPlayer` and the Avalonia/SDL3 projects already write `mixer.Start...` etc. without the full prefix in practice.

This is a larger rename but a worthwhile one for API ergonomics.

---

### 2.4 `VideoOutputTimestampMonotonicMode` → `VideoTimestampMode`

The current name has four words and the term "Monotonic" in the middle — it reads as "the mode for the output's monotonic timestamp behaviour". The options (`Passthrough`, `ClampForward`, `RebaseOnDiscontinuity`) are self-explanatory without the word "Monotonic".

```csharp
// BEFORE:
public enum VideoOutputTimestampMonotonicMode { Passthrough, ClampForward, RebaseOnDiscontinuity }

// AFTER:
public enum VideoTimestampMode { Passthrough, ClampForward, RebaseOnDiscontinuity }
```

Property rename in `VideoOutputConfig`:

```csharp
// BEFORE:
public VideoOutputTimestampMonotonicMode TimestampMonotonicMode { get; init; }

// AFTER:
public VideoTimestampMode TimestampMode { get; init; }
```

---

### 2.5 `VideoPresenterSync*` → `FrameSync*`

Three types share the `VideoPresenterSync` prefix:
- `VideoPresenterSyncPolicy` — the policy implementation (currently `internal`)
- `VideoPresenterSyncPolicyOptions` — its configuration (currently `internal`)
- `VideoPresenterSyncDecision` — its return value (currently `internal`)

The word "Presenter" is redundant — these types are used only by the video presentation path, so "Sync" alone is sufficient context.

When these types are made public (see `API-Review.md` §12.7), simpler names are more usable:

| Current | Proposed |
|---|---|
| `VideoPresenterSyncPolicy` | `VideoSyncPolicy` |
| `VideoPresenterSyncPolicyOptions` | `VideoSyncOptions` |
| `VideoPresenterSyncDecision` | `VideoSyncDecision` (keep internal) |

---

### 2.6 `ISupportsAdvancedRouting` → rename if kept separate

The word "Advanced" implies routing is optional or complex. If routing is merged into `IAudioVideoMixer`/`IAVMixer` (see `API-Review.md` §3.4), this interface disappears. If it stays separate:

```csharp
// BEFORE:
public interface ISupportsAdvancedRouting { ... }

// AFTER (option A — descriptive):
public interface IMixerRouting { ... }

// AFTER (option B — consistent with other capabilities):
public interface IRoutingSupport { ... }
```

---

### 2.7 `VideoPresentationHostPolicy` → `VideoDispatchPolicy`

"Host policy" is ambiguous — it sounds like it could relate to OS hosting, UI hosting, etc. The actual meaning is "how the mixer dispatches video frames to outputs (direct thread vs. background workers)".

```csharp
// BEFORE:
public enum VideoPresentationHostPolicy { DirectPresenterThread, ManagedBackground }

// AFTER:
public enum VideoDispatchPolicy { DirectThread, BackgroundWorker }
```

The member names are also simplified: `DirectPresenterThread` → `DirectThread`, `ManagedBackground` → `BackgroundWorker`.

---

### 2.8 `NDIIntegrationOptions` → `NDIEngineOptions`

`NDIIntegrationOptions` is passed to `NDIEngine.Initialize()`. The word "Integration" doesn't clarify what kind of options these are — they're engine startup options. `NDIEngineOptions` is consistent with `AudioEngineConfig` in `S.Media.PortAudio`.

```csharp
// BEFORE:
public sealed class NDIIntegrationOptions { ... }
NDIEngine.Initialize(ndiIntegrationOptions, limitsOptions);

// AFTER:
public sealed class NDIEngineOptions { ... }
NDIEngine.Initialize(engineOptions, limitsOptions);
```

---

### 2.9 `NDILimitsOptions` → `NDICapacityOptions`

"Limits" is slightly ambiguous (could mean error limits, rate limits, etc.). "Capacity" more precisely describes what this type controls: buffer depths, queue depths, maximum pending frames. The prefix method names (`LowLatency`, `Balanced`, `Safe`) suggest it controls capacity/buffering trade-offs.

```csharp
// BEFORE:
public sealed record NDILimitsOptions { ... }

// AFTER:
public sealed record NDICapacityOptions { ... }
```

---

### 2.10 `FFSharedDecodeContext` → `FFmpegDecodeSession`

`FFSharedDecodeContext` is actually a full session manager for FFmpeg: it holds the format context, manages `RefCount`, coordinates open/close, and stores the `ResolvedDecodeOptions`. The word "Context" understates this — it behaves like a session.

Renaming also aligns it with the `FFmpegRuntime` pattern and makes it consistent with the `FFmpeg` prefix decision from §2.1.

```csharp
// BEFORE:
public sealed class FFSharedDecodeContext : IDisposable

// AFTER:
public sealed class FFmpegDecodeSession : IDisposable
```

---

### 2.11 `MixerSourceDetachOptions` — consider removing

`MixerSourceDetachOptions` is a 9-line sealed record with two bool properties:
```csharp
public sealed record MixerSourceDetachOptions
{
    public bool StopOnDetach    { get; init; }
    public bool DisposeOnDetach { get; init; }
}
```

This could be replaced with a `[Flags]` enum or just two separate bool parameters on the `RemoveAudioSource` / `RemoveVideoSource` methods:

```csharp
// Option A — [Flags] enum:
[Flags]
public enum SourceDetachBehavior
{
    None    = 0,
    Stop    = 1,
    Dispose = 2,
}

// Option B — separate bool parameters (clearest):
int RemoveAudioSource(Guid sourceId, bool stopOnDetach = false, bool disposeOnDetach = false);
```

If the type stays, rename it to `SourceDetachOptions` (drop the `Mixer` prefix — it's not mixer-specific behaviour).

---

### 2.12 `SDL3VideoView` → `SDL3VideoOutput`

`SDL3VideoView` and `AvaloniaVideoOutput` both implement `IVideoOutput` and serve the same architectural role — they are video output drivers for a specific UI platform. Their names are inconsistent:

| Class | Implements | Suffix |
|---|---|---|
| `AvaloniaVideoOutput` | `IVideoOutput` | `VideoOutput` ✅ |
| `SDL3VideoView` | `IVideoOutput` | `VideoView` ❌ |

The suffix `View` implies a passive UI control; `VideoOutput` names the role in the pipeline correctly.

**Fix:** Rename to `SDL3VideoOutput`. Also align `SDL3VideoViewOptions` → `SDL3VideoOutputOptions`.

---

### 2.13 `OpenGLVideoEngine` → `OpenGLEngine`

The `S.Media.OpenGL` package is exclusively video — there is no audio engine or any non-video concern in it. The word `Video` in `OpenGLVideoEngine` is therefore redundant.

Compare: `PortAudioEngine`, `NDIEngine`, `MIDIEngine` — none of them include the media type in the engine name.

```csharp
// BEFORE:
public sealed class OpenGLVideoEngine : IDisposable

// AFTER:
public sealed class OpenGLEngine : IDisposable
```

Similarly, `OpenGLVideoOutput` loses nothing by becoming `OpenGLOutput`.

---

### 2.14 `CoreMediaClock` → `MediaClock`

`CoreMediaClock` is the concrete wall-clock `IMediaClock` implementation. The `Core` prefix is redundant — the class lives in `S.Media.Core.Clock` and there is no other `MediaClock` it needs to be distinguished from. The `Core` prefix reads as a namespace leak into the type name.

```csharp
// BEFORE:
public sealed class CoreMediaClock : IMediaClock  // in S.Media.Core.Clock

// AFTER:
public sealed class MediaClock : IMediaClock
```

---

### 2.15 `*Options` vs `*Config` — standardise one suffix

The framework uses both suffixes without a clear rule:

| `*Config` pattern | `*Options` pattern |
|---|---|
| `AudioEngineConfig` | `FFmpegOpenOptions` |
| `AudioOutputConfig` | `FFmpegDecodeOptions` |
| `VideoOutputConfig` | `NDISourceOptions` |
| `AudioInputConfig` | `NDIOutputOptions` |
| | `NDILimitsOptions` |
| | `NDIDiagnosticsOptions` |
| | `MIDIReconnectOptions` |
| | `SDL3VideoViewOptions` |
| | `OpenGLCloneOptions` |
| | `OpenGLClonePolicyOptions` |

The imbalance is roughly `Config` in `S.Media.Core` / `S.Media.PortAudio` and `Options` everywhere else, but there is no principled distinction.

**Recommendation:** Standardise on `*Options` across the board (it is more common in .NET BCL and ASP.NET patterns) and rename the `S.Media.Core` and `S.Media.PortAudio` outliers:

| Current | Proposed |
|---|---|
| `AudioEngineConfig` | `AudioEngineOptions` |
| `AudioOutputConfig` | `AudioOutputOptions` |
| `VideoOutputConfig` | `VideoOutputOptions` |
| `AudioInputConfig` | `AudioInputOptions` |

Alternatively, keep `*Config` for types passed to constructors/`Initialize()` and `*Options` for per-operation parameters — but this distinction must be applied consistently with documentation.

---

### 2.16 `AvaloniaGLRenderer` → `AvaloniaOpenGLRenderer`

The `GL` abbreviation appears nowhere else in the codebase — all other types spell it out as `OpenGL` (`OpenGLVideoOutput`, `OpenGLVideoEngine`, `OpenGLCloneOptions`, etc.). `AvaloniaGLRenderer` is the only exception.

```csharp
// BEFORE:
internal sealed class AvaloniaGLRenderer

// AFTER:
internal sealed class AvaloniaOpenGLRenderer
```

---

### 2.17 `*DebugInfo` vs `*Diagnostics` — pick one suffix for diagnostic snapshot types

Diagnostic snapshot records use two different suffixes with no clear rule:

| Type | Suffix |
|---|---|
| `AudioVideoMixerDebugInfo` | `DebugInfo` |
| `VideoOutputDiagnostics` | `Diagnostics` |
| `NDIAudioDiagnostics` | `Diagnostics` |
| `NDIVideoSourceDebugInfo` | `DebugInfo` ← inconsistent within NDI package |
| `NDIVideoOutputDebugInfo` | `DebugInfo` ← inconsistent within NDI package |
| `NDIEngineDiagnostics` | `Diagnostics` ← but wraps `*DebugInfo` fields |
| `OpenGLOutputDebugInfo` | `DebugInfo` |

**Fix:** Standardise on `*Diagnostics` for all public diagnostic snapshot records:

| Current | Proposed |
|---|---|
| `AudioVideoMixerDebugInfo` | `AVMixerDiagnostics` (after §2.3 rename) |
| `NDIVideoSourceDebugInfo` | `NDIVideoSourceDiagnostics` |
| `NDIVideoOutputDebugInfo` | `NDIVideoOutputDiagnostics` |
| `OpenGLOutputDebugInfo` | `OpenGLOutputDiagnostics` |

The `DebugInfo` suffix implies the data is only useful for debugging; `Diagnostics` is a more professional term that also fits monitoring and observability use cases.

---

## 3. File / Type Name Mismatches

### 3.1 `AudioVideoMixerRuntimeSnapshot.cs` contains `AudioVideoMixerDebugInfo`

The file is named `AudioVideoMixerRuntimeSnapshot.cs` but the type it defines is `AudioVideoMixerDebugInfo`. These should match.

**Fix:** Rename the file to `AudioVideoMixerDebugInfo.cs`.

---

### 3.2 `AreaExceptions.cs` contains only `DecodingException`

The file `AreaExceptions.cs` was presumably created to hold multiple per-area exception types (one per `MediaErrorArea`). Currently only `DecodingException` exists. The file name is misleading if this is the final state.

**Options:**
- If more area exceptions are planned, rename to `MediaExceptions.cs` (more general but still multi-type).
- If only `DecodingException` will ever exist, rename to `DecodingException.cs`.

---

### 3.3 `OSCPackets.cs` contains `OSCPacket`, `OSCMessage`, and `OSCBundle`

`OSCPackets.cs` contains three public types. Convention in C# is one primary type per file. The file name implies it contains the `OSCPackets` class (plural), but there is no such class — just three distinct types.

**Fix options:**
- Split into three files: `OSCPacket.cs`, `OSCMessage.cs`, `OSCBundle.cs`.
- Or merge into `OSCTypes.cs` (which already exists and groups related small types — `OSCTimeTag`, `OSCMIDIMessage`, etc.). `OSCMessage` and `OSCBundle` fit conceptually in `OSCTypes.cs`.

---

### 3.4 `OpenGLOutputDiagnostics.cs` contains `OpenGLOutputDebugInfo`

The file `OpenGLOutputDiagnostics.cs` defines a type named `OpenGLOutputDebugInfo`. The file name and type name do not match — the same category of mismatch as §3.1.

**Fix:** Either rename the file to `OpenGLOutputDebugInfo.cs` now, or rename both to `OpenGLOutputDiagnostics` as part of the §2.17 standardisation pass.

---

### 3.5 `NdiVideoReceive/` and `NDIVideoReceive/` both exist in `Test/`

The `Test/` directory contains two projects with near-identical names but different casing:
- `Test/NdiVideoReceive/` — lower-case `di`
- `Test/NDIVideoReceive/` — upper-case `DI`

One of these is presumably a stale duplicate from a rename that was not fully completed.

**Fix:** Determine which is the live project, delete the other, and ensure the solution file references only the correct one.

---

## 4. OSCLib Structural Issues

### 4.1 `OSCDecodeOptions`, `OSCServerOptions`, `OSCClientOptions` use mutable `set` — inconsistent with framework

```csharp
// OSCDecodeOptions:
public bool StrictMode { get; set; } = true;      // mutable setter
public int MaxArrayDepth { get; set; } = 16;       // mutable setter

// vs. S.Media.Core pattern:
public VideoOutputConfig { QueueCapacity { get; init; } }  // init-only
```

OSCLib options use mutable property setters while the rest of the framework uses `init`-only properties. This is a minor inconsistency but makes OSCLib options behave differently from everything else.

**Fix:** Change to `init`-only (and `sealed record` for consistency):

```csharp
public sealed record OSCDecodeOptions
{
    public bool StrictMode         { get; init; } = true;
    public int  MaxArrayDepth      { get; init; } = 16;
    // ...
}
```

---

### 4.2 `OSCAddressMatcher` is a static class with one public method

`OSCAddressMatcher.IsMatch(string pattern, string address)` is the only public method. It is a pure utility that could be a `static bool` method directly on `OSCRouter` (`private static`) or an extension method on `string`.

Since it is a testable unit (there are `AddressMatcherTests`), it is reasonable to keep it as a separate class for testability. However, rename it to make the purpose clearer:

```csharp
// BEFORE:
public static class OSCAddressMatcher

// AFTER:
public static class OSCAddressPattern   // or OSCPattern
```

---

## 5. Structural Issues in S.Media.\*

### 5.1 `NdiLib` empty directory should be deleted

`/NDI/NdiLib/` contains only build artefacts (`bin/`, `obj/`) and no source files. This is a stale project shell that should be removed.

---

### 5.2 PMLib `NativeCode/` folder

The `NativeCode/` subdirectory in PMLib likely contains platform-specific C code or is an empty placeholder. If empty, remove it. If it contains native source, document what it is and why it's in a C# project.

---

### 5.3 `S.Media.MIDI` layout is consistent but `MIDIPortMidiErrorMapper` name leaks a backend detail

`S.Media.MIDI` follows the correct layout (`Runtime/`, `Input/`, `Output/`, `Config/`, `Diagnostics/`, `Events/`, `Types/`) and is consistent with `S.Media.PortAudio` and `S.Media.NDI`. No structural issues.

One minor naming concern: `MIDIPortMidiErrorMapper` is an internal class whose name includes the PortMidi backend name. If the backend changes (or a secondary backend is added), the name becomes misleading. As it is `internal`, rename it to `MIDIErrorMapper` — the PortMidi origin doesn't need to be in the name.

---

### 5.4 Naming convention for event arg classes

The codebase has three patterns for event arg classes:

| Pattern | Example |
|---|---|
| `*EventArgs` suffix | `AudioSourceErrorEventArgs` |
| `*ChangedEventArgs` suffix | `AudioVideoMixerStateChangedEventArgs` |
| `*EventArgs` without subject | N/A |

All event arg classes should follow the `{Subject}{Verb}EventArgs` pattern where the verb is past tense (already happened):
- `AudioVideoMixerStateChangedEventArgs` ✅ — subject + past tense verb
- `AudioSourceErrorEventArgs` ✅ — subject + noun (error is already an event)
- `VideoActiveSourceChangedEventArgs` ✅

After unification (§1.3), `MediaSourceErrorEventArgs` fits this pattern. The naming is consistent; the only action needed is the merge.

---

### 5.5 PMLib `Devices/` uses `MIDI` prefix — should use `Pm*` to match PortMidi conventions

PMLib is a low-level PortMidi P/Invoke wrapper. All native PortMidi types it exposes correctly use the `Pm` prefix (`PmEvent`, `PmError`, `PmDeviceInfo`, `PmFilter`, `PmTimeProcDelegate`). However the managed device abstractions in `PMLib/Devices/` break this convention:

```
PMLib/Devices/MIDIDevice.cs        → base class for open device streams
PMLib/Devices/MIDIInputDevice.cs   → input stream wrapper
PMLib/Devices/MIDIOutputDevice.cs  → output stream wrapper
```

These are still part of the PortMidi wrapper layer (they use the native `Pm_*` calls directly), so they should carry the `Pm` prefix, not `MIDI`:

| Current | Proposed |
|---|---|
| `MIDIDevice` | `PmDevice` |
| `MIDIInputDevice` | `PmInputDevice` |
| `MIDIOutputDevice` | `PmOutputDevice` |

`MIDI` is used at the abstraction layer in `S.Media.MIDI`; `Pm` belongs in the raw wrapper `PMLib`.

---

### 5.6 `AudioRouteMapValidator` — another single-method static class

`AudioRouteMapValidator` in `S.Media.Core.Audio` is 36 lines with one public method:

```csharp
public static class AudioRouteMapValidator
{
    public static int ValidatePushFrameMap(
        in AudioFrame frame,
        ReadOnlySpan<int> sourceChannelByOutputIndex,
        int sourceChannelCount) { ... }
}
```

This is the same pattern as `MixerClockTypeRules` (§1.2) — a single-purpose static helper that barely warrants its own type. It is only called by `PortAudioOutput.PushFrame` and the mixer's audio push path.

**Fix:** Make it `internal` at minimum. If it is only ever called by `PortAudioOutput`, move the method there as a private static. If it is shared across multiple output types, keep it as an `internal static` helper class and remove it from the public API.

---

### 5.7 `IMediaPlaybackSourceBinding` — verbose interface name

`IMediaPlaybackSourceBinding` is an optional bridge interface implemented by `FFMediaItem` and `NDIMediaItem` to expose pre-constructed `IAudioSource` / `IVideoSource` lists for attaching to a mixer. The name is unwieldy.

**Proposed alternatives:**

```csharp
// Option A — describes what it provides:
public interface IMediaSources { ... }

// Option B — describes the relationship to the player:
public interface IPlayableMedia { ... }

// Option C — shorter binding name:
public interface ISourceProvider { ... }
```

`IMediaSources` is the most self-descriptive — callers see `item is IMediaSources` and understand it is a media item that carries source instances. It is also shorter by nine characters.

---


## 6. Summary Table

### Remove / Delete

| Type / File | Action |
|---|---|
| `NDIRuntimeScope` | Delete — fold into `NDIRuntime` as `IDisposable` with `Create()` factory |
| `MixerClockTypeRules` | Delete — move single method to `AudioVideoMixerConfig` as private static |
| `AudioSourceErrorEventArgs` | Delete — replace with unified `MediaSourceErrorEventArgs` |
| `VideoSourceErrorEventArgs` | Delete — replace with unified `MediaSourceErrorEventArgs` |
| `MediaErrorAllocations` int shortcuts (~30) | Delete — callers cast `MediaErrorCode` directly |
| `OSCArgs` | Delete — use `OSCArgument.Int32()` etc. directly |
| `NdiLib/` directory | Delete — empty project shell |

### Rename — Types

| Current | Proposed | Breaking? |
|---|---|---|
| `FFMediaItem` | `FFmpegMediaItem` | ✅ yes |
| `FFAudioSource` | `FFmpegAudioSource` | ✅ yes |
| `FFVideoSource` | `FFmpegVideoSource` | ✅ yes |
| `FFSharedDecodeContext` | `FFmpegDecodeSession` | ✅ yes (internal candidate) |
| `FFAudioChannelMap` | `FFmpegAudioChannelMap` | ✅ yes |
| `FFAudioSourceOptions` | `FFmpegAudioSourceOptions` | ✅ yes |
| `NDIVideoOutput` | `NDIOutput` | ✅ yes |
| `NDIIntegrationOptions` | `NDIEngineOptions` | ✅ yes |
| `NDILimitsOptions` | `NDICapacityOptions` | ✅ yes |
| `AudioVideoMixer` | `AVMixer` | ✅ yes |
| `IAudioVideoMixer` | `IAVMixer` | ✅ yes |
| `AudioVideoMixerConfig` | `AVMixerConfig` | ✅ yes |
| `AudioVideoMixerState` | `AVMixerState` | ✅ yes |
| `AudioVideoMixerDebugInfo` | `AVMixerDiagnostics` (see §2.17) | ✅ yes |
| `AudioVideoMixerStateChangedEventArgs` | `AVMixerStateChangedEventArgs` | ✅ yes |
| `AudioVideoSyncMode` | `AVSyncMode` | ✅ yes |
| `VideoOutputTimestampMonotonicMode` | `VideoTimestampMode` | ✅ yes |
| `VideoPresenterSyncPolicy` | `VideoSyncPolicy` | ✅ yes (make public) |
| `VideoPresenterSyncPolicyOptions` | `VideoSyncOptions` | ✅ yes (make public) |
| `VideoPresentationHostPolicy` | `VideoDispatchPolicy` | ✅ yes |
| `ISupportsAdvancedRouting` | `IMixerRouting` (or fold into `IAVMixer`) | ✅ yes |
| `MixerSourceDetachOptions` | `SourceDetachOptions` | ✅ yes |
| `OSCAddressMatcher` | `OSCAddressPattern` | ✅ yes |
| `AudioSourceState` | `SourceState` (shared with video) | ✅ yes |
| `VideoSourceState` | `SourceState` (shared with audio) | ✅ yes |
| `AudioOutputState` | `OutputState` | ✅ yes |
| `SDL3VideoView` | `SDL3VideoOutput` | ✅ yes |
| `SDL3VideoViewOptions` | `SDL3VideoOutputOptions` | ✅ yes |
| `OpenGLVideoEngine` | `OpenGLEngine` | ✅ yes |
| `OpenGLVideoOutput` | `OpenGLOutput` | ✅ yes |
| `CoreMediaClock` | `MediaClock` | ✅ yes |
| `NDIVideoSourceDebugInfo` | `NDIVideoSourceDiagnostics` | ✅ yes |
| `NDIVideoOutputDebugInfo` | `NDIVideoOutputDiagnostics` | ✅ yes |
| `OpenGLOutputDebugInfo` | `OpenGLOutputDiagnostics` | ✅ yes |
| `IMediaPlaybackSourceBinding` | `IMediaSources` | ✅ yes |
| `MIDIDevice` (PMLib) | `PmDevice` | ✅ yes |
| `MIDIInputDevice` (PMLib) | `PmInputDevice` | ✅ yes |
| `MIDIOutputDevice` (PMLib) | `PmOutputDevice` | ✅ yes |
| `AudioEngineConfig` | `AudioEngineOptions` | ✅ yes |
| `AudioOutputConfig` | `AudioOutputOptions` | ✅ yes |
| `VideoOutputConfig` | `VideoOutputOptions` | ✅ yes |
| `AudioInputConfig` | `AudioInputOptions` | ✅ yes |

### Rename — Files

| Current file | Proposed file |
|---|---|
| `AudioVideoMixerRuntimeSnapshot.cs` | `AudioVideoMixerDebugInfo.cs` |
| `AreaExceptions.cs` | `MediaExceptions.cs` or `DecodingException.cs` |
| `OSCPackets.cs` | Split into `OSCPacket.cs`, `OSCMessage.cs`, `OSCBundle.cs` |
| `OpenGLOutputDiagnostics.cs` | `OpenGLOutputDebugInfo.cs` (or rename type to match, per §2.17) |

### Make Internal

| Type | Reason |
|---|---|
| `FFStreamDescriptor` | Internal FFmpeg plumbing, duplicates `AudioStreamInfo`/`VideoStreamInfo` |
| `FFmpegConfigValidator` | Internal validation helper |
| `AudioRouteMapValidator` | Single-method static helper; no public consumer benefit |

### Stale / Dead Code

| Item | Action |
|---|---|
| `NdiLib/` project directory | Delete — no source files, empty build-artefact shell |
| `Test/NdiVideoReceive/` or `Test/NDIVideoReceive/` | Delete the stale duplicate — keep only one |

---

*This document covers naming conventions, class consolidation, and structural tidiness. All changes are appropriate for an API-breaking release. Cross-referenced from individual library docs.*

