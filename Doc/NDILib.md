# NDILib — Issues & Fix Guide

> **Scope:** `NDILib` — NDI SDK P/Invoke wrapper (`Native.cs`, `NDIWrappers.cs`, `NDIRuntime.cs`, `NDILibLogging`, types)
> **Cross-references:** See `API-Review.md` §11.2 and `S.Media.NDI.md` for the consumer-layer issues.

---

## Table of Contents

1. [Cross-Platform Library Loading](#1-cross-platform-library-loading)
2. [API Visibility](#2-api-visibility)
3. [Error Handling — Constructors That Throw](#3-error-handling--constructors-that-throw)
4. [Capture Semantics & Logging](#4-capture-semantics--logging)
5. [Logging](#5-logging)
6. [Class Consolidation](#6-class-consolidation)

---

## 1. Cross-Platform Library Loading

### Issue 1.1 — Hard-coded `"libndi.so.6"` — Linux-only

```csharp
private const string LibraryName = "libndi.so.6";
```

The NDI SDK ships under different names per platform:

| Platform | Library name |
|---|---|
| Linux | `libndi.so.6` |
| Windows | `Processing.NDI.Lib.x64.dll` (or `x86`) |
| macOS | `libndi.dylib` |

The current hard-code means `NDILib` fails silently on Windows and macOS. Unlike PALib, there is no `NativeLibrary.SetDllImportResolver` fallback.

**Fix:** Add an `NDILibraryResolver` registered via `[ModuleInitializer]`:

```csharp
// NDILib/Runtime/NDILibraryResolver.cs  (new file)
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NDILib.Runtime;

internal static class NDILibraryResolver
{
    private const string NdiLibName = "libndi.so.6";

    [ModuleInitializer]
    internal static void Register()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(NDILibraryResolver).Assembly,
            Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NdiLibName)
            return nint.Zero;

        string[] candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidates = ["Processing.NDI.Lib.x64", "Processing.NDI.Lib.x86"];
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidates = ["libndi", "libndi.dylib"];
        else
            candidates = ["libndi.so.6", "libndi.so", "libndi"];

        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
                return handle;
        }

        return nint.Zero;
    }
}
```

After adding this file, `[ModuleInitializer]` ensures it runs automatically when `NDILib` is first loaded.

**Consideration:** The NDI SDK on Windows must be installed in a location on the system `PATH` or next to the application. The resolver probes common names but does not probe custom install paths. If consumers use a non-standard NDI install path, they can call `NativeLibrary.SetDllImportResolver` themselves after the `[ModuleInitializer]` — the last registered resolver wins.

---

## 2. API Visibility

### Issue 2.1 — `Native.cs` is `internal` — inconsistent with PALib and PMLib

`NDILib.Native` is correctly `internal` (see `API-Review.md` §11.2.2). PALib and PMLib should be aligned to this pattern (see `PALib.md` §2.1 and `PMLib.md` §2.1).

No change needed in NDILib — this is the reference architecture for the other wrappers.

---

### Issue 2.2 — `NDISender.SendVideo` and `NDISender.SendAudio` do not return error codes

Both `SendVideo` and `SendAudio` call `Native.*` and return `void`. If the native send fails silently, there is no mechanism to detect it.

The NDI SDK's send functions technically do not return error codes (they are fire-and-forget). However, at minimum the connection count can be checked:

```csharp
public int GetConnectionCount(uint timeoutMs = 0)
    => Native.NDIlib_send_get_no_connections(_instance, timeoutMs);
```

**Recommendation:** After sending a frame, if `GetConnectionCount()` returns 0, log a Debug message indicating "no active NDI connections — frame may be dropped". This surfaces silent send-to-nowhere conditions.

---

## 3. Error Handling — Constructors That Throw

### Issue 3.1 — `NDIRuntimeScope`, `NDIFinder`, `NDISender`, `NDIFrameSync` throw `InvalidOperationException` on creation failure

All four throw when the native create call returns a null pointer:

```csharp
_instance = Native.NDIlib_send_create(create);
if (_instance == nint.Zero)
    throw new InvalidOperationException("Failed to create NDI sender instance.");
```

This is inconsistent with the S.Media.* framework convention of integer return codes. `using var scope = new NDIRuntimeScope()` requires a try/catch, unlike `PortAudioEngine.Initialize()` which returns a code.

**Fix:** Replace throwing constructors with static factory methods:

```csharp
// NDISender — example (apply same pattern to NDIFinder, NDIFrameSync, NDIRuntimeScope):
public sealed class NDISender : IDisposable
{
    private nint _instance;

    // Private constructor — only created via factory:
    private NDISender(nint instance) => _instance = instance;

    public static int Create(
        string? senderName,
        string? groups,
        bool clockVideo,
        bool clockAudio,
        out NDISender? sender)
    {
        sender = null;

        using var ndiName  = Utf8Buffer.From(senderName);
        using var groupBuf = Utf8Buffer.From(groups);

        var createParams = new NdiSendCreate
        {
            PNdiName  = ndiName.Pointer,
            PGroups   = groupBuf.Pointer,
            ClockVideo = clockVideo ? (byte)1 : (byte)0,
            ClockAudio = clockAudio ? (byte)1 : (byte)0
        };

        var ptr = Native.NDIlib_send_create(createParams);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDISenderCreateFailed;   // new error code

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISender created (name={Name}, ptr={Ptr})",
                senderName ?? "(default)", NDILibLogging.PtrMeta(ptr));

        sender = new NDISender(ptr);
        return MediaResult.Success;
    }

    // ...rest of the class unchanged...
}
```

Add to `NDILib` error codes (or map to `S.Media.Core` codes):

```csharp
public enum NDIErrorCode
{
    NDISenderCreateFailed  = 1,
    NDIReceiverCreateFailed = 2,
    NDIFinderCreateFailed  = 3,
    NDIFrameSyncCreateFailed = 4,
    NDIRuntimeInitFailed   = 5,
}
```

**Migration for `NDIRuntimeScope`:**

```csharp
// BEFORE:
using var scope = new NDIRuntimeScope();   // throws

// AFTER:
if (NDIRuntimeScope.Create(out var scope) is var r and not MediaResult.Success)
    return r;
using (scope) { ... }
```

---

## 4. Capture Semantics & Logging

### Issue 4.1 — `NDICaptureScope` frame-type semantics are undocumented

`NDIReceiver.CaptureScoped()` returns an `NDICaptureScope` whose `Dispose()` correctly handles `None` / `Error` / `StatusChange` frame types (by not freeing anything). However, the caller must check `scope.FrameType` before accessing `scope.Video`, `scope.Audio`, or `scope.Metadata` — the default struct values are zero/empty and will be silently passed to the caller if not checked.

**Fix:** Add XML documentation:

```csharp
/// <summary>
/// Represents a captured NDI frame. The captured resource is automatically freed on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <b>Important:</b> Always check <see cref="FrameType"/> before accessing <see cref="Video"/>,
/// <see cref="Audio"/>, or <see cref="Metadata"/>. Only the property corresponding to the current
/// <see cref="FrameType"/> contains valid data:
/// <list type="bullet">
///   <item><see cref="NdiFrameType.Video"/> → <see cref="Video"/> is valid</item>
///   <item><see cref="NdiFrameType.Audio"/> → <see cref="Audio"/> is valid</item>
///   <item><see cref="NdiFrameType.Metadata"/> → <see cref="Metadata"/> is valid</item>
///   <item><see cref="NdiFrameType.None"/>, <see cref="NdiFrameType.Error"/>,
///         <see cref="NdiFrameType.StatusChange"/> → no frame data; nothing to access</item>
/// </list>
/// </remarks>
public sealed class NDICaptureScope : IDisposable { ... }
```

---

### Issue 4.2 — No failure logging on `NDIlib_recv_capture_v3` — hot-path blind spot

PALib logs `Pa_ReadStream` and `Pa_WriteStream` failures at Debug level. NDILib has no equivalent for `NDIlib_recv_capture_v3`. Capture errors (`NdiFrameType.Error`) will silently manifest as "no frame" in `NDICaptureCoordinator`.

**Fix:** Add a Debug log in `NDIReceiver.Capture()` when `NdiFrameType.Error` is returned:

```csharp
public NdiFrameType Capture(
    out NdiVideoFrameV2 video,
    out NdiAudioFrameV3 audio,
    out NdiMetadataFrame metadata,
    uint timeoutMs)
{
    var frameType = Native.NDIlib_recv_capture_v3(_instance, out video, out audio, out metadata, timeoutMs);

    if (frameType == NdiFrameType.Error && Logger.IsEnabled(LogLevel.Debug))
        Logger.LogDebug("NDIReceiver capture returned Error (possible stream disconnect)");

    return frameType;
}
```

---

## 5. Logging

### Issue 5.1 — `NDILibLogging.TraceCall` boxes value types

Identical to `PALibLogging.TraceCall`. See `PALib.md` §3.1 for the recommended fix (source-generated `LoggerMessage` or inline `IsEnabled` guard).

---

### Issue 5.2 — Logging only at wrapper layer, not at native call layer

PALib logs at the `Native.cs` layer (trace on lifecycle calls, debug on I/O failures). NDILib only logs at the `NDIFinder` / `NDIReceiver` / `NDISender` wrapper layer. If a native call in `Native.cs` fails unexpectedly (e.g. `NDIlib_recv_free_video_v2` on an invalid pointer), there is no log entry.

For most NDI operations, the wrapper layer is sufficient. The exception is the hot-path `NDIlib_recv_capture_v3` failure case (see §4.2 above).

---

### Shared Logging Bootstrap

All three native wrapper logging classes require separate `Configure(ILoggerFactory)` calls. Add a unified entry point (see `PALib.md` §5 / `API-Review.md` §11.4):

```csharp
MediaNativeLogging.Configure(loggerFactory);
// configures PALibLogging, NDILibLogging, PMLibLogging in one call
```

---

## 6. Class Consolidation

### Issue 6.1 — `NDIRuntimeScope` should be folded into `NDIRuntime`

`NDIRuntime` is a `static` class. `NDIRuntimeScope` is a separate `IDisposable` RAII wrapper whose sole job is calling `NDIRuntime.Initialize()` and `NDIRuntime.Destroy()` in a `using`-friendly way. Two types for one concept.

**Full details:** See `Naming-and-Consolidation.md` §1.1. Summary fix:

```csharp
// Delete NDIRuntimeScope. Convert NDIRuntime to sealed IDisposable:

public sealed class NDIRuntime : IDisposable
{
    private bool _disposed;

    // Static queries safe to call without an instance:
    public static string Version
        => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;
    public static bool IsSupportedCpu()
        => Native.NDIlib_is_supported_CPU();

    // Factory — returns error code instead of throwing (resolves §3.1):
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

Migration: `using var scope = new NDIRuntimeScope()` (throws) → `NDIRuntime.Create(out var runtime)` (returns code) + `using (runtime)`.

---

## 7. Missing Native API Bindings

Cross-referencing `Native.cs` and `NDIWrappers.cs` against the NDI SDK headers (`Processing.NDI.Send.h`, `Processing.NDI.Recv.h`, `Processing.NDI.FrameSync.h`, `Processing.NDI.Routing.h`, `Processing.NDI.utilities.h`, `Processing.NDI.Recv.ex.h`) reveals several entire functional areas not bound.

### Issue 7.1 — Async video send (`NDIlib_send_send_video_async_v2`) missing

The SDK docs (§13.1) describe async video sending as a significant performance optimisation for BGRA sources, allowing color conversion and compression to happen off the main render thread. The frame buffer must not be freed until a synchronisation event (next `SendVideo`, `SendVideoAsync(null)`, or `Dispose`).

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_send_video_async_v2(nint p_instance, in NdiVideoFrameV2 p_video_data);

// Overload to flush pending async frame (pass nint.Zero for p_video_data)
[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_flush_async(nint p_instance);
```

Expose on `NDISender`:
```csharp
public void SendVideoAsync(in NdiVideoFrameV2 frame)
    => Native.NDIlib_send_send_video_async_v2(_instance, frame);

/// <summary>Flush a pending async video frame submitted via <see cref="SendVideoAsync"/>.</summary>
public void FlushAsync() => Native.NDIlib_send_send_video_async_v2(_instance, default);
```

---

### Issue 7.2 — Tally support entirely missing

NDI tally (on-program / on-preview) is a core broadcast workflow feature. Both senders and receivers expose tally:

- **Receiver tally** — `NDIlib_recv_set_tally` tells the upstream sender that this receiver is on-program or on-preview.  
- **Sender tally** — `NDIlib_send_get_tally` reads back the aggregate tally from all connected receivers.

`NdiTally` struct is also missing from `Types.cs`.

**Fix:** Add to `Types.cs`:
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NdiTally
{
    /// <summary>This output is currently on program.</summary>
    public byte OnProgram;
    /// <summary>This output is currently on preview.</summary>
    public byte OnPreview;
}
```

Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_recv_set_tally(nint p_instance, in NdiTally p_tally);

[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_send_get_tally(nint p_instance, out NdiTally p_tally, uint timeout_in_ms);
```

Expose on wrappers:
```csharp
// NDIReceiver:
public bool SetTally(bool onProgram, bool onPreview)
{
    var tally = new NdiTally { OnProgram = onProgram ? (byte)1 : (byte)0, OnPreview = onPreview ? (byte)1 : (byte)0 };
    return Native.NDIlib_recv_set_tally(_instance, tally);
}

// NDISender:
public bool GetTally(out NdiTally tally, uint timeoutMs = 0)
    => Native.NDIlib_send_get_tally(_instance, out tally, timeoutMs);
```

---

### Issue 7.3 — Receiver performance and queue monitoring missing

`NDIlib_recv_get_performance` returns total and dropped frame counts — essential for detecting overloaded receivers. `NDIlib_recv_get_queue` returns current queue depths.

**Fix:** Add to `Types.cs`:
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NdiRecvPerformance
{
    public long VideoFrames;
    public long AudioFrames;
    public long MetadataFrames;
}

[StructLayout(LayoutKind.Sequential)]
public struct NdiRecvQueue
{
    public int VideoFrames;
    public int AudioFrames;
    public int MetadataFrames;
}
```

Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial void NDIlib_recv_get_performance(nint p_instance, out NdiRecvPerformance p_total, out NdiRecvPerformance p_dropped);

[LibraryImport(LibraryName)]
internal static partial void NDIlib_recv_get_queue(nint p_instance, out NdiRecvQueue p_total);
```

Expose on `NDIReceiver`:
```csharp
public void GetPerformance(out NdiRecvPerformance total, out NdiRecvPerformance dropped)
    => Native.NDIlib_recv_get_performance(_instance, out total, out dropped);

public NdiRecvQueue GetQueue()
{
    Native.NDIlib_recv_get_queue(_instance, out var queue);
    return queue;
}
```

---

### Issue 7.4 — Bidirectional metadata missing

NDI supports metadata flowing in both directions:

- **`NDIlib_recv_send_metadata`** — receiver sends a metadata message upstream to the connected sender.  
- **`NDIlib_send_capture` + `NDIlib_send_free_metadata`** — sender can receive metadata from connected receivers.

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_recv_send_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

[LibraryImport(LibraryName)]
internal static partial NdiFrameType NDIlib_send_capture(nint p_instance, out NdiMetadataFrame p_metadata, uint timeout_in_ms);

[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_free_metadata(nint p_instance, in NdiMetadataFrame p_metadata);
```

Expose on wrappers:
```csharp
// NDIReceiver:
public bool SendMetadata(in NdiMetadataFrame frame) => Native.NDIlib_recv_send_metadata(_instance, frame);

// NDISender:
public NdiFrameType CaptureMetadata(out NdiMetadataFrame metadata, uint timeoutMs)
    => Native.NDIlib_send_capture(_instance, out metadata, timeoutMs);

public void FreeMetadata(in NdiMetadataFrame frame) => Native.NDIlib_send_free_metadata(_instance, frame);
```

---

### Issue 7.5 — Connection metadata management missing

Both `NDIReceiver` and `NDISender` support "connection metadata" — strings automatically sent to any new connection.

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial void NDIlib_recv_clear_connection_metadata(nint p_instance);
[LibraryImport(LibraryName)]
internal static partial void NDIlib_recv_add_connection_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_clear_connection_metadata(nint p_instance);
[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_add_connection_metadata(nint p_instance, in NdiMetadataFrame p_metadata);
```

Expose on both wrappers as `ClearConnectionMetadata()` / `AddConnectionMetadata(in NdiMetadataFrame)`.

---

### Issue 7.6 — Web control URL and source name missing from receiver

`NDIlib_recv_get_web_control` returns a URL for a PTZ/configuration web UI. `NDIlib_recv_get_source_name` returns the name of the currently connected source. Both require `NDIlib_recv_free_string` to free the returned pointer.

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial nint NDIlib_recv_get_web_control(nint p_instance);

[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_recv_get_source_name(nint p_instance, out nint p_source_name, uint timeout_in_ms);

[LibraryImport(LibraryName)]
internal static partial void NDIlib_recv_free_string(nint p_instance, nint p_string);
```

Expose on `NDIReceiver` with managed string helpers:
```csharp
public string? GetWebControl()
{
    var ptr = Native.NDIlib_recv_get_web_control(_instance);
    if (ptr == nint.Zero) return null;
    var result = Marshal.PtrToStringUTF8(ptr);
    Native.NDIlib_recv_free_string(_instance, ptr);
    return result;
}

public string? GetSourceName(uint timeoutMs = 0)
{
    if (!Native.NDIlib_recv_get_source_name(_instance, out var ptr, timeoutMs)) return null;
    if (ptr == nint.Zero) return null;
    var result = Marshal.PtrToStringUTF8(ptr);
    Native.NDIlib_recv_free_string(_instance, ptr);
    return result;
}
```

---

### Issue 7.7 — Failover and sender source name missing

`NDIlib_send_set_failover` lets a sender specify a backup NDI source for receivers if this sender goes offline. `NDIlib_send_get_source_name` retrieves the `NdiSource` for the created sender instance.

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial void NDIlib_send_set_failover(nint p_instance, in NdiSource p_failover_source);

[LibraryImport(LibraryName)]
internal static partial nint NDIlib_send_get_source_name(nint p_instance);
```

Expose on `NDISender`:
```csharp
public void SetFailover(in NdiDiscoveredSource source)
{
    using var name = Utf8Buffer.From(source.Name);
    using var url  = Utf8Buffer.From(source.UrlAddress);
    var s = new NdiSource { PNdiName = name.Pointer, PUrlAddress = url.Pointer };
    Native.NDIlib_send_set_failover(_instance, s);
}

public void ClearFailover()
{
    // pass zeroed struct = no failover
    Native.NDIlib_send_set_failover(_instance, default);
}
```

---

### Issue 7.8 — `NDIFrameSync.AudioQueueDepth` missing

`NDIlib_framesync_audio_queue_depth` returns the approximate number of audio samples in the queue — useful for adaptive pull.

**Fix:** Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial int NDIlib_framesync_audio_queue_depth(nint p_instance);
```

Expose on `NDIFrameSync`:
```csharp
public int AudioQueueDepth() => Native.NDIlib_framesync_audio_queue_depth(_instance);
```

---

### Issue 7.9 — NDI Routing API entirely missing

`Processing.NDI.Routing.h` defines a `NDIRouter` concept: a virtual NDI source that can be redirected to point at another source — useful for router/switching workflows.

**Fix:** Add to `Types.cs`:
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NdiRoutingCreate
{
    public nint PNdiName;
    public nint PGroups;
}
```

Add to `Native.cs`:
```csharp
[LibraryImport(LibraryName)]
internal static partial nint NDIlib_routing_create(in NdiRoutingCreate p_create_settings);
[LibraryImport(LibraryName)]
internal static partial void NDIlib_routing_destroy(nint p_instance);
[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_routing_change(nint p_instance, in NdiSource p_source);
[LibraryImport(LibraryName)]
[return: MarshalAs(UnmanagedType.I1)]
internal static partial bool NDIlib_routing_clear(nint p_instance);
[LibraryImport(LibraryName)]
internal static partial int NDIlib_routing_get_no_connections(nint p_instance, uint timeout_in_ms);
[LibraryImport(LibraryName)]
internal static partial nint NDIlib_routing_get_source_name(nint p_instance);
```

Add new `NDIRouter` wrapper class.

---

### Issue 7.10 — Audio interleaved conversion utilities missing

`Processing.NDI.utilities.h` provides send helpers and conversion functions for interleaved 16-bit, 32-bit, and float audio — the standard exchange format with OS audio APIs. All are missing from both `Native.cs` and the managed wrappers.

**Fix:** Add interleaved audio frame structs to `Types.cs` (`NdiAudioInterleaved16s`, `NdiAudioInterleaved32s`, `NdiAudioInterleaved32f`) and bind `NDIlib_util_*` functions in `Native.cs`. Expose via a static `NDIAudioUtils` class.

---

## 8. Missing Constants

### Issue 8.1 — `NDIlib_send_timecode_synthesize` not exposed

The NDI SDK defines `NDIlib_send_timecode_synthesize = INT64_MAX`. When this value is set in a frame's `Timecode` field, the SDK auto-generates the timecode. This is the intended default but callers have no named constant — they must use `long.MaxValue` with no hint.

**Fix:** Add to `Types.cs`:
```csharp
public static class NdiConstants
{
    /// <summary>
    /// Pass as a frame's <c>Timecode</c> to have the NDI runtime synthesize the timecode automatically.
    /// </summary>
    public const long TimecodeSynthesize = long.MaxValue;

    /// <summary>
    /// A <c>Timestamp</c> value indicating the sender does not provide a timestamp.
    /// Only valid when receiving; means the sender is pre-v2.5.
    /// </summary>
    public const long TimestampUndefined = long.MaxValue;
}
```

---

## 9. Struct Union Fields

### Issue 9.1 — `NdiVideoFrameV2.LineStrideInBytes` union semantics undocumented

The native struct uses a union:
```c
union {
    int line_stride_in_bytes;   // non-compressed: bytes between scan lines
    int data_size_in_bytes;     // compressed: total buffer size
};
```

Our C# struct exposes only `LineStrideInBytes`. For compressed formats, this same field is the total buffer size. The field name and XML doc should reflect this.

**Fix:** Add XML doc to `NdiVideoFrameV2.LineStrideInBytes`:
```csharp
/// <summary>
/// For non-compressed formats: inter-line stride in bytes (0 = sizeof(pixel) × <see cref="Xres"/>).
/// For compressed formats: total size of the <see cref="PData"/> buffer in bytes.
/// </summary>
public int LineStrideInBytes;
```

### Issue 9.2 — `NdiAudioFrameV3.ChannelStrideInBytes` union semantics undocumented

Identical pattern:
```c
union {
    int channel_stride_in_bytes;  // FLTP: bytes per channel plane
    int data_size_in_bytes;       // compressed: total buffer size
};
```

Add corresponding XML doc to `NdiAudioFrameV3.ChannelStrideInBytes`.

---

## 10. NDIFinder Settings — Groups and Extra IPs not exposed

`NDIlib_find_create_t` has three fields: `show_local_sources`, `p_groups`, and `p_extra_ips`. The current `NDIFinder` constructor only exposes `showLocalSources`. The other two fields allow:

- **`p_groups`** — filter by NDI group name (e.g. `"Public"`, `"Studio A"`)
- **`p_extra_ips`** — comma-separated list of extra IP addresses to query for sources on remote subnets

**Fix:** Add a `NDIFinderSettings` class:
```csharp
public sealed class NDIFinderSettings
{
    public bool ShowLocalSources { get; init; } = true;
    public string? Groups { get; init; }
    public string? ExtraIps { get; init; }
}
```

Update `NDIFinder` factory to accept it:
```csharp
public static int Create(out NDIFinder? finder, NDIFinderSettings? settings = null)
```

---

## 11. NDIReceiver — `Connect(null)` Disconnect Not Exposed

The native `NDIlib_recv_connect(p_instance, NULL)` disconnects the receiver from its current source. The current `Connect(in NdiDiscoveredSource)` has no disconnect counterpart.

**Fix:** Add a `Disconnect()` method:
```csharp
public void Disconnect() => Native.NDIlib_recv_connect_null(_instance);
```

Since `LibraryImport` does not support passing `null` for an `in` struct, a separate binding is needed:
```csharp
// Native.cs:
[LibraryImport(LibraryName, EntryPoint = "NDIlib_recv_connect")]
internal static partial void NDIlib_recv_connect_null(nint p_instance, nint p_src);

// NDIReceiver:
public void Disconnect() => Native.NDIlib_recv_connect_null(_instance, nint.Zero);
```

---

## 12. `NDICaptureScope` Nested Type

`NDIReceiver.NDICaptureScope` is a nested class inside `NDIReceiver`. Every other public type in the library is top-level. The nesting makes it harder to reference in doc comments, XML signatures, and generic constraints.

**Fix:** Promote `NDICaptureScope` to a top-level class in the `NDILib` namespace:
```csharp
// Top-level in NDIWrappers.cs (or its own file):
public sealed class NDICaptureScope : IDisposable { ... }
```

---

*See also `Naming-and-Consolidation.md` for all rename and removal recommendations across the solution.*
