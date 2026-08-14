using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace S.Media.NativeInterop;

/// <summary>
/// F-12 (2026-08-14 review): coalesces libasound's stderr diagnostic flood into one structured
/// summary line. ALSA prints every device-probe failure ("unable to open slave", missing PCM
/// definitions, …) straight to stderr - dozens to hundreds of repeated lines per backend
/// enumeration on a typical desktop, drowning the actionable log (and the same spam has buried a
/// crashed test host). Installing an <c>snd_lib_error_set_handler</c> routes the messages here
/// instead, where they are counted per distinct pattern and reported ONCE per enumeration through
/// the wrapper's own logger; <c>MFP_ALSA_VERBOSE=1</c> opts back into the raw native stderr.
/// </summary>
/// <remarks>
/// <para>
/// Source-linked into the audio wrappers (PALib/MALib) like
/// <see cref="SystemFirstNativeLibraryResolver"/>, so each carries its own copy: the handler is
/// process-wide in libasound, and whichever wrapper installed last receives the messages - both
/// installs are idempotent per assembly and the summaries simply report what each handler saw.
/// Everything here is BEST-EFFORT: any failure (no libasound, no export, non-Linux) leaves the
/// native behavior untouched.
/// </para>
/// <para>
/// The native handler type is variadic (<c>void (*)(const char *file, int line, const char
/// *function, int err, const char *fmt, ...)</c>). The managed callback declares only the named
/// prefix and ignores the varargs - safe on the supported Linux ABIs (SysV AMD64, AAPCS64): the
/// named pointer/int arguments arrive in the same registers whether or not the call is variadic,
/// and the caller cleans up. The format ARGUMENTS are therefore not expanded - the pattern string
/// itself (with its <c>%s</c> placeholders) is what the summary reports, which is exactly the
/// dedupe key wanted here.
/// </para>
/// </remarks>
internal static unsafe class AlsaDiagnosticsSilencer
{
    private const int MaxDistinctPatterns = 16;

    private static int _installState; // 0 = not tried, 1 = installed, 2 = unavailable/opted-out
    private static long _suppressedSinceLastSummary;
    private static readonly ConcurrentDictionary<string, int> Patterns = new(StringComparer.Ordinal);

    /// <summary>Installs the coalescing handler (Linux only, idempotent, best-effort). Call before
    /// the first native call that probes ALSA devices.</summary>
    internal static void Install()
    {
        if (Interlocked.CompareExchange(ref _installState, 2, 0) != 0)
            return; // already tried (installed or unavailable)

        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("MFP_ALSA_VERBOSE") == "1")
            return;

        try
        {
            if (!NativeLibrary.TryLoad("libasound.so.2", out var asound)
                && !NativeLibrary.TryLoad("libasound.so", out asound))
                return;

            if (!NativeLibrary.TryGetExport(asound, "snd_lib_error_set_handler", out var export))
                return;

            var setHandler = (delegate* unmanaged[Cdecl]<nint, int>)export;
            setHandler((nint)(delegate* unmanaged[Cdecl]<nint, int, nint, int, nint, void>)&HandleAlsaError);
            Volatile.Write(ref _installState, 1);
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Leave the native stderr behavior in place.
        }
    }

    /// <summary>Logs one summary line for everything suppressed since the previous call (typically:
    /// once after each backend initialization/enumeration), then resets the delta. A quiet probe
    /// logs nothing.</summary>
    internal static void LogSummary(ILogger logger, string stage)
    {
        if (Volatile.Read(ref _installState) != 1)
            return;

        var suppressed = Interlocked.Exchange(ref _suppressedSinceLastSummary, 0);
        if (suppressed == 0)
            return;

        var patterns = Patterns.ToArray();
        Patterns.Clear();
        logger.LogInformation(
            "ALSA emitted {Count} diagnostic message(s) during {Stage} (missing/unopenable devices are "
            + "normal on a desktop; set MFP_ALSA_VERBOSE=1 for the raw stream). Distinct: {Patterns}",
            suppressed,
            stage,
            string.Join(" · ", patterns
                .OrderByDescending(entry => entry.Value)
                .Take(MaxDistinctPatterns)
                .Select(entry => $"{entry.Key} ×{entry.Value}")));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleAlsaError(nint file, int line, nint function, int err, nint fmt)
    {
        // An exception escaping an UnmanagedCallersOnly callback is a process crash - swallow all.
        try
        {
            Interlocked.Increment(ref _suppressedSinceLastSummary);
            var functionName = function != 0 ? Marshal.PtrToStringUTF8(function) : null;
            var pattern = fmt != 0 ? Marshal.PtrToStringUTF8(fmt) : null;
            var key = $"{functionName ?? "?"}: {pattern ?? "?"}";
            // Bounded distinct set: known patterns keep counting, novel ones stop being tracked
            // once the cap is reached (the total above still counts everything).
            if (Patterns.Count >= MaxDistinctPatterns && !Patterns.ContainsKey(key))
                return;
            Patterns.AddOrUpdate(key, 1, static (_, count) => count + 1);
        }
        catch
        {
            // Counting failed; the message is dropped - still better than crashing the process.
        }
    }
}
