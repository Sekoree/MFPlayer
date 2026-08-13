using System.Diagnostics;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// A disposed container leaves the file it opened CLOSED.
/// </summary>
/// <remarks>
/// <para>
/// It did not. <c>Dispose</c> nulled <c>AVFormatContext.pb</c> before <c>avformat_close_input</c> — right for the
/// custom-IO opens, where <c>StreamAvioBridge</c> owns that context and frees it separately, but it ran on every
/// open. On the native file path FFmpeg owns pb and closing it is what releases the OS handle, so every container
/// opened from a PATH stayed open for the life of the process — on the success path as much as the failure one.
/// </para>
/// <para>
/// What that cost: a cue player probes every file an operator adds to a show. On Linux the descriptors simply
/// accumulated; on Windows the files also stayed LOCKED, so media already added could not be moved, renamed or
/// replaced until the app quit. It surfaced as a HaPlay drop test failing in CI on its CLEANUP rather than its
/// assertions, which is the kind of symptom that gets a test quarantined instead of a bug fixed.
/// </para>
/// <para>
/// Asserted per platform because neither probe answers on both: /proc/self/fd counts the real descriptors on
/// Linux, and Windows has no such view but does enforce sharing — so an exclusive open asks the same question in
/// the way that platform can answer, and is also precisely the thing the operator could not do.
/// </para>
/// </remarks>
public sealed class ContainerHandleReleaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mf-handle-release-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [RemuxFact]
    public void DisposingAReadableContainerReleasesTheFile()
    {
        var path = Path.Combine(_dir, "readable.flac");
        using (var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-nostdin -loglevel error -y -f lavfi -i sine=frequency=440:duration=1 \"{path}\"",
            RedirectStandardError = true,
        })!)
        {
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, $"ffmpeg input generation failed: {stderr}");
        }

        for (var i = 0; i < 5; i++)
            using (MediaContainerDecoder.Open(path)) { }

        AssertClosed(path);
    }

    /// <summary>
    /// The failure path, which needs no CLI: an empty file cannot be opened, and the handle taken on the way to
    /// finding that out must still come back. A dropped folder of unreadable files was the original repro.
    /// </summary>
    [FFmpegNativeFact]
    public void FailingToOpenAContainerStillReleasesTheFile()
    {
        var path = Path.Combine(_dir, "unreadable.flac");
        File.WriteAllBytes(path, []);

        for (var i = 0; i < 5; i++)
        {
            try { using var decoder = MediaContainerDecoder.Open(path); }
            catch { /* expected — the point is what happens to the handle */ }
        }

        AssertClosed(path);
    }

    private static void AssertClosed(string path)
    {
        if (Directory.Exists("/proc/self/fd"))
        {
            var held = Directory.GetFiles("/proc/self/fd").Count(fd =>
            {
                // Descriptors churn while the directory is walked; an entry that vanishes is not a match.
                try { return File.ResolveLinkTarget(fd, returnFinalTarget: true)?.FullName == path; }
                catch { return false; }
            });

            Assert.True(held == 0, $"{held} descriptor(s) still open on the file after every container was disposed");
            return;
        }

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
