using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// P4 close-out (plan §3.1): the property drawer never shows a hidden/stale tab after a cue
/// switch, and remembers the last tab per cue type - the A→B→A scenario from the plan.
/// </summary>
public sealed class CueDrawerTabMemoryTests
{
    private const int AudioTabIndex = 2; // General, Preview, Audio, Video, Effects, Visualizer, …

    /// <summary>Runs <paramref name="action"/> on the headless UI session and OBSERVES the result.
    /// <c>Dispatch</c> hands back a Task; discarding it (the shape this helper used to have) threw every
    /// assertion failure inside the body away, so these tests passed no matter what the code under test
    /// did. Blocking here is safe - the body is synchronous and the xunit thread is not the session's
    /// dispatcher thread (the async sibling is <see cref="HeadlessDispatchExtensions.DispatchAsync"/>).</summary>
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CueDrawerTabMemoryTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static (CuePlayerViewModel Vm, Window Window, TabControl Tabs) HostDrawer()
    {
        // CuePlayerView hosts a ToggleSwitch, which needs a real control theme to template - see
        // HeadlessAppTheme. Without it Window.Show() throws instead of laying the view out.
        HeadlessAppTheme.ApplyProductionBaseTheme();
        var vm = new CuePlayerViewModel();
        var view = new CuePlayerView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        return (vm, window, view.FindControl<TabControl>("CueDrawerTabs")!);
    }

    [Fact]
    public void SwitchingCueTypes_NeverLeavesAHiddenTabSelected()
    {
        DispatchUi(static () =>
        {
            var (vm, window, tabs) = HostDrawer();
            try
            {
                vm.AddEmptyMediaCue();
                var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                vm.SelectedCueNode = null;
                vm.AddGroupCommand.Execute(null);
                var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                Dispatcher.UIThread.RunJobs();

                // Operator works on the media cue's Audio tab.
                vm.SelectedCueNode = media;
                Dispatcher.UIThread.RunJobs();
                tabs.SelectedIndex = AudioTabIndex;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(AudioTabIndex, tabs.SelectedIndex);
                Assert.True(
                    Assert.IsType<TabItem>(tabs.Items[AudioTabIndex]).IsVisible,
                    "the Audio tab must be a real, visible choice for a media cue");

                // Switch to the group: the Audio tab is hidden for groups, so the drawer must not
                // keep it selected (the original stale/blank-drawer bug).
                vm.SelectedCueNode = group;
                Dispatcher.UIThread.RunJobs();
                var groupTab = Assert.IsType<TabItem>(tabs.SelectedItem);
                Assert.True(groupTab.IsVisible, "drawer landed on a hidden tab for the group cue");
                Assert.NotEqual(AudioTabIndex, tabs.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // REGRESSION GUARD for a defect that shipped silently for months.
    //
    // The view recorded the per-kind memory from EVERY TabControl.SelectionChanged, but the restore
    // that reads it (EnsureVisibleDrawerTabSelected) is POSTED at DispatcherPriority.Loaded. Selecting
    // a cue flips the per-kind IsVisible bindings synchronously, and the TabControl answers by churning
    // its own selection back to index 0 - three SelectionChanged events, all raised while
    // SelectedCueNode is ALREADY the incoming cue. The incoming kind's remembered index was therefore
    // overwritten with 0 before the posted restore read it, and the restore then "landed on" the
    // General tab it had just written: after selecting Audio (memory = {Media:2}) and switching
    // Media→Group→Media the dictionary read {Media:0, Group:0} with the drawer on General.
    //
    // It was invisible until 2026-07 because this class's DispatchUi discarded the Task returned by
    // HeadlessUnitTestSession.Dispatch, so every assertion in the body was thrown away. Fixed in the
    // view with a switch-depth counter that suppresses recording while a cue switch retargets the
    // drawer (CuePlayerView.axaml.cs `_drawerTabSwitchDepth`).
    [Fact]
    public void SwitchingCueTypes_RestoresThePerTypeTab()
    {
        DispatchUi(static () =>
        {
            var (vm, window, tabs) = HostDrawer();
            try
            {
                vm.AddEmptyMediaCue();
                var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                vm.SelectedCueNode = null;
                vm.AddGroupCommand.Execute(null);
                var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
                Dispatcher.UIThread.RunJobs();

                vm.SelectedCueNode = media;
                Dispatcher.UIThread.RunJobs();
                tabs.SelectedIndex = AudioTabIndex;
                Dispatcher.UIThread.RunJobs();

                vm.SelectedCueNode = group;
                Dispatcher.UIThread.RunJobs();

                // Back to the media cue: the Audio tab is restored (per-type memory).
                vm.SelectedCueNode = media;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(AudioTabIndex, tabs.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
