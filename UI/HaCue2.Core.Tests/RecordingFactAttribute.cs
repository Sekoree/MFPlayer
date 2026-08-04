using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS (rather than fails) when this machine's FFmpeg cannot
/// encode what a recording writes.
/// </summary>
/// <remarks>
/// <para>
/// The tests behind it arm a REAL recording — that is the whole point of them, and the reason they
/// catch a container that refuses its codec. But the H.264 encoder they need is libx264, which is GPL
/// and therefore absent from plenty of perfectly good FFmpeg builds: the Windows CI leg has one, and
/// every one of these failed there with "This FFmpeg build has no encoder for H264" while the Linux leg
/// (a GPL build on <c>LD_LIBRARY_PATH</c>) ran them green. A missing encoder is an environment fact, not
/// a defect in the recorder, and it must not gate CI.
/// </para>
/// <para>
/// The probe asks the exact question <see cref="ProjectRecorders"/> asks when arming — the same format
/// table, the same options, the same <c>probeEncoders</c> validation — so it can never disagree with
/// the code under test about what this build can write. Mirrors the framework's
/// <c>FFmpegNativeFactAttribute</c> / <c>LibAssFactAttribute</c> convention, including caching the
/// verdict once per process.
/// </para>
/// </remarks>
public sealed class RecordingFactAttribute : FactAttribute
{
    private static readonly string? UnavailableReason = Probe();

    private static string? Probe()
    {
        try
        {
            // The recorder tests' own shape: a 160x120 25 fps picture-only .mkv (H.264 + Matroska).
            var format = RecordFormats.Find("probe.mkv");
            if (format is null)
                return "the .mkv format is missing from RecordFormats — not an environment problem";

            var errors = RecordFormats
                .Options(format, channels: 0, sampleRate: 48_000, width: 160, height: 120, fps: 25)
                .Validate(probeEncoders: true);

            return errors.Count == 0 ? null : $"this FFmpeg build cannot write a recording: {string.Join("; ", errors)}";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // EnsureInitialized throws outright when the natives are absent or the wrong major version.
            return $"FFmpeg native not usable on this machine: {failure.GetType().Name}: {failure.Message}";
        }
    }

    public RecordingFactAttribute()
    {
        if (UnavailableReason is not null)
            Skip = UnavailableReason;
    }
}
