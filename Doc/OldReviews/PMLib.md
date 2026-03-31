# PMLib — Issues & Fix Guide

> **Scope:** `PMLib` — PortMidi P/Invoke wrapper (`Native.cs`, `PMUtil.cs`, `PMLibLogging`, device wrappers)
> **Cross-references:** See `API-Review.md` §11.3 and `S.Media.MIDI.md` for the consumer-layer issues.
> **Last updated:** all issues from the original review have been implemented. Convention-alignment pass completed March 2026. See §7 for the resolution summary.
> **Native AOT:** ✅ `<IsAotCompatible>true</IsAotCompatible>` — verified clean by the .NET AOT analyzer.

---

## Table of Contents

1. [Library Loading](#1-library-loading)
2. [API Visibility](#2-api-visibility)
3. [Unsafe Enumeration Pattern](#3-unsafe-enumeration-pattern)
4. [Logging & Tracing](#4-logging--tracing)
5. [Good Patterns to Preserve](#5-good-patterns-to-preserve)
6. [Native AOT Compatibility](#6-native-aot-compatibility)
7. [Resolution Summary](#7-resolution-summary)

---

## 1. Library Loading

### ✅ Issue 1.1 — No `PortMidiLibraryResolver` — **RESOLVED (updated)**

PMLib relied on the platform's default loader to find `"portmidi"`. Some Linux distributions ship
`libportmidi.so.2` without a `.so` symlink, causing silent load failure.

**Initial fix:** `PortMidiLibraryResolver.cs` registered via `[ModuleInitializer]` — probes candidate
names per platform.

**Convention-alignment update:** `PortMidiLibraryResolver` was upgraded to match the PALib/NDILib
pattern:

- Added `Install(ILoggerFactory? loggerFactory = null)` public method — can be called at app
  startup to attach a real logger. The logger is always updated when a non-null factory is supplied,
  even if the resolver is already installed.
- Added `_installed` bool flag with a `Lock` — prevents double registration.
- Added `ILogger _logger` field — resolver logs successful and failed load attempts at `Debug`.
- The `[ModuleInitializer]` moved to a dedicated `PMLibModuleInit` class (mirrors `PALibModuleInit`).
- Created `PortMidiLibraryNames.cs` — public constants file with `Default`, `LinuxCandidates`,
  `MacCandidates`, `WindowsCandidates` (mirrors `PortAudioLibraryNames.cs`).

Probe order:
- Linux: `libportmidi.so.2` → `libportmidi.so` → `portmidi`
- macOS: `libportmidi.dylib` → `portmidi`
- Windows: `portmidi` (finds `portmidi.dll` automatically)

Probe order:
- Linux: `libportmidi.so.2` → `libportmidi.so` → `portmidi`
- macOS: `libportmidi.dylib` → `portmidi`
- Windows: `portmidi` (finds `portmidi.dll` automatically)

---

## 2. API Visibility

### ✅ Issue 2.1 — `Native.cs` methods were `public` inside an `internal` class — **RESOLVED (updated)**

The class `PMLib.Native` was already `internal static partial class Native`. However, all P/Invoke
method declarations used `public static partial` — inconsistent with the enclosing `internal` class
and with NDILib's pattern.

**Convention-alignment update:** All method declarations changed to `internal static partial`.
Functionally equivalent (the class being `internal` already prevents external access), but now
visually consistent and correctly expresses the intended scope.

---

## 3. Unsafe Enumeration Pattern

### ✅ Issue 3.1 — `PMUtil.GetAllDevices()` unsafe string pointers — **RESOLVED**

**Root cause (two-stage):**
1. The original `GetAllDevices()` used `yield return` — deferred materialisation with a live
   `Pm_Terminate`-hazard window.
2. Even after switching to eager materialisation, `PmDeviceInfo.Name` and `.Interf` are
   *computed properties* that call `Marshal.PtrToStringUTF8(nint)` on demand from raw pointer
   fields. Any cached `PmDeviceInfo` struct accessed after `Pm_Terminate` is a use-after-free.

**Fix implemented:** New type `PMLib.Types.PmDeviceEntry` — a `readonly record struct` with
managed `string?` fields that are resolved eagerly at creation time:

```csharp
public readonly record struct PmDeviceEntry(
    int     Id,
    string? Name,       // copied from native memory at construction
    string? Interface,  // copied from native memory at construction
    bool    IsInput,
    bool    IsOutput,
    bool    IsVirtual,
    bool    IsOpen);
```

`PMUtil.GetAllDevices()`, `GetInputDevices()`, and `GetOutputDevices()` now return
`IReadOnlyList<PmDeviceEntry>`. The low-level `GetDeviceInfo(int)` is retained for internal
use but carries an XML doc warning. A new `GetDeviceEntry(int)` convenience overload returns a
safe `PmDeviceEntry?` for callers that query a single device.

`MIDIEngine.RefreshCatalog` has been updated to use `PMUtil.GetAllDevices()` — eliminating the
manual `CountDevices`/`GetDeviceInfo` loop.

---

### Issue 3.2 — `Pm_QueueCreate` uses `nint` for `numMsgs`

No fix needed. `nint` correctly models C `long` (64-bit on Linux/macOS, 32-bit on Windows).
Documented as intentional.

---

## 4. Logging & Tracing

### ✅ Issue 4.1 — No call tracing in device wrappers — **RESOLVED**

`MIDIInputDevice` and `MIDIOutputDevice` now log:
- `Debug` on `Open()` and `Close()` (with device ID, name, buffer size, latency as structured properties)
- `Warning` on `Open()` failure
- `Warning` on buffer overflow in the `PollLoop`

`MIDIDevice` base class logs `Debug` at construction time (device ID, name, interface).

---

### ✅ Issue 4.2 — `PMLibLogging.TraceCall` allocated on every call — **RESOLVED (updated)**

The initial fix changed the signature from `params (string Name, object? Value)[]` to
`params (string Name, string Value)[]` to eliminate boxing. However, the `params` array was
still allocated at every call site even when trace logging was disabled.

**Convention-alignment update:** `TraceCall` was **removed** from `PMLibLogging` entirely.
`PMLib.Native` does not use a wrapper-layer tracing pattern — device wrappers log directly with
`IsEnabled` guards — so the helper was unused. Removing it eliminates the dead code and aligns
with the NDILib and updated PALib conventions.

---

## 5. Good Patterns to Preserve

### ✅ `MIDIDevice` constructor eagerly copies device info

```csharp
protected MIDIDevice(int deviceId)
{
    var ptr = Native.Pm_GetDeviceInfo(deviceId);
    if (ptr != nint.Zero)
    {
        var info = Marshal.PtrToStructure<PmDeviceInfo>(ptr);
        Name      = info.Name;   // PtrToStringUTF8 called now, while library is alive
        Interface = info.Interf; // same
    }
}
```

Strings are copied into managed memory immediately — safe after `Pm_Terminate`.
The new `PmDeviceEntry` type extends this pattern to bulk enumeration.

---

### ✅ `PMUtil.GetHostErrorText` uses `stackalloc`

```csharp
public static string GetHostErrorText()
{
    Span<byte> buffer = stackalloc byte[256]; // PM_HOST_ERROR_MSG_LEN = 256
    Native.Pm_GetHostErrorText(buffer, (uint)buffer.Length);
    var nullIdx = buffer.IndexOf((byte)0);
    var slice = nullIdx >= 0 ? buffer[..nullIdx] : buffer;
    return Encoding.UTF8.GetString(slice);
}
```

Stack-allocated buffer — zero heap allocation for the error string path.

---

### ✅ Virtual device support (`Pm_CreateVirtualInput` / `Pm_CreateVirtualOutput`)

PMLib exposes PortMidi's virtual device creation API with clear documentation that it returns
`PmError.NotImplemented` on Windows. Keep as-is; any `MIDIEngine.CreateVirtualInput()` wrapper
should propagate this error code without throwing.

---

## 6. Native AOT Compatibility

PMLib is **fully Native AOT compatible**. The project carries `<IsAotCompatible>true</IsAotCompatible>`,
which activates the .NET AOT and trim-safety analyzers at every build. The library produces **zero
AOT warnings**.

### What makes it safe

| Concern | Verdict | Reason |
|---|---|---|
| P/Invoke declarations | ✅ Safe | All bindings use `[LibraryImport]` (source-generated marshaling — no runtime reflection) |
| `Marshal.PtrToStructure<PmDeviceInfo>` | ✅ Safe | `PmDeviceInfo` is a blittable `[StructLayout(Sequential)]` struct (only `int`/`nint` fields); the generic overload uses `[DynamicallyAccessedMembers]` internally — no dynamic code required |
| `Marshal.PtrToStringUTF8` | ✅ Safe | Simple native-to-managed string copy, no marshaling metadata needed |
| `[ModuleInitializer]` in `PortMidiLibraryResolver` | ✅ Safe | Fully supported by the AOT runtime |
| `NativeLibrary.SetDllImportResolver` / `TryLoad` | ✅ Safe | Designed for AOT use |
| `Lock` (`System.Threading.Lock`) | ✅ Safe | .NET 9+ intrinsic type |
| `Microsoft.Extensions.Logging.Abstractions` | ✅ Safe | AOT-annotated by Microsoft |
| `Span<PmEvent>` / `ReadOnlySpan<byte>` in `[LibraryImport]` | ✅ Safe | `PmEvent` is blittable; source generator emits correct pinned-span interop |
| `PmTimeProcDelegate` (`[UnmanagedFunctionPointer]`) | ✅ Safe | The delegate *type definition* is AOT-safe. Note: `Marshal.GetFunctionPointerForDelegate` (mentioned in its doc comment) is **not** AOT-safe — callers wishing to pass a time procedure under AOT should use `delegate* unmanaged[Cdecl]<nint, int>` directly |

### Property in `PMLib.csproj`

```xml
<IsAotCompatible>true</IsAotCompatible>
```

This property does two things:
1. Activates the trim and AOT analyzers during `dotnet build` — any future regression will surface
   as a build warning immediately.
2. Signals to NuGet and downstream project analyzers that the library is safe to include in an
   AOT-published application.

---

## 7. Resolution Summary

| # | Issue | Status |
|---|---|---|
| 1.1 | No `PortMidiLibraryResolver` — `libportmidi.so.2` not found on some Linux distros | ✅ Resolved |
| 1.1+ | `PortMidiLibraryResolver` lacked `Install(ILoggerFactory?)`, `_installed` flag, logger | ✅ Resolved (convention-alignment) — upgraded to PALib/NDILib pattern; `PortMidiLibraryNames.cs` + `PMLibModuleInit` added |
| 2.1 | `Native.cs` methods declared `public` inside `internal` class | ✅ Resolved (convention-alignment) — all methods now `internal static partial` |
| 3.1 | `GetAllDevices()` deferred `IEnumerable` + `PmDeviceInfo` string pointer hazard | ✅ Resolved — `PmDeviceEntry` + eager materialisation |
| 3.2 | `nint` for `numMsgs` in `Pm_QueueCreate` | ✅ Intentional — no change needed |
| 4.1 | No call tracing in device wrappers | ✅ Resolved |
| 4.2 | `TraceCall` allocated `params` array even when trace disabled | ✅ Resolved (convention-alignment) — `TraceCall` removed entirely; unused in `Native.cs` |
| — | Native AOT compatibility | ✅ Verified — `<IsAotCompatible>true</IsAotCompatible>`, zero analyzer warnings |
