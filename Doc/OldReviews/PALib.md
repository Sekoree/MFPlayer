# PALib — Issues & Fix Guide

> **Scope:** `PALib` — PortAudio P/Invoke wrapper (`Native.cs`, `PALibLogging`, `PortAudioLibraryResolver`, host-API-specific types)
> **Cross-references:** See `API-Review.md` §11.1 and `S.Media.PortAudio.md` for the consumer-layer issues.
> **Last updated:** March 2026 — header cross-reference audit completed; §8 issues found and resolved. Convention-alignment pass (§9) completed March 2026.
> **Native AOT:** ✅ `<IsAotCompatible>true</IsAotCompatible>` — verified clean by the .NET AOT analyzer.

---

## Table of Contents

1. [Library Loading](#1-library-loading)
2. [API Visibility](#2-api-visibility)
3. [Logging & Tracing](#3-logging--tracing)
4. [Memory Allocation in Hot Paths](#4-memory-allocation-in-hot-paths)
5. [Miscellaneous](#5-miscellaneous)
6. [Native AOT Compatibility](#6-native-aot-compatibility)
7. [Header Cross-Reference Audit](#7-header-cross-reference-audit)
8. [Resolution Summary](#8-resolution-summary)

---

## 1. Library Loading

### ✅ Issue 1.1 — `PortAudioLibraryResolver` must be called manually — **RESOLVED**

`PortAudioLibraryResolver.Install()` was required to be called before any `Native.*` method. There
was no enforcement of this ordering — if forgotten, the OS default loader could pick up an
incompatible library version or fail entirely.

**Fix implemented:** `PALib/Runtime/PALibModuleInit.cs` — an `internal` class using
`[ModuleInitializer]` to call `PortAudioLibraryResolver.Install()` automatically:

```csharp
internal static class PALibModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() => PortAudioLibraryResolver.Install();
}
```

`[ModuleInitializer]` runs exactly once before any other code in the assembly (including static
constructors). `Install()` calls `NativeLibrary.SetDllImportResolver` which is idempotent —
calling it again manually is safe. The CA2255 analyser warning is suppressed with a targeted
`#pragma` and explanatory comment.

---

## 2. API Visibility

### ✅ Issue 2.1 — All `Native` classes were `public` — **RESOLVED**

All nine `Native` classes (`PALib.Native` + eight host-API namespaces: ALSA, ASIO, CoreAudio,
DirectSound, JACK, WASAPI, WDMKS, WMME) are now `internal`. Raw P/Invoke is no longer part of
the public API surface.

`PALib.csproj` grants `InternalsVisibleTo` access to the three intentional consumers:

```xml
<InternalsVisibleTo Include="S.Media.PortAudio" />  <!-- primary managed wrapper -->
<InternalsVisibleTo Include="PALib.Smoke" />         <!-- developer smoke test    -->
<InternalsVisibleTo Include="PALib.Tests" />         <!-- unit / interop tests    -->
```

`S.Media.PortAudio` is the correct layer for high-level PortAudio lifecycle management.
Third-party code can no longer call `Native.Pa_CloseStream` or `Native.Pa_Terminate` directly.

**Note:** A `PortAudioStream` RAII wrapper (as described in the original issue) remains an option
for a future refactor if `S.Media.PortAudio` is ever split out as a standalone NuGet package.

---

### ✅ Issue 2.2 — `Pa_Sleep` exposed on the public API — **RESOLVED**

`Pa_Sleep` is now `internal` along with the rest of `Native`. The one call site in
`S.Media.PortAudio/Output/PortAudioOutput.cs` has been replaced with `Thread.Sleep(1)`, which
integrates properly with the .NET runtime cooperative scheduler.

---

## 3. Logging & Tracing

### ✅ Issue 3.1 — `PALibLogging.TraceCall` allocated on every call — **RESOLVED (updated)**

The initial fix changed the signature from `params (string Name, object? Value)[]` to
`params (string Name, string Value)[]` to eliminate boxing. However, the `params` array itself
and any string-interpolation arguments were still allocated before `TraceCall` could check
`IsEnabled` — meaning a disabled trace level still caused allocation at every call site.

**Final fix (convention-alignment pass):** `TraceCall` and `BufferMeta` were **removed** from
`PALibLogging` entirely. All call sites in `Native.cs` (core, ASIO, WMME, WDMKS, DirectSound,
WASAPI) were converted to inline `IsEnabled` guards:

```csharp
// Before
PALibLogging.TraceCall(Logger, nameof(Pa_CloseStream), (nameof(stream), PALibLogging.PtrMeta(stream)));

// After
if (Logger.IsEnabled(LogLevel.Trace))
    Logger.LogTrace("{Method}({Stream})", nameof(Pa_CloseStream), PALibLogging.PtrMeta(stream));
```

Zero allocation when trace logging is disabled. `PtrMeta` is retained in `PALibLogging` as a
formatting helper — it is only called inside an `IsEnabled` guard and does not allocate on the
fast path.

---

### ✅ Issue 3.2 — Tracing applied non-uniformly — **RESOLVED**

The following lifecycle calls were not previously traced and now are:

| Method | Log level |
|---|---|
| `Pa_GetDeviceInfo` | `Trace` |
| `Pa_CloseStream` | `Trace` |
| `Pa_StartStream` | `Trace` |
| `Pa_StopStream` | `Trace` |
| `Pa_AbortStream` | `Trace` |

Stream I/O failure logging (`Pa_WriteStream` / `Pa_ReadStream` errors) at `Debug` level was
already in place and is preserved. Pure query methods (`GetDeviceCount`, `GetStreamTime`, etc.)
remain untraced — they are on the hot path.

---

## 4. Memory Allocation in Hot Paths

### ✅ Issue 4.1 — `Pa_OpenStream` / `Pa_IsFormatSupported` used `Marshal.AllocHGlobal` — **RESOLVED**

Both methods previously heap-allocated two `PaStreamParameters` blocks unconditionally, even
when both parameters were `null`. Replaced with unsafe stack values:

```csharp
PaStreamParameters inParam  = inputParameters  ?? default;
PaStreamParameters outParam = outputParameters ?? default;
nint pIn  = inputParameters.HasValue  ? (nint)(&inParam)  : nint.Zero;
nint pOut = outputParameters.HasValue ? (nint)(&outParam) : nint.Zero;
return Pa_OpenStream_Import(out stream, pIn, pOut, ...);
```

`PaStreamParameters` contains no owned unmanaged memory — all pointer fields are non-owning
references into PortAudio's internal state. Stack allocation is safe.

---

## 5. Miscellaneous

### Issue 5.1 — `TraceCall` removal after source-gen migration

After §3.1 fixed the boxing by changing the signature, `TraceCall` is still in use and retained.
If a future refactor adopts source-generated logging (`[LoggerMessage]`), `TraceCall` and
`BufferMeta` would become dead code and should be removed at that point.

`PtrMeta` remains useful as a shared formatting helper for native handle diagnostics across all
three wrappers. Migration to a shared `MediaNativeLogging` utility is tracked in `API-Review.md`
§11.4.

---

### Issue 5.2 — Host-API specific types logging

ASIO, WASAPI, and WMME `Native` classes already use `TraceCall` at their respective call sites.
ALSA, CoreAudio, JACK log at `Debug` level for unsupported-platform guards. WDMKS and DirectSound
provide `TraceInfo`/`TraceStreamInfo` diagnostic helpers.

All host-API `Native` classes are now `internal` (see §2.1), ensuring no raw host-API handles are
accessible from external assemblies.

---

### Shared Logging Bootstrap

All three native wrappers (`PALibLogging`, `NDILibLogging`, `PMLibLogging`) still have
near-identical implementations and require separate `Configure(ILoggerFactory)` calls. A shared
`MediaNativeLogging.Configure(factory)` bootstrap is tracked in `API-Review.md` §11.4.

---

## 6. Native AOT Compatibility

PALib is **fully Native AOT compatible**. The project carries `<IsAotCompatible>true</IsAotCompatible>`,
which activates the .NET AOT and trim-safety analyzers at every build. The library produces **zero
AOT warnings**.

### What makes it safe

| Concern | Verdict | Reason |
|---|---|---|
| P/Invoke declarations | ✅ Safe | All `[LibraryImport]` — source-generated marshaling |
| `Marshal.PtrToStructure<T>` for info structs | ✅ Safe | All target types are blittable `[StructLayout(Sequential)]` structs |
| `Marshal.PtrToStringUTF8` | ✅ Safe | Simple pointer-to-string copy |
| `[ModuleInitializer]` in `PALibModuleInit` | ✅ Safe | Fully supported by AOT runtime |
| `NativeLibrary.SetDllImportResolver` / `TryLoad` | ✅ Safe | Designed for AOT use |
| `delegate* unmanaged[Cdecl]<...>` function pointers | ✅ Safe | Unmanaged function pointers are AOT-native |
| Stack-allocated `PaStreamParameters` (Issue 4.1) | ✅ Safe | Blittable struct, no managed references |
| LINQ in `PALibLogging.TraceCall` | ✅ Safe | Simple `Select` over concrete `(string, string)[]` |
| `Microsoft.Extensions.Logging.Abstractions` | ✅ Safe | AOT-annotated by Microsoft |

### Property in `PALib.csproj`

```xml
<IsAotCompatible>true</IsAotCompatible>
```

Activates the trim and AOT analyzers during `dotnet build`. Any future regression surfaces
immediately as a build warning.

---

## 7. Header Cross-Reference Audit

A detailed cross-reference of the PortAudio C headers in `Reference/Portaudio/include/` against
the C# implementation was performed. The following issues were found and resolved.

---

### ✅ Issue 7.1 — Missing `EntryPoint` on `[LibraryImport]` across all host-API `Native.cs` files — **RESOLVED** (CRITICAL)

**Root cause:** `[LibraryImport]` without an explicit `EntryPoint` uses the C# *method name* as the
native symbol to look up. Since all host-API import stubs follow the `<FuncName>_Import` naming
convention, the runtime would search for a symbol named `PaAlsa_InitializeStreamInfo_Import`
instead of `PaAlsa_InitializeStreamInfo` — resulting in `EntryPointNotFoundException` when
called.

**Severity:** ALSA is Linux-only; its functions would fail at runtime on the current development
platform. JACK (`PaJack_SetClientName`) is also Linux/macOS. WASAPI, CoreAudio, WMME affect
Windows only.

**Files and methods fixed (explicit `EntryPoint` added):**

| File | Methods fixed |
|---|---|
| `ALSA/Native.cs` | ALL 7 methods — `PaAlsa_*` |
| `JACK/Native.cs` | `PaJack_SetClientName_Import` |
| `WASAPI/Native.cs` | 14 of 15 methods — all `PaWasapi_*` and `PaWasapiWinrt_*` except `PaWasapi_SetStreamStateHandler` which already had `EntryPoint` |
| `CoreAudio/Native.cs` | 5 of 6 methods — all `PaMacCore_*` except `PaMacCore_GetChannelName` which already had `EntryPoint` |
| `WMME/Native.cs` | `PaWinMME_GetStreamInputHandle_Import`, `PaWinMME_GetStreamOutputHandle_Import` |

The two `ASIO/Native.cs` methods (`PaAsio_*`) and all of `DirectSound/Native.cs` and `WDMKS/Native.cs`
already had explicit `EntryPoint` attributes and were not affected.

---

### ✅ Issue 7.2 — `Pa_GetVersionText` missing from core `Native.cs` — **RESOLVED**

`portaudio.h` declares `Pa_GetVersionText()` (deprecated since 19.5.0 but still present in the ABI).
The binding was absent. Added with an `[Obsolete]` attribute directing callers to
`Pa_GetVersionInfo().VersionText` instead:

```csharp
[Obsolete("Deprecated since PortAudio 19.5.0. Use Pa_GetVersionInfo().VersionText instead.")]
public static string? Pa_GetVersionText() { ... }
```

---

### ✅ Issue 7.3 — `WaveFormatNative` was `public` and lacked a platform guard — **RESOLVED**

`WASAPI/PaWinWaveFormatTypes.cs` contained a `public static partial class WaveFormatNative` with
four direct `[LibraryImport]` P/Invoke methods (`PaWin_SampleFormatToLinearWaveFormatTag`,
`PaWin_InitializeWaveFormatEx`, `PaWin_InitializeWaveFormatExtensible`,
`PaWin_DefaultChannelMask`). These Windows-only symbols do not exist in `libportaudio.so` on
Linux — calling any of them on Linux would throw `EntryPointNotFoundException`.

**Fix:** Class changed to `internal`. Methods refactored to the `_Import` + wrapper pattern with
`IsSupportedPlatform => OperatingSystem.IsWindows()` guard and explicit `EntryPoint` on each
`[LibraryImport]`.

---

### 📝 Issue 7.4 — `unsigned long` C type vs C# mapping (documented, not a bug on Linux)

`portaudio.h` uses `unsigned long` for `PaSampleFormat`, `PaStreamFlags`, `PaStreamCallbackFlags`,
`PaStreamParameters.sampleFormat`, and various stream-info struct fields. In C, `sizeof(unsigned long)`
is **platform-dependent**:

| Platform | `sizeof(unsigned long)` | C# mapping used | Match? |
|---|---|---|---|
| Linux x64 / macOS | 8 bytes | `ulong` / `nuint` | ✅ Exact |
| Windows x64 | 4 bytes | `ulong` / `nuint` (8 bytes) | ⚠️ Differs in size |

On Linux (the current target) all mappings are **exact**. On Windows the mismatch is benign in
practice because:
- All defined flag values fit in 32 bits (high 32 bits are always zero in C#)
- Little-endian layout means the native code reads the correct lower 32 bits
- `PaStreamParameters` total size is 32 bytes on both platforms (padding falls in the same place)

This is a known PortAudio cross-platform concern. The current approach (`ulong`/`nuint`) prioritises
Linux/macOS correctness. A future port to Windows would need to verify struct sizes via
`Marshal.SizeOf<PaStreamParameters>()` and compare with the native `sizeof`.

**Affected types:** `PaSampleFormat : ulong`, `PaStreamFlags : ulong`,
`PaStreamCallbackFlags : ulong`, `PaHostErrorInfo.errorCode : nint` (maps `long`, correct on
Linux), Windows-only structs (`PaWasapiStreamInfo`, `PaWinMmeStreamInfo`,
`PaWinDirectSoundStreamInfo`, `PaWinWDMKSInfo`) — all `nuint` fields match Windows ABI on
Windows (32-bit `unsigned long` fields → 32-bit offsets match), and match Linux ABI on Linux.

---

### ✅ Issue 7.5 — `PortAudioLibraryResolver.Install(loggerFactory)` logger silently ignored — **RESOLVED**

**Root cause:** `[ModuleInitializer]` in `PALibModuleInit` calls `Install()` (no logger) before
any user code runs, setting `_installed = true`. Any subsequent explicit call such as
`Install(myLoggerFactory)` at app startup hit the early-return guard and discarded the logger —
the resolver always logged to `NullLogger.Instance`.

**Fix:** Logger update is now separated from the install guard. A supplied `loggerFactory` is
always applied, even if the resolver was already registered:

```csharp
lock (Gate)
{
    if (loggerFactory != null)            // always upgrade logger when supplied
        _logger = loggerFactory.CreateLogger("PALib.Runtime");

    if (_installed) return;               // resolver registration is one-time only

    NativeLibrary.SetDllImportResolver(...);
    _installed = true;
}
```

This means `PALib.Smoke` and `PortAudioEngine` can call `Install(loggerFactory)` at startup to
get resolver diagnostic output as expected, while the `[ModuleInitializer]` still guarantees the
resolver is wired up even before `Install` is called explicitly.

---

### ✅ Issue 7.6 — `PaDelegates.cs` misnamed — **RESOLVED**

`Audio/PALib/Types/Core/PaDelegates.cs` contained the `PaConstants` class (constants only, no
delegates). Renamed to `PaConstants.cs` to match the type it declares.

---

## 8. Resolution Summary

| # | Issue | Status |
|---|---|---|
| 1.1 | `PortAudioLibraryResolver` required manual `Install()` call | ✅ Resolved — `[ModuleInitializer]` in `PALibModuleInit` |
| 2.1 | All `Native` classes were `public` — raw P/Invoke exposed | ✅ Resolved — all `internal`; `InternalsVisibleTo` for 3 consumers |
| 2.2 | `Pa_Sleep` on public API; used in managed write loop | ✅ Resolved — `internal` + replaced with `Thread.Sleep(1)` |
| 3.1 | `TraceCall` allocated params array + string-interpolation args even when disabled | ✅ Resolved (updated) — `TraceCall`/`BufferMeta` removed; all sites use inline `IsEnabled` guard |
| 3.2 | Tracing applied non-uniformly across lifecycle methods | ✅ Resolved — traces added for `GetDeviceInfo`, `Close/Start/Stop/AbortStream` |
| 4.1 | `Pa_OpenStream` / `Pa_IsFormatSupported` heap-allocated `PaStreamParameters` | ✅ Resolved — stack allocation via unsafe pointer |
| — | Native AOT compatibility | ✅ Verified — `<IsAotCompatible>true</IsAotCompatible>`, zero analyzer warnings |
| 7.1 | Missing `EntryPoint` on 29 `[LibraryImport]` stubs across 5 host-API files | ✅ Resolved — explicit `EntryPoint` added to all affected methods |
| 7.2 | `Pa_GetVersionText` missing from core `Native.cs` | ✅ Resolved — added with `[Obsolete]` attribute |
| 7.3 | `WaveFormatNative` was `public` and lacked platform guard | ✅ Resolved — made `internal`, added `IsSupportedPlatform` guard |
| 7.4 | `unsigned long` C type vs C# `ulong`/`nuint` on Windows | 📝 Documented — benign on Linux; Windows portability note added |
| 7.5 | `PortAudioLibraryResolver` logger silently dropped after `[ModuleInitializer]` | ✅ Resolved — logger update separated from install-guard logic |
| 7.6 | `PaDelegates.cs` misnamed — contained `PaConstants` | ✅ Resolved — renamed to `PaConstants.cs` |

---

## 9. Convention-Alignment with NDILib (March 2026)

This pass aligned PALib to the conventions established while rewriting NDILib.

### ✅ 9.1 — `TraceCall` / `BufferMeta` removed from `PALibLogging`

See updated §3.1 above. `TraceCall` and `BufferMeta` removed; `PtrMeta` retained.

### ✅ 9.2 — All `Native.cs` TraceCall call sites converted to inline `IsEnabled` guards

Files updated: `PALib/Native.cs`, `ASIO/Native.cs`, `WMME/Native.cs`, `WDMKS/Native.cs`,
`DirectSound/Native.cs`, `WASAPI/Native.cs`.

The pattern is now consistent across PALib and NDILib — no helper indirection, no allocation on
the disabled path.

