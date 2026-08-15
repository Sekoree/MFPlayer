using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Tests;

/// <summary>The status-bar chip names severities (2026-08-14 nit): two warnings must not read as
/// "2 issues" in red - red is reserved for a show that cannot play.</summary>
public sealed class StatusChipTests
{
    private static ProjectStatusReport Report(int errors, int warnings) => new(
        [
            .. Enumerable.Range(0, errors).Select(index => new StatusCheck(
                $"e{index}", CheckOutcome.Failed, "", "", [])),
            .. Enumerable.Range(0, warnings).Select(index => new StatusCheck(
                $"w{index}", CheckOutcome.Warning, "", "", [])),
        ],
        0);

    [Fact]
    public Task WarningsOnlyReadAsWarningsAndWearAmber() => ShellFixture.WithShell(shell =>
    {
        shell.Status = Report(errors: 0, warnings: 2);

        Assert.True(shell.IssuesAreWarningsOnly);
        Assert.Contains("2 warnings", shell.IssueSummary);
        Assert.DoesNotContain("issue", shell.IssueSummary);
    });

    [Fact]
    public Task AnErrorIsNamedAnErrorAndStaysRed() => ShellFixture.WithShell(shell =>
    {
        shell.Status = Report(errors: 1, warnings: 1);

        Assert.False(shell.IssuesAreWarningsOnly);
        Assert.Contains("1 error", shell.IssueSummary);
        Assert.Contains("1 warning", shell.IssueSummary);
    });
}
