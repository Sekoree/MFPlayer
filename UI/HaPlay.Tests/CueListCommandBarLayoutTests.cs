using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// F-10 (2026-08-14 review): the cue-list command row wraps instead of clipping. As a fixed Grid it
/// needed ~740 px while the supported minimum workspace (720 px window minus the expanded 180 px
/// sidebar) leaves ~540 - Output setup and Settings were cut off with no overflow affordance. These
/// lay the row out at the worst supported width and assert every command stays inside the view.
/// </summary>
public sealed class CueListCommandBarLayoutTests
{
    // View widths ≈ window width minus the expanded 180px sidebar and chrome paddings.
    [Theory]
    [InlineData(1100)] // comfortable: single line
    [InlineData(540)]  // the 720px MinWidth window's content area - the reported clipping case
    public Task ListCommands_NeverClipOffTheView(double viewWidth) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CueListCommandBarLayoutTests).Assembly)
            .DispatchGuarded(() =>
            {
                // CuePlayerView hosts a ToggleSwitch, which needs a real control theme to template.
                HeadlessAppTheme.ApplyBaseTheme(AppBaseTheme.Simple);
                var view = new CuePlayerView { DataContext = new CuePlayerViewModel() };
                var window = new Window { Width = viewWidth, Height = 700, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var commands = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible)
                    .Take(12)
                    .ToList();
                Assert.NotEmpty(commands);

                foreach (var command in commands)
                {
                    var origin = command.TranslatePoint(new Avalonia.Point(0, 0), view);
                    Assert.NotNull(origin);
                    var right = origin!.Value.X + command.Bounds.Width;
                    Assert.True(origin.Value.X >= -0.5,
                        $"a command clips off the left edge at {viewWidth}px (x={origin.Value.X:0.#})");
                    Assert.True(right <= view.Bounds.Width + 0.5,
                        $"a command clips off the right edge at {viewWidth}px (ends at {right:0.#}, view is {view.Bounds.Width:0.#})");
                }

                window.Close();
            }, CancellationToken.None);
}
