using System;
using System.Collections.ObjectModel;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// The Cue player's placement canvas mirrors the COMPOSITION, always - never an output window.
/// <para><b>This class asserted the opposite until 2026-07-30, on purpose and wrongly.</b> The canvas used to
/// follow the live aspect of a resizable local window the composition fed, reasoning that the editor should
/// look like what the operator sees on that output. But a placement's DestX/Y/Width/Height are normalized
/// against the composition, so a canvas drawn at the window's aspect draws every placement at the wrong shape
/// AND the wrong position - and resizing the output window visibly moved rectangles nobody had touched
/// (reported from a live show file). Where the composition then lands inside a differently-shaped output is a
/// separate, later mapping step with its own editor (<c>CompositionOutputLayoutDialog</c>), which is where
/// letterboxing is authored and seen.</para>
/// <para>The two tests below are the old ones inverted, keeping their exact fixtures - a windowed output and a
/// fullscreen one reporting a live raster - because those are precisely the cases that used to bend the
/// canvas.</para>
/// </summary>
public sealed class CuePlayerCompositionCanvasTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerCompositionCanvasTests).Assembly)
            .DispatchGuarded(body, System.Threading.CancellationToken.None)
            .GetAwaiter().GetResult();

    [Fact]
    public void PlacementCanvasAspect_IgnoresABoundLocalWindow_AndItsResizes()
    {
        RunUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var lineId = Guid.NewGuid();
            var def = new LocalVideoOutputDefinition(
                lineId, "Screen", VideoOutputEngine.AvaloniaOpenGl, VideoSurfaceMode.Windowed,
                ScreenIndex: 0, WindowWidth: 800, WindowHeight: 600);
            var line = new OutputLineViewModel(def, _ => { });
            vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { line });

            vm.AddCueListCommand.Execute(null);     // creates + selects a cue list
            vm.AddCompositionCommand.Execute(null); // adds + selects a composition (default 1920x1080)
            vm.AddVideoOutputCommand.Execute(null); // binds the composition to the local output line

            // The composition's 1920x1080, NOT the 800x600 window it feeds.
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);

            // Resizing that window changes nothing: no placement moved, so nothing may appear to move.
            line.ReplaceDefinition(def with { WindowWidth = 1000, WindowHeight = 500 });
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);

            // Dropping the binding changes nothing either - it never depended on the binding.
            vm.RemoveVideoOutputCommand.Execute(null);
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);
        });
    }

    [Fact]
    public void PlacementCanvasAspect_IgnoresAFullscreenOutputsLiveRaster()
    {
        RunUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var lineId = Guid.NewGuid();
            var line = new OutputLineViewModel(
                new LocalVideoOutputDefinition(
                    lineId, "Screen", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.FullScreen,
                    ScreenIndex: 0, WindowWidth: null, WindowHeight: null),
                _ => { });
            vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { line });
            vm.AddCueListCommand.Execute(null);
            vm.AddCompositionCommand.Execute(null);
            vm.AddVideoOutputCommand.Execute(null);

            // A 16:9 composition shown on a 16:9 screen at a different raster is still edited at 16:9…
            line.ReportLiveVideoSize(2560, 1440);
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);

            // …and so is one shown on a screen of a completely different shape. This is the letterbox case:
            // the composition does not change shape because its output does - the output mapping handles it.
            line.ReportLiveVideoSize(1024, 768);
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);

            // The live raster is still reported (other features read it); it just no longer bends the canvas.
            var definition = Assert.IsType<LocalVideoOutputDefinition>(line.Definition);
            Assert.Null(definition.WindowWidth);
            Assert.Null(definition.WindowHeight);
        });
    }

    [Fact]
    public void PlacementCanvasAspect_TracksTheSelectedPlacementsOwnComposition()
    {
        RunUi(() =>
        {
            // Two compositions of different shapes: the canvas follows whichever one the selected placement
            // belongs to, so editing a 4:3 placement is not done on a 16:9 canvas.
            var vm = new CuePlayerViewModel();
            vm.AddCueListCommand.Execute(null);
            vm.AddCompositionCommand.Execute(null);
            var wide = vm.SelectedComposition!;
            wide.Width = 1920;
            wide.Height = 1080;
            vm.AddCompositionCommand.Execute(null);
            var tall = vm.SelectedComposition!;
            tall.Width = 1024;
            tall.Height = 768;

            vm.AddEmptyMediaCue();
            vm.AddVideoPlacementCommand.Execute(null);
            var placement = vm.SelectedVideoPlacement!;

            placement.CompositionId = wide.Id;
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);
            placement.CompositionId = tall.Id;
            Assert.Equal(1024.0 / 768.0, vm.PlacementCanvasAspect, 3);
        });
    }
}
