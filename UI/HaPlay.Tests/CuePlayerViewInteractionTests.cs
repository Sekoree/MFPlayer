using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlayerViewInteractionTests
{
    // "Add group" is no longer a standalone Button: the authoring row collapsed every add-a-cue affordance
    // into the "Add cue…" button's MenuFlyout (CuePlayerView.axaml, AddCueMenuButton). The old
    // FindButtonByContent(Strings.AddGroupButton) could not match a MenuItem and threw "Sequence contains no
    // matching element" - unnoticed for as long as DispatchUi discarded its Task. Open the flyout for real
    // and raise the item's Click exactly as MenuItem.RaiseClick does, so the flyout's Command binding (which
    // only resolves once the item is in the popup's tree) is still what's under test.
    [Fact]
    public void AddGroupMenuItem_Click_AddsGroupCue()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            // Simple, not the Classic default: opening a flyout headless needs the overlay layer that only
            // the PART_VisualLayerManager-naming Window templates enable. See HeadlessAppTheme.
            var window = HostInWindow(new CuePlayerView { DataContext = vm }, AppBaseTheme.Simple);
            try
            {
                var addCue = FindButtonByContent(window, Strings.AddCueMenuButton);
                var flyout = Assert.IsType<MenuFlyout>(addCue.Flyout);
                flyout.ShowAt(addCue);
                Dispatcher.UIThread.RunJobs();

                var addGroup = flyout.Items.OfType<MenuItem>()
                    .First(m => string.Equals(m.Header?.ToString(), Strings.AddGroupButton, StringComparison.Ordinal));
                Assert.NotNull(addGroup.Command); // the flyout's binding really resolved
                addGroup.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                var selected = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                Assert.Equal(CueNodeKind.Group, selected.Kind);
                Assert.Single(vm.VisibleNodes);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AddAudioRouteButton_Click_AddsRouteToSelectedMediaCue()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            vm.ApplyCueLists(
            [
                new CueList
                {
                    Name = "Act 1",
                    Nodes =
                    {
                        new MediaCueNode
                        {
                            Number = "1",
                            Label = "Track",
                            Source = new FilePlaylistItem("/tmp/track.wav"),
                        },
                    },
                },
            ]);
            vm.SelectedCueNode = vm.VisibleNodes[0];
            var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            Assert.Empty(media.AudioRoutes);

            var view = new CuePlayerView { DataContext = vm };
            var window = HostInWindow(view);
            try
            {
                // The button lives in the drawer's Audio tab, and a TabControl only realises the SELECTED
                // tab's content - without this the lookup finds nothing (it threw "Sequence contains no
                // matching element", unnoticed while DispatchUi discarded its Task).
                var tabs = view.FindControl<TabControl>("CueDrawerTabs")!;
                tabs.SelectedItem = tabs.Items.OfType<TabItem>()
                    .First(t => string.Equals(t.Header?.ToString(), Strings.AudioTabLabel, StringComparison.Ordinal));
                Dispatcher.UIThread.RunJobs();

                var addRoute = FindButtonByContent(window, Strings.AddAudioRouteButton);
                ClickButton(window, addRoute);
                Dispatcher.UIThread.RunJobs();

                Assert.Single(media.AudioRoutes);
                Assert.Single(vm.VisibleAudioRoutes);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Opt-in: drives real Task.Run hops → Dispatcher.InvokeAsync and asserts the status lands within a pumped
    // window. That window was missed even at a 20 s pump on a loaded CI runner, so gate it like the repo's
    // other non-deterministic integration tests (run locally with MFP_TIMING_TESTS=1).
    [TimingFact]
    public void Go_DispatchedStatusMessage_IsRaisedOnUiThread()
    {
        var seenFinalStatus = new ManualResetEventSlim(false);
        var statusRaisedOffUiThread = false;

        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddEmptyMediaCue();
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.SourceOrAction = "/tmp/test.mp3";
            vm.MediaCueExecutor = async (_, _) =>
            {
                await Task.Run(static () => Thread.Sleep(10));
                return "done";
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CuePlayerViewModel.StatusMessage)
                    || vm.StatusMessage?.Contains("done", StringComparison.Ordinal) != true)
                    return;
                statusRaisedOffUiThread = !Dispatcher.UIThread.CheckAccess();
                seenFinalStatus.Set();
            };

            vm.StandbySelectedCommand.Execute(null);
            vm.OnCueStarted(cue.Id);
            vm.GoCommand.Execute(null);
        });

        // Generous window: the executor hops through Task.Run on a thread pool the parallel test
        // collections keep saturated on CI - 2 s flaked on the Windows runner. Exits early on success.
        PumpUntil(() => seenFinalStatus.IsSet, TimeSpan.FromSeconds(20));

        Assert.True(seenFinalStatus.IsSet);
        Assert.False(statusRaisedOffUiThread);
    }

    // Opt-in for the same reason as Go_DispatchedStatusMessage above: identical Task.Run → InvokeAsync → pump
    // mechanism, so it shares the same rare CI-VM race. Runs locally with MFP_TIMING_TESTS=1.
    [TimingFact]
    public void Go_MediaExecutorReturnsWithoutCueStarted_RestoresCueToStandby()
    {
        var seenFailureStatus = new ManualResetEventSlim(false);
        CuePlayerViewModel? vm = null;
        CueNodeViewModel? cue = null;

        DispatchUi(() =>
        {
            vm = new CuePlayerViewModel();
            vm.AddEmptyMediaCue();
            cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            cue.SourceOrAction = "/tmp/test.mp4";
            vm.MediaCueExecutor = async (_, _) =>
            {
                await Task.Run(static () => Thread.Sleep(10));
                return "No cue video output could be acquired.";
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CuePlayerViewModel.StatusMessage)
                    || vm.StatusMessage?.Contains("No cue video output", StringComparison.Ordinal) != true)
                    return;
                seenFailureStatus.Set();
            };

            vm.StandbySelectedCommand.Execute(null);
            vm.GoCommand.Execute(null);
        });

        PumpUntil(() => seenFailureStatus.IsSet, TimeSpan.FromSeconds(20));

        DispatchUi(() =>
        {
            Assert.NotNull(vm);
            Assert.NotNull(cue);
            Assert.Null(vm!.CurrentCueNode);
            Assert.Same(cue, vm.StandbyCueNode);
            Assert.Same(cue, vm.SelectedCueNode);
            Assert.Contains("Failed to start", vm.StatusMessage);
            Assert.DoesNotContain("Triggered", vm.StatusMessage);
        });
    }

    [Fact]
    public void IdleCuePlacementEdit_FlagsShowDocumentStaleForNextFire()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var staleMarks = 0;
            var liveUpdates = 0;
            vm.CueClipModelStaleCallback = () => staleMarks++;
            vm.UpdateActiveCueVideoPlacementCallback = (_, _, _) => { liveUpdates++; return Task.CompletedTask; };

            vm.AddEmptyMediaCue();
            Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            vm.AddVideoPlacementCommand.Execute(null);
            var placement = Assert.Single(vm.VisibleVideoPlacements);

            // The cue was never fired → editing its placement must flag the backing show document stale (so the
            // next GO reloads the current geometry) rather than attempting a live update on a non-running cue.
            placement.DestX = 0.25;
            placement.DestWidth = 1.0;

            Assert.True(staleMarks > 0, "idle placement edit should flag the show document stale");
            Assert.Equal(0, liveUpdates);
        });
    }

    [Fact]
    public void ActiveCuePlacementEdit_PushesLiveUpdate_WithoutMarkingStale()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var staleMarks = 0;
            var liveUpdates = 0;
            vm.CueClipModelStaleCallback = () => staleMarks++;
            vm.UpdateActiveCueVideoPlacementCallback = (_, _, _) => { liveUpdates++; return Task.CompletedTask; };

            vm.AddEmptyMediaCue();
            var cue = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            vm.AddVideoPlacementCommand.Execute(null);
            var placement = Assert.Single(vm.VisibleVideoPlacements);

            vm.OnCueStarted(cue.Id); // now running → live path, not document reload

            placement.DestX = 0.25;

            Assert.True(liveUpdates > 0, "running cue placement edit should push a live update");
            Assert.Equal(0, staleMarks);
        });
    }

    [Fact]
    public void CueSearchBox_Typing_SelectsMatchAndUpdatesTreeSelection()
    {
        DispatchUi(() =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddEmptyMediaCue();
            var intro = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            intro.Label = "Intro video";
            vm.AddEmptyMediaCue();
            var music = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            music.Label = "Walk-in music";
            vm.AddEmptyMediaCue();
            var outro = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            outro.Label = "Outro video";

            var view = new CuePlayerView { DataContext = vm };
            var window = HostInWindow(view);
            try
            {
                var searchBox = window.GetVisualDescendants()
                    .OfType<TextBox>()
                    .First(t => t.Name == "CueSearchBox");

                searchBox.Text = "video"; // TwoWay binding drives the VM search
                Dispatcher.UIThread.RunJobs(); // flush the Loaded-priority grid-selection post

                Assert.Same(intro, vm.SelectedCueNode);
                Assert.Equal("1 of 2", vm.CueSearchStatus);

                vm.FindNextCueMatchCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(outro, vm.SelectedCueNode);
                Assert.Equal("2 of 2", vm.CueSearchStatus);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the headless UI session and OBSERVES the result.
    /// <c>Dispatch</c> hands back a Task; discarding it (the shape this helper used to have) threw every
    /// assertion failure inside the body away, so these tests passed no matter what the code under test
    /// did. Blocking here is safe - the body is synchronous and the xunit thread is not the session's
    /// dispatcher thread (the async sibling is <see cref="HeadlessDispatchExtensions.DispatchAsync"/>).</summary>
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerViewInteractionTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CuePlayerViewInteractionTests).Assembly);
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            // Wait for the pump to actually finish before re-testing the condition: discarding this Task
            // let the loop spin ahead of the dispatcher and burn the timeout without pumping.
            session.DispatchGuarded(static () => Dispatcher.UIThread.RunJobs(), CancellationToken.None)
                .GetAwaiter().GetResult();
            Thread.Sleep(10);
        }
    }

    private static Window HostInWindow(Control view, AppBaseTheme baseTheme = AppBaseTheme.Classic)
    {
        // CuePlayerView hosts a ToggleSwitch, which needs a real control theme to template - see
        // HeadlessAppTheme. Without it Window.Show() throws instead of laying the view out.
        HeadlessAppTheme.ApplyBaseTheme(baseTheme);
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs(); // finish the initial layout: templates applied, layers realised
        return window;
    }

    private static Button FindButtonByContent(Window window, string content) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => string.Equals(b.Content?.ToString(), content, StringComparison.Ordinal));

    private static void ClickButton(Window window, Button button)
    {
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
    }
}
