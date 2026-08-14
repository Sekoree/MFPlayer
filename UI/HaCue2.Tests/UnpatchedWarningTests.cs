using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The unpatched-output warning names the output the operator actually selected.
/// </summary>
/// <remarks>
/// The markup used to hardcode "Lobby" into this sentence, so the one screen an operator opens to
/// answer "why is there no sound" pointed them at the wrong bus by name whenever anything else was
/// selected.
/// </remarks>
public sealed class UnpatchedWarningTests
{
    [Fact]
    public Task TheWarningNamesTheSelectedOutput() => ShellFixture.WithShell(shell =>
    {
        // "Unpatched" is red only when something actually SENDS here - a fed output with no device
        // cells is the silent-cue hazard the warning exists for.
        var channel = new LogicalAudioChannel { Name = "Balcony Sub", SortOrder = 99 };
        shell.Project.AudioPatch.LogicalChannels.Add(channel);
        ShellFixture.Bed(shell.Project).Sends.Add(
            new CueAudioSend { SourceChannel = 0, LogicalChannelId = channel.Id });
        shell.Audio.Refresh();

        var unpatched = shell.Audio.Outputs.Single(row => row.Name == "Balcony Sub");
        shell.Audio.SelectedOutput = unpatched;

        Assert.True(shell.Audio.IsSelectedOutputUnpatched);
        Assert.Contains("Balcony Sub", shell.Audio.UnpatchedWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("Lobby", shell.Audio.UnpatchedWarning, StringComparison.Ordinal);
    });
}
