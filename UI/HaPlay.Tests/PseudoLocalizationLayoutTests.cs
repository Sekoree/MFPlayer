using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaApps.Localization;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// F-21 acceptance, HaPlay side: the cue-list command bar keeps every command inside the view at
/// the worst supported width when every label expands ~35% and goes non-ASCII (pseudo-loc). Same
/// contract as <see cref="CueListCommandBarLayoutTests"/>, under stress copy.
/// </summary>
public sealed class PseudoLocalizationLayoutTests
{
    [Fact]
    public Task CueListCommands_SurvivePseudoLocalizationAtTheMinimumWorkspaceWidth() =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(PseudoLocalizationLayoutTests).Assembly)
            .DispatchGuarded(() =>
            {
                PseudoLocalization.ForceEnabled = true;
                try
                {
                    HeadlessAppTheme.ApplyBaseTheme(AppBaseTheme.Simple);
                    var view = new CuePlayerView { DataContext = new CuePlayerViewModel() };
                    var window = new Window { Width = 540, Height = 700, Content = view };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    var commands = view.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.IsEffectivelyVisible)
                        .Take(12)
                        .ToList();
                    Assert.NotEmpty(commands);

                    foreach (var command in commands)
                    {
                        var origin = command.TranslatePoint(new Point(0, 0), view);
                        Assert.NotNull(origin);
                        var right = origin!.Value.X + command.Bounds.Width;
                        Assert.True(right <= view.Bounds.Width + 0.5,
                            $"a pseudo-localized command clips (ends at {right:0.#}, view is {view.Bounds.Width:0.#})");
                    }

                    window.Close();
                }
                finally
                {
                    PseudoLocalization.ForceEnabled = null;
                }
            }, CancellationToken.None);
}
