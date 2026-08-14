using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaApps.Localization;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// F-21 acceptance: the complete shell renders under pseudo-localization (≥35% expansion +
/// non-ASCII markers) - now that every operator string flows through <c>Strings</c>, the whole
/// surface can be stress-tested for layouts sized to the English copy.
/// </summary>
public sealed class PseudoLocalizationTests
{
    [Fact]
    public void Apply_ExpandsAccentsAndBrackets_ButPreservesFormatPlaceholders()
    {
        PseudoLocalization.ForceEnabled = true;
        try
        {
            var result = PseudoLocalization.Apply("Save {0:0.##} items");

            Assert.StartsWith("⟦", result);
            Assert.EndsWith("⟧", result);
            Assert.Contains("{0:0.##}", result);                       // placeholders survive verbatim
            Assert.DoesNotContain("Save", result);                     // display text is accented
            Assert.True(result.Length >= "Save {0:0.##} items".Length * 1.3,
                $"expected ≥35% expansion; got {result.Length} chars");

            // string.Format must still work on the pseudo-localized pattern.
            var formatted = string.Format(result, 12.5);
            Assert.Contains("12.5", formatted);
        }
        finally
        {
            PseudoLocalization.ForceEnabled = null;
        }
    }

    [Fact]
    public void Apply_IsIdentityWhenDisabled()
    {
        PseudoLocalization.ForceEnabled = false;
        try
        {
            Assert.Equal("GO", PseudoLocalization.Apply("GO"));
        }
        finally
        {
            PseudoLocalization.ForceEnabled = null;
        }
    }

    [Fact]
    public Task TheShellTitleBar_SurvivesPseudoLocalizationAtTheMinimumWidth() =>
        ShellFixture.WithShell(shell =>
        {
            // Same contract as ShellTitleBarResponsiveTests, under expanded copy: the safety
            // controls stay inside a 900px window when every label grows ~35% and goes non-ASCII.
            PseudoLocalization.ForceEnabled = true;
            try
            {
                var window = new ShellWindow { DataContext = shell, Width = 900, Height = 640 };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var overflow = window.FindControl<Button>("TitleOverflowButton")!;
                Assert.True(overflow.IsVisible);
                var overflowRight = overflow.TranslatePoint(
                    new Avalonia.Point(overflow.Bounds.Width, 0), window);
                Assert.NotNull(overflowRight);
                Assert.True(overflowRight!.Value.X <= 900,
                    $"pseudo-localized overflow button ends at x={overflowRight.Value.X:0} in a 900px window");

                window.Close();
            }
            finally
            {
                PseudoLocalization.ForceEnabled = null;
            }
        });
}
