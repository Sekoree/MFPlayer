using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>A11Y-02 acceptance smoke: soundboard tiles must be real, keyboard-focusable Buttons that a
/// screen reader can name - not pointer-only Borders.</summary>
public sealed class SoundboardAccessibilityTests
{
    [Fact]
    // Returns the Dispatch Task so xunit awaits it: the earlier `void` body DISCARDED it, which
    // threw away every assertion failure raised inside the dispatched lambda (the test passed
    // no matter what the code under test did).
    public Task SoundboardTiles_AreFocusableButtons_WithAutomationNames() =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SoundboardAccessibilityTests).Assembly)
            .Dispatch(static () =>
            {
                // The headless TestApp ships no Application.Styles, so an ItemsControl gets Avalonia's
                // bare fallback template - no ItemsPresenter, no realized tiles, and this test's
                // GetVisualDescendants<Button>() came back EMPTY. Stand up the app's real theme first.
                HeadlessAppTheme.ApplyProductionBaseTheme();
                var vm = new SoundboardWorkspaceViewModel();
                var board = vm.Boards[0];
                vm.SelectedBoard = board;
                board.BindTile(board.Tiles[0], "/tmp/sting.wav");

                var window = new Window { Width = 900, Height = 640, Content = new SoundboardView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs(); // flush the initial layout so ItemsControl realizes its tiles

                var tiles = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b => b.Classes.Contains("tile"))
                    .ToList();

                Assert.NotEmpty(tiles); // tiles realized as Buttons, not Borders
                Assert.All(tiles, t => Assert.True(t.Focusable, "a soundboard tile is not keyboard-focusable"));

                var bound = tiles.FirstOrDefault(t => t.DataContext is SoundboardTileViewModel { IsBound: true });
                Assert.NotNull(bound);
                Assert.False(
                    string.IsNullOrEmpty(AutomationProperties.GetName(bound!)),
                    "a bound tile exposes no automation name to screen readers");

                window.Close();
            }, CancellationToken.None);
}
