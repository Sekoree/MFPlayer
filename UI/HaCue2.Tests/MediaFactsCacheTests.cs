using HaCue2.Core.Model;
using HaCue2.Session;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The probe cache answers for the file as it IS, not as it once was.
/// </summary>
/// <remarks>
/// Re-exporting a file over the same name is the normal workflow mid-production, and a cache keyed
/// by path alone kept the old duration and stream identities for the rest of the session - the same
/// staleness class <c>WaveformCache</c> has always keyed length + mtime to avoid.
/// </remarks>
public sealed class MediaFactsCacheTests
{
    [Fact]
    public async Task AReplacedFileIsReProbedAndItsNewDurationAdopted()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-facts");

        try
        {
            var path = Path.Combine(directory.FullName, "bed.wav");
            WriteWav(path, seconds: 1);

            var cue = new MediaCueNode { Number = "1", Label = "Bed", MediaPath = path };
            var project = new HaCueProject
            {
                Title = "facts-test",
                CueLists = [new CueList { Name = "Act", Cues = [cue] }],
            };

            var cache = new MediaFactsCache();
            cache.Refresh(project);
            var first = await WaitForDurationAsync(cache, project, cue.Id);
            Assert.InRange(first.TotalMilliseconds, 900, 1_100);

            // Same path, new content - a re-render landing over the old file.
            WriteWav(path, seconds: 2);
            cache.Refresh(project);
            var second = await WaitForDurationAsync(
                cache, project, cue.Id, above: TimeSpan.FromMilliseconds(1_500));
            Assert.InRange(second.TotalMilliseconds, 1_900, 2_100);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AFileThatAppearsAfterBeingProbedMissingIsPickedUp()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-facts");

        try
        {
            var path = Path.Combine(directory.FullName, "late.wav");
            var cue = new MediaCueNode { Number = "1", Label = "Late", MediaPath = path };
            var project = new HaCueProject
            {
                Title = "facts-test",
                CueLists = [new CueList { Name = "Act", Cues = [cue] }],
            };

            var cache = new MediaFactsCache();
            cache.Refresh(project);
            await WaitUntilAsync(() => cache.Facts(path) is { IsKnown: false });

            // The media arrives - copied in mid-session. The next refresh must notice, without an
            // app restart clearing the broken badge.
            WriteWav(path, seconds: 1);
            cache.Refresh(project);
            var duration = await WaitForDurationAsync(cache, project, cue.Id);
            Assert.InRange(duration.TotalMilliseconds, 900, 1_100);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<TimeSpan> WaitForDurationAsync(
        MediaFactsCache cache, HaCueProject project, Guid cueId, TimeSpan? above = null)
    {
        TimeSpan? found = null;
        await WaitUntilAsync(() =>
        {
            found = cache.DurationsIn(project, null).GetValueOrDefault(cueId) is { Ticks: > 0 } value
                ? value
                : null;
            return found is { } duration && duration > (above ?? TimeSpan.Zero);
        });
        return found!.Value;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("the probe answer did not arrive inside the timeout");
            await Task.Delay(25);
        }
    }

    /// <summary>A canonical little PCM WAV whose declared duration FFmpeg reads exactly.</summary>
    private static void WriteWav(string path, int seconds)
    {
        const int rate = 8_000;
        var dataLength = rate * seconds * 2;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // mono
        writer.Write(rate);
        writer.Write(rate * 2);      // byte rate
        writer.Write((short)2);      // block align
        writer.Write((short)16);     // bits per sample
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}
