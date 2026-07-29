using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Every shipped base theme must give its <see cref="TopLevel"/> a working
/// <see cref="OverlayLayer"/>. Avalonia only enables the overlay layer when it finds a
/// <c>VisualLayerManager</c> NAMED <c>PART_VisualLayerManager</c> in the template
/// (<c>TopLevel</c> declares it as a template part). The in-repo Classic theme left all four of its
/// TopLevel templates' layer managers unnamed, so under Classic - HaPlay's STARTUP DEFAULT - there
/// was no overlay layer at all: everything that falls back to it (flyouts and menus when no
/// <c>IPopupImpl</c> is available, light-dismiss overlays, drag adorners, the text selector) was
/// silently disabled. Desktop hid it because the platform supplies real popup windows.
/// </summary>
public sealed class ThemeOverlayLayerTests
{
    [Theory]
    [InlineData(AppBaseTheme.Classic)]
    [InlineData(AppBaseTheme.Simple)]
    [InlineData(AppBaseTheme.Fluent)]
    public Task EveryBaseTheme_GivesTheWindowAnOverlayLayer(AppBaseTheme baseTheme) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ThemeOverlayLayerTests).Assembly)
            .Dispatch(() =>
            {
                HeadlessAppTheme.ApplyBaseTheme(baseTheme);
                var window = new Window { Width = 400, Height = 300, Content = new TextBlock { Text = "x" } };
                window.Show();
                try
                {
                    Assert.NotNull(OverlayLayer.GetOverlayLayer(window));
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);

    /// <summary>
    /// The user-visible consequence: with no overlay layer and no headless <c>IPopupImpl</c>,
    /// <c>FlyoutBase.ShowAt</c> threw "Unable to create IPopupImpl and no overlay layer is found".
    /// HaPlay's cue toolbar is a <c>MenuFlyout</c>, so this is the shape that actually bit.
    /// </summary>
    [Fact]
    public Task ClassicTheme_CanOpenAFlyout_WithoutAPlatformPopup() =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ThemeOverlayLayerTests).Assembly)
            .Dispatch(() =>
            {
                HeadlessAppTheme.ApplyBaseTheme(AppBaseTheme.Classic);
                var button = new Button { Content = "Add cue…" };
                var flyout = new MenuFlyout();
                flyout.Items.Add(new MenuItem { Header = "Add group" });
                button.Flyout = flyout;
                var window = new Window { Width = 400, Height = 300, Content = button };
                window.Show();
                try
                {
                    flyout.ShowAt(button);
                    Assert.True(flyout.IsOpen);
                    flyout.Hide();
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
}
