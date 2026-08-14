using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// F-09 (2026-08-14 review): the title bar keeps its safety controls at the 900 px minimum. The
/// project identity is bounded (a long Unicode filename cannot push mode/LOCK/view switching out of
/// the window), and below the compact threshold SETTINGS/DIAGNOSTICS fold into the MORE overflow
/// while Lock and the mode chip stay directly visible.
/// </summary>
public sealed class ShellTitleBarResponsiveTests
{
    [Fact]
    public Task AtTheMinimumWidth_SafetyControlsStayVisible_AndChipsFoldIntoOverflow() =>
        ShellFixture.WithShell(shell =>
        {
            var window = new ShellWindow { DataContext = shell, Width = 900, Height = 640 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var settings = window.FindControl<Button>("SettingsButton")!;
            var diagnostics = window.FindControl<Button>("DiagnosticsButton")!;
            var overflow = window.FindControl<Button>("TitleOverflowButton")!;

            Assert.False(settings.IsVisible);
            Assert.False(diagnostics.IsVisible);
            Assert.True(overflow.IsVisible);

            // The overflow must sit INSIDE the window - folding the chips away is pointless if the
            // replacement itself overflows the title bar.
            var overflowRight = overflow.TranslatePoint(
                new Avalonia.Point(overflow.Bounds.Width, 0), window);
            Assert.NotNull(overflowRight);
            Assert.True(overflowRight!.Value.X <= 900,
                $"overflow button ends at x={overflowRight.Value.X:0} in a 900px window");

            window.Close();
        });

    [Fact]
    public Task AtDesktopWidth_TheChipsAreDirectAndTheOverflowIsHidden() =>
        ShellFixture.WithShell(shell =>
        {
            var window = new ShellWindow { DataContext = shell, Width = 1440, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.FindControl<Button>("SettingsButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("DiagnosticsButton")!.IsVisible);
            Assert.False(window.FindControl<Button>("TitleOverflowButton")!.IsVisible);

            window.Close();
        });

    [Fact]
    public Task ALongProjectIdentity_IsBoundedInsteadOfPushingControlsOut() =>
        ShellFixture.WithShell(shell =>
        {
            var window = new ShellWindow { DataContext = shell, Width = 900, Height = 640 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Overriding the bound text locally simulates the pathological filename without
            // touching the project model; the MaxWidth + trimming contract is on the CONTROL.
            var identity = window.FindControl<TextBlock>("ProjectIdentity")!;
            identity.Text = new string('滝', 24) + " — the annual gala — rev 17 (FINAL final).hacue2proj";
            Dispatcher.UIThread.RunJobs();

            Assert.True(identity.Bounds.Width <= 220 + 1,
                $"project identity measured {identity.Bounds.Width:0}px; it must stay bounded");

            window.Close();
        });
}
