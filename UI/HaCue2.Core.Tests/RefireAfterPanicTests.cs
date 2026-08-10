using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Field incident 2026-08-10: fire a video cue alone, PANIC, then fire its parent all-together group
/// — the same cue's video layer froze on its first frame (slot overflow + sampling repeats climbing
/// at pump rate, every sibling fine) until another panic and re-fire cleared it.
/// </summary>
public sealed class RefireAfterPanicTests
{
    /// <summary>A few seconds of colour bars, decoded for real. Null where ffmpeg is unavailable.</summary>
    private static string? MakeClip(string directory, string name, int seconds)
    {
        var path = Path.Combine(directory, name);
        try
        {
            // Video AND audio, like the incident's ProRes clips: the audio leg is what makes the
            // voice a clocked participant in the group's master election, which the panic disturbs.
            using var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    $"-v error -f lavfi -i testsrc=size=320x240:rate=30:duration={seconds} "
                    + $"-f lavfi -i sine=frequency=440:sample_rate=48000:duration={seconds} "
                    + $"-pix_fmt yuv420p -c:v libx264 -c:a aac -shortest \"{path}\" -y",
                UseShellExecute = false,
            });
            if (ffmpeg is null)
                return null;
            ffmpeg.WaitForExit(60_000);
            return File.Exists(path) ? path : null;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Group_refire_after_panic_advances_the_previously_sounding_video_layer()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-refire-panic");
        try
        {
            if (MakeClip(directory.FullName, "bg.mp4", 30) is not { } bg
                || MakeClip(directory.FullName, "ia.mp4", 30) is not { } ia)
            {
                return; // no ffmpeg on this box
            }

            var fixture = new TestProject();
            var project = fixture.Project;
            fixture.Cyc.Width = 320;
            fixture.Cyc.Height = 240;
            fixture.Cyc.FramesPerSecond = 30;

            var bgCue = new MediaCueNode
            {
                Number = "1.1",
                Label = "BG",
                MediaPath = bg,
                Placements =
                [
                    new LayerPlacement { CompositionId = fixture.Cyc.Id, LayerIndex = 0 },
                ],
            };
            var iaCue = new MediaCueNode
            {
                Number = "1.2",
                Label = "IA",
                MediaPath = ia,
                Placements =
                [
                    new LayerPlacement { CompositionId = fixture.Cyc.Id, LayerIndex = 2 },
                ],
            };
            var group = new GroupCueNode
            {
                Number = "1",
                Label = "Group",
                FireMode = GroupFireMode.AllTogether,
                Children = [bgCue, iaCue],
            };
            var list = new CueList { Name = "Show", Cues = [group], StandbyCueId = group.Id };
            project.CueLists = [list];

            await using var host = await ShowHost.StartAsync(project, backend: null, headless: true);

            // 1. Fire the BG child alone and let it run.
            Assert.True(await host.FireAsync(bgCue.Id));
            await Task.Delay(1_000);

            // 2. Panic (short fade), as the operator did.
            await host.PanicAsync();
            await Task.Delay(500);

            // 3. Fire the parent group, which contains the same cue.
            Assert.True(await host.FireAsync(group.Id));
            await Task.Delay(1_500);

            // 4. The frozen-layer signature: SamplingRepeated/SlotOverflow climbing at pump rate.
            var before = host.CompositionStats().Single();
            await Task.Delay(1_000);
            var after = host.CompositionStats().Single();

            // Positive control first - without these the freeze assertions pass vacuously when the
            // group simply failed to open or the pump never ran.
            Assert.True(after.LayerCount >= 2,
                $"the group re-fire did not place both video layers (layers={after.LayerCount})");
            Assert.True(after.FramesComposited > before.FramesComposited,
                "the composition pump is not running after the group re-fire");

            var repeated = after.SourceSamplesRepeated - before.SourceSamplesRepeated;
            var overflow = after.SlotOverflowFrames - before.SlotOverflowFrames;
            Assert.True(repeated < 10,
                $"a video layer is frozen after panic+group-refire: {repeated} repeats in 1s (frozen ≈ 30)");
            Assert.True(overflow < 10,
                $"a video layer is not consuming frames after panic+group-refire: {overflow} overflows in 1s");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
