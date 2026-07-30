using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

/// <summary>
/// MEASURED layout audit of the Cue Player workspace: the view is realised for real in a headless
/// <see cref="Window"/> at several sizes, populated with realistic show content (three cue lists, cues
/// with long operator-authored names, a playlist group, a cue carrying MIDI/OSC triggers and a
/// timecode schedule, live Now Playing rows), and every visible control's laid-out rectangle is
/// compared against the innermost container that could still have shown it - see <see cref="LayoutProbe"/>.
///
/// <para>The facts in this file are measurements, not opinions. <see cref="LayoutSnapshot"/> prints the
/// full offender list to test output; run it with <c>--logger "console;verbosity=detailed"</c> to
/// reproduce the numbers quoted in the DEFECT_* comments below.</para>
///
/// <para>The DEFECT_* tests assert the CORRECT behaviour. They were authored <c>Skip</c>ped, naming the
/// five measured defects while the suite stayed green; every one of them is now UN-SKIPPED and passing
/// against the fixed views, so they are live regression guards. Each comment keeps the "before" numbers
/// that were measured against the broken layout alongside the "after" numbers they now pin.</para>
/// </summary>
public sealed class CuePlayerLayoutBoundsTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- diagnostics

    /// <summary>Not an assertion - the measurement itself. Prints every escaping control at each size
    /// so the numbers quoted below can be re-derived at any time.</summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void LayoutSnapshot(int width, int height)
    {
        output.WriteLine(DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var sb = new StringBuilder();
            sb.AppendLine($"--- CuePlayerView {width}x{height} (Classic) ---");
            sb.AppendLine($"cue tree: {scene.CueTree.Bounds}");
            sb.AppendLine($"drawer:   {scene.DrawerTabs.Bounds}");
            foreach (var escape in LayoutProbe.FindEscapes(scene.View))
                sb.AppendLine("  " + escape);
            sb.AppendLine("  -- Now Playing rows (strict containment) --");
            foreach (var row in scene.NowPlayingRows())
            foreach (var escape in LayoutProbe.FindEscapesWithin(row, scene.View))
                sb.AppendLine("  " + escape);
            return sb.ToString();
        }));
    }

    // ---------------------------------------------------------------- passing guards

    /// <summary>The transport row (§6 master trim fader + the Schedules/Triggers arm toggles + the
    /// state and MTC-chase chips) wants ~1670 px to lay out on a single line. This guard pins the
    /// large-window case - every control laid out inside the client at full HD, where it does still
    /// fit on one line. The narrow sizes, where wrapping is the correct answer, are covered by
    /// <see cref="DEFECT_TransportRow_OverflowsBelowAbout1670Px"/>.</summary>
    [Fact]
    public void TransportRow_ShowsEveryControl_AtFullHd()
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(1920, 1080);
            var escapes = LayoutProbe.FindEscapes(scene.View, scene.TransportRow);
            Assert.True(escapes.Count == 0, "transport row overflows at 1920x1080:\n" + Join(escapes));
        });
    }

    /// <summary>The drawer's General tab holds this round's widest new rows - the MIDI trigger binding
    /// editor (device / message / channel / number / min-value / Learn). Its ScrollViewer cannot scroll
    /// horizontally (Avalonia's default), so anything wider than the viewport is clipped away for good.
    /// Measured: the MIDI row needs ~880 px of drawer width; as a horizontal StackPanel it fitted from
    /// 900 px up and was cut off below that (at 800x600 the "Learn" toggle landed at x=746..815 outside
    /// a 795 px viewport). It is a WrapPanel now, so it folds instead of being clipped away.</summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void CueDrawer_TriggerAndScheduleEditors_FitHorizontally(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var escapes = LayoutProbe.FindEscapes(scene.View, scene.DrawerTabs)
                .Where(e => e.Axis.Contains('H'))
                .ToList();
            Assert.True(escapes.Count == 0, $"drawer content escapes horizontally at {width}x{height}:\n" + Join(escapes));
        });
    }

    /// <summary>EVERY drawer tab keeps its content inside the drawer VERTICALLY, at every size and for
    /// every cue kind. Reported from a real show file: selecting a Playlist group put the Group tab's
    /// options (fire mode + three checkboxes + a four-row grid, ~250 px) past the bottom of a drawer that
    /// the splitter's 88 px MinHeight and the 440 px MaxHeight can make much shorter. Four tabs - Group,
    /// Action, Comment and Cue preview - had a bare panel as their root instead of a ScrollViewer, so they
    /// CLIPPED rather than scrolled and the controls below the fold were unreachable. Measured before the
    /// fix, at 1024x600 with the Playlist group selected: the Group tab's panel laid out at
    /// <c>(11,456 1002x161)</c>, i.e. its bottom edge at y=617 in a 600 px window - the End-behavior combo
    /// row was off-screen. 1280x800 and 1920x1080 had the headroom to hide it, which is why this only
    /// showed up on a shorter window.
    /// <para>This walks the tab strip and measures each tab in turn, so a newly added tab is covered
    /// without touching this test - the sibling of <see cref="CueDrawer_TriggerAndScheduleEditors_FitHorizontally"/>
    /// on the other axis. A ScrollViewer is what makes it pass: its content may be taller than the
    /// viewport, but the viewport itself is what has to fit.</para></summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void CueDrawer_EveryTab_KeepsItsContentInsideTheDrawerVertically(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var offenders = new StringBuilder();
            var covered = 0;
            foreach (var cue in scene.CuesCoveringEveryDrawerTab())
            {
                scene.SelectCue(cue);
                foreach (var (index, header) in scene.VisibleDrawerTabs())
                {
                    scene.SelectDrawerTab(index);
                    covered++;
                    var escapes = LayoutProbe.FindEscapes(scene.View, scene.DrawerTabs)
                        .Where(e => e.Axis.Contains('V'))
                        .ToList();
                    if (escapes.Count > 0)
                        offenders.AppendLine($"cue '{cue.Kind}' tab '{header}':\n{Join(escapes)}");
                }
            }

            // Guards the guard: a visibility change that hid every tab would otherwise make this vacuous.
            Assert.True(covered >= 8, $"only {covered} drawer tabs were measured - the walk found nothing");

            Assert.True(
                offenders.Length == 0,
                $"drawer content escapes vertically at {width}x{height}:\n{offenders}");
        });
    }

    /// <summary>The Now Playing row's interactive parts (tap-to-seek progress bar, per-cue ✕) stay in
    /// the row. It excludes TextBlocks, so it held even while the row's LABEL did not - see
    /// <see cref="DEFECT_NowPlayingRowLabels_OverflowTheirRow"/>, which now covers those too. Worth
    /// pinning separately: the ✕ is a later Grid child than the label, so it paints on top of the label
    /// and stays clickable; reordering those children would bury it.</summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void NowPlayingRows_KeepTheirControlsInsideTheRow(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var rows = scene.NowPlayingRows().ToList();
            Assert.NotEmpty(rows);
            var escapes = rows
                .SelectMany(row => LayoutProbe.FindEscapesWithin(row, scene.View))
                .Where(e => !e.Path.EndsWith("TextBlock", StringComparison.Ordinal) && !e.Path.Contains("TextBlock '", StringComparison.Ordinal))
                .ToList();
            Assert.True(escapes.Count == 0, $"Now Playing controls escape their row at {width}x{height}:\n" + Join(escapes));
        });
    }

    /// <summary>The timeline editor's "Duck under…" dialog: fixed 460x360, CanResize=False. Nothing in
    /// it escapes - the one authoring surface in this round that is bounded correctly.</summary>
    [Fact]
    public void DuckUnderDialog_FitsItsWindow()
    {
        DispatchUi(() =>
        {
            HeadlessAppTheme.ApplyProductionBaseTheme();
            var vm = CueScene.BuildViewModel();
            var timelineGroup = vm.VisibleNodes[2];
            var window = new HaPlay.Views.Dialogs.DuckUnderDialog
            {
                DataContext = HaPlay.ViewModels.Dialogs.DuckUnderDialogViewModel.For(
                    timelineGroup.Children[0], timelineGroup.Children),
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var root = (Visual)window.GetVisualChildren().First();
                var escapes = LayoutProbe.FindEscapes(root);
                Assert.True(escapes.Count == 0, "duck dialog overflows:\n" + Join(escapes));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------- measured defects (fixed; now regression guards)

    // DEFECT 1 - the transport row cannot compress. FIXED.
    //
    // BEFORE (all line numbers below are pre-fix). CuePlayerView.axaml:171 declared
    // ColumnDefinitions="Auto,…,Auto,*": every control up to and including the two arm toggles sat in
    // an Auto column, so the row's width was a constant and the single star column absorbed nothing.
    // Measured (Classic theme, "Standby: (none)" state text and a
    // typical MTC chase string):
    //
    //   x range           control                                     first size at which it is whole
    //   846 .. 977        Schedules-armed toggle (§4, new)            >= 977 px
    //   983 .. 1103       Triggers-armed toggle (§6, new)             >= 1103 px
    //   1117 .. 1282      transport state chip                        >= 1282 px
    //   1288 .. 1662      timecode chase chip (D1, new)               >= 1662 px
    //
    // So at 1280x800 the chase chip is ENTIRELY off-screen (it starts 8 px past the window edge) and
    // the state chip is clipped; at 1024x600 the Triggers toggle itself is half gone. There is no
    // ScrollViewer or WrapPanel on the row, so none of it is reachable. Also reproduced under the
    // Simple theme (thresholds ~20 px higher).
    //
    // AFTER: the row is a WrapPanel inside the (single-cell) Grid this test scopes to, so what no
    // longer fits folds onto another line instead of running off the window. Zero escapes at every
    // size, and the row is 24 px tall at 1920 (ONE line, every child at the same x it had under the
    // Auto-column Grid, chase chip 1288..1662 - the wide-window look is unchanged), 40 px at 1280
    // (chips on line 2), 52 px at 1024 (Triggers toggle + chips on line 2), 66 px at 800.
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void DEFECT_TransportRow_OverflowsBelowAbout1670Px(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var escapes = LayoutProbe.FindEscapes(scene.View, scene.TransportRow);
            Assert.True(escapes.Count == 0, $"transport row overflows at {width}x{height}:\n" + Join(escapes));
        });
    }

    // DEFECT 2 - Now Playing row labels never trim. FIXED.
    //
    // BEFORE (all line numbers below are pre-fix). CuePlayerView.axaml:1516-1519 (ActiveCueViewModel)
    // and :1550-1560 (ActiveGroupViewModel) put a
    // TextBlock with TextTrimming="CharacterEllipsis" inside a HORIZONTAL StackPanel. A horizontal
    // StackPanel measures its children with infinite width, so trimming never engages: the label is
    // laid out at its full desired width and runs straight out of the row.
    //
    // Measured at EVERY size including 1920x1080 - the row border is 228-244 px wide and the label is
    // 1364 px (single cue) / 1584 px (group). Worse for the group row: because the label does not trim,
    // the siblings AFTER it in the same StackPanel - the "(n)" child count and the new PlaylistStatus
    // readout ("item 3/12 · pass 1/2") - are pushed to x=2419 / x=3315, i.e. entirely outside the
    // window. The cross-list list-name prefix added this round (ActiveCueViewModel.CueLabel) makes every
    // foreign-list row hit this.
    //
    // AFTER: both templates use the UpcomingChainItemViewModel shape - the badges in Auto columns, the
    // trimming label in a star column - so the label is measured against the row and ellipsises inside
    // it. Nothing escapes any Now Playing row at 1024x600 / 1280x800 / 1920x1080, and the (n) badge and
    // PlaylistStatus sit next to the label instead of at x=2419 / x=3315.
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 800)]
    [InlineData(1024, 600)]
    public void DEFECT_NowPlayingRowLabels_OverflowTheirRow(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            var escapes = scene.NowPlayingRows()
                .SelectMany(row => LayoutProbe.FindEscapesWithin(row, scene.View))
                .ToList();
            Assert.True(escapes.Count == 0, $"Now Playing row content escapes at {width}x{height}:\n" + Join(escapes));
        });
    }

    // DEFECT 3 - the bottom drawer starves the cue tree at short window heights. FIXED.
    //
    // BEFORE (all line numbers below are pre-fix). CuePlayerView.axaml:249 docked the drawer Expander
    // to the bottom of a DockPanel whose LastChildFill
    // child is the cue tree, and :264 gives the TabControl MinHeight=220 / MaxHeight=440. A docked child
    // takes its desired height FIRST, so with a media cue selected (the General tab is the tall one) the
    // drawer claims ~466 px and the tree gets whatever is left. Measured with a media cue selected:
    //
    //   window height   cue tree height
    //   1080            487
    //   800             207
    //   700             107
    //   600               7      <- the cue list, the workspace's primary surface, is gone
    //   560               0      + the drawer itself is clipped 11 px off the bottom of the window
    //
    // There is no GridSplitter between tree and drawer and no scroll on the outer DockPanel, so the only
    // recovery is collapsing the whole Expander.
    //
    // AFTER: tree and drawer each own a star row of one Grid (drawer capped at 470 px so tall windows
    // keep the old drawer size) with a GridSplitter between them. Same measurement, media cue selected:
    //
    //   window height   cue tree height   before
    //   1080            479               487
    //   800             316               207
    //   600             210                 7
    //   560             196                 0
    [Theory]
    [InlineData(1024, 600)]
    [InlineData(1280, 560)]
    public void DEFECT_CueTree_IsStarvedByTheDrawer(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(width, height);
            Assert.True(
                scene.CueTree.Bounds.Height >= 120,
                $"cue tree is {scene.CueTree.Bounds.Height:0} px tall at {width}x{height}");
        });
    }

    // DEFECT 4 - the drawer is clipped off the bottom below ~590 px of window height. FIXED.
    //
    // BEFORE. Same root cause as DEFECT 3, one step further: once the tree is at 0 the drawer's own 440 px
    // TabControl no longer fits either. Measured at 1280x560 the TabControl occupies y=131..571 against
    // a 560 px client - the bottom 11 px (and, as the window shrinks, whole rows of the selected tab)
    // are unreachable: the tab's ScrollViewer scrolls its CONTENT, not the clipped TabControl itself.
    //
    // AFTER: the drawer's star row bounds it and the Expander content is a Grid (a vertical StackPanel
    // measured the TabControl with infinite height, which is what let it keep its full desired size and
    // hang off the bottom). At 1280x560 the TabControl is y=373..560 against the 560 px client.
    [Fact]
    public void DEFECT_Drawer_IsClippedAtShortWindowHeights()
    {
        DispatchUi(() =>
        {
            using var scene = CueScene.Realise(1280, 560);
            var rect = LayoutProbe.RectIn(scene.DrawerTabs, scene.View)!.Value;
            Assert.True(
                rect.Bottom <= scene.View.Bounds.Height + 0.5,
                $"drawer bottom {rect.Bottom:0} exceeds the {scene.View.Bounds.Height:0} px client");
        });
    }

    // DEFECT 5 - timeline editor lane labels overflow into the canvas. FIXED.
    //
    // BEFORE (all line numbers below are pre-fix). TimelineEditorWindow.axaml:61-64 repeated the
    // DEFECT 2 pattern: TextTrimming inside a horizontal
    // StackPanel, this time in the fixed 180 px lane-label column (Grid ColumnDefinitions="180,*").
    // Measured at the window's own default size (1100x460) and at its MinWidth (640x280): the label is
    // 1056 px wide inside a 174 px border, i.e. it runs ~880 px into the timeline canvas column.
    //
    // It is also a LAYERING defect: the label ItemsControl is declared before the canvas ScrollViewer,
    // so the canvas (lane stripes, blocks, ruler - all semi-transparent) paints over the escaped text,
    // leaving cue names visibly smeared under the blocks instead of ellipsised at the column edge.
    //
    // AFTER: number in an Auto column, name in a star column inside the lane border, so the name trims
    // at the column edge and nothing reaches the canvas at all - which also settles the layering half:
    // there is no longer any escaped text for the canvas to paint over, whatever the declaration order.
    // Zero escapes at the window's default 1100x460 and at its 640x280 MinWidth.
    [Theory]
    [InlineData(1100, 460)]
    [InlineData(640, 280)]
    public void DEFECT_TimelineLaneLabels_OverflowIntoTheCanvas(int width, int height)
    {
        DispatchUi(() =>
        {
            using var scene = TimelineScene.Realise(width, height);
            var escapes = scene.LaneLabelBorders()
                .SelectMany(border => LayoutProbe.FindEscapesWithin(border, scene.Root))
                .ToList();
            Assert.True(escapes.Count == 0, $"timeline lane labels escape at {width}x{height}:\n" + Join(escapes));
        });
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs <paramref name="body"/> on the headless UI session and OBSERVES the result -
    /// discarding the Task <c>Dispatch</c> hands back throws every assertion failure away (see
    /// <see cref="HeadlessDispatchExtensions"/>). Blocking is safe: the body is synchronous and the
    /// xunit thread is not the session's dispatcher thread. The helper (rather than an inline
    /// <c>.GetResult()</c> in each test) also keeps xUnit1031 quiet.</summary>
    private static T DispatchUi<T>(Func<T> body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerLayoutBoundsTests).Assembly)
            .DispatchGuarded(body, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static void DispatchUi(Action body) => DispatchUi<object?>(() => { body(); return null; });

    private static string Join(IEnumerable<LayoutEscape> escapes) =>
        string.Join(Environment.NewLine, escapes.Select(e => "  " + e));
}

/// <summary>A realised <see cref="CuePlayerView"/> in a headless window, populated with show content.</summary>
internal sealed class CueScene(Window window, CuePlayerView view, CuePlayerViewModel vm) : IDisposable
{
    private const string LongLabel =
        "Act II — walk-in bed with a deliberately very long operator-authored cue name that wraps";

    private const string LongPath =
        "/mnt/media/shows/2026/summer-festival/main-stage/act-ii/walk-in-bed-master-2496-final-v3.wav";

    private const string ForeignListName = "Backup / understudy running order";

    public CuePlayerView View { get; } = view;

    public CuePlayerViewModel ViewModel { get; } = vm;

    /// <summary>The transport row: the Grid that owns the master trim fader.</summary>
    public Control TransportRow =>
        (Control)View.GetVisualDescendants().OfType<Slider>().First(s => s.Name == "MasterTrimSlider")
            .GetVisualAncestors().OfType<Grid>().First();

    public Control CueTree =>
        View.GetVisualDescendants().OfType<Control>().First(c => c.Name == "CueTreeGrid");

    public TabControl DrawerTabs => View.FindControl<TabControl>("CueDrawerTabs")!;

    /// <summary>One cue per authored kind, so selecting each in turn exposes every drawer tab (the tabs
    /// are visibility-bound to the selected cue's kind, so no single selection can reveal them all).
    /// Prefers a PLAYLIST group for the group kind - that is the tallest Group tab, and the shape the
    /// out-of-bounds report came from.</summary>
    public IEnumerable<CueNodeViewModel> CuesCoveringEveryDrawerTab()
    {
        var all = new List<CueNodeViewModel>();
        void Walk(IEnumerable<CueNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                all.Add(n);
                Walk(n.Children);
            }
        }

        Walk(ViewModel.SelectedCueList!.Nodes);
        foreach (var kind in all.Select(n => n.Kind).Distinct())
        {
            var ofKind = all.Where(n => n.Kind == kind).ToList();
            yield return ofKind.FirstOrDefault(n => n.IsPlaylistFireMode) ?? ofKind[0];
        }
    }

    public void SelectCue(CueNodeViewModel cue)
    {
        ViewModel.SelectedCueNode = cue;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The drawer tabs currently selectable for the selected cue, as (index, header).</summary>
    public IEnumerable<(int Index, string Header)> VisibleDrawerTabs()
    {
        var items = DrawerTabs.Items.OfType<TabItem>().ToList();
        for (var i = 0; i < items.Count; i++)
            if (items[i].IsVisible)
                yield return (i, items[i].Header?.ToString() ?? $"#{i}");
    }

    public void SelectDrawerTab(int index)
    {
        DrawerTabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    public IEnumerable<Visual> NowPlayingRows() =>
        View.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.DataContext is ActiveCueViewModel or ActiveGroupViewModel);

    public static CueScene Realise(int width, int height)
    {
        // CuePlayerView hosts a ToggleSwitch, which needs a real control theme to template - see
        // HeadlessAppTheme. Classic is the app's startup default AND the tightest metrics of the three
        // shipped base themes, so it is the right one to audit against.
        HeadlessAppTheme.ApplyProductionBaseTheme();
        var vm = BuildViewModel();
        var view = new CuePlayerView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Selection and playback come AFTER the first layout pass, exactly as they do in a show: the
        // drawer and the Now Playing panel then re-lay out into an already-sized workspace.
        vm.SelectedCueNode = vm.VisibleNodes[0];
        StartPlayback(vm);
        Dispatcher.UIThread.RunJobs();
        return new CueScene(window, view, vm);
    }

    public void Dispose() => window.Close();

    public static CuePlayerViewModel BuildViewModel()
    {
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([MainList(), BackupList(), AmbienceList()], LongPath);
        vm.TimecodeChaseStatus = "MTC 25 fps · 01:02:03:11 · chasing";
        return vm;
    }

    /// <summary>Two Now Playing rows: one single cue and one expanded group aggregate, both carrying the
    /// cross-list list-name prefix (<see cref="ActiveCueViewModel.ListName"/>) this round added.</summary>
    private static void StartPlayback(CuePlayerViewModel vm)
    {
        vm.OnCueStarted(vm.VisibleNodes[0].Id);
        vm.OnCueStarted(vm.VisibleNodes[1].Children[0].Id);

        foreach (var row in vm.NowPlayingRows)
        {
            switch (row)
            {
                case ActiveCueViewModel cue:
                    cue.ListName = ForeignListName;
                    cue.PositionMs = 42_000;
                    break;
                case ActiveGroupViewModel group:
                    group.ListName = ForeignListName;
                    group.IsExpanded = true;
                    break;
            }
        }
    }

    private static CueList MainList() => new()
    {
        Name = "Main show — full running order (long list name)",
        Nodes =
        {
            new MediaCueNode
            {
                Number = "1",
                Label = LongLabel,
                Source = new FilePlaylistItem(LongPath),
                DurationMs = 300_000,
                HasAudio = true,
                HasVideo = true,
                AudioChannels = 2,
                Schedule = new CueSchedule
                {
                    Kind = CueScheduleKind.Timecode,
                    Enabled = true,
                    Timecode = "01:00:00:00",
                    TimecodeRate = CueTimecodeRate.Fps25,
                },
                HotkeyGesture = "Ctrl+Shift+F5",
                Triggers =
                [
                    new CueTriggerBinding
                    {
                        Kind = CueTriggerKind.Midi,
                        MidiDeviceName = "APC40 mkII MIDI 1",
                        MidiChannel = 1,
                        MidiNumber = 64,
                        MidiValueMin = 1,
                    },
                    new CueTriggerBinding
                    {
                        Kind = CueTriggerKind.Osc,
                        OscAddress = "/haplay/showcontrol/cue/1/go",
                        OscArgument = "1",
                    },
                ],
            },
            new CueGroupNode
            {
                Number = "2",
                Label = "Interval playlist — " + LongLabel,
                FireMode = CueGroupFireMode.Playlist,
                Playlist = new CuePlaylistOptions { Shuffle = true, CrossfadeMs = 2500, LoopCount = 0 },
                Children =
                {
                    new MediaCueNode
                    {
                        Number = "2.1",
                        Label = LongLabel,
                        Source = new FilePlaylistItem(LongPath),
                        DurationMs = 180_000,
                        HasAudio = true,
                    },
                    new MediaCueNode
                    {
                        Number = "2.2",
                        Label = "Second interval item",
                        Source = new FilePlaylistItem(LongPath),
                        DurationMs = 120_000,
                        HasAudio = true,
                    },
                },
            },
            new CueGroupNode
            {
                Number = "3",
                Label = "Timeline group",
                FireMode = CueGroupFireMode.Timeline,
                Children =
                {
                    new MediaCueNode
                    {
                        Number = "3.1",
                        Label = LongLabel,
                        Source = new FilePlaylistItem(LongPath),
                        DurationMs = 60_000,
                        HasAudio = true,
                    },
                    new MediaCueNode
                    {
                        Number = "3.2",
                        Label = "Stinger",
                        Source = new FilePlaylistItem(LongPath),
                        DurationMs = 5_000,
                        HasAudio = true,
                        TimelineStartMs = 30_000,
                        PreWaitMs = 2_000,
                    },
                    new CommentCueNode { Number = "3.3", Label = LongLabel, TimelineStartMs = 45_000 },
                },
            },
        },
    };

    private static CueList BackupList() => new()
    {
        Name = ForeignListName,
        Nodes = { new MediaCueNode { Number = "1", Label = "Backup bed", DurationMs = 60_000, HasAudio = true } },
    };

    private static CueList AmbienceList() => new()
    {
        Name = "FOH ambience",
        Nodes = { new MediaCueNode { Number = "1", Label = "House music", DurationMs = 60_000, HasAudio = true } },
    };
}

/// <summary>A realised <see cref="TimelineEditorWindow"/> over the fixture's Timeline group.</summary>
internal sealed class TimelineScene(Window window, Visual root) : IDisposable
{
    public Visual Root { get; } = root;

    public static TimelineScene Realise(int width, int height)
    {
        HeadlessAppTheme.ApplyProductionBaseTheme();
        var vm = CueScene.BuildViewModel();
        var editor = new TimelineEditorWindowViewModel(vm, vm.VisibleNodes[2], startPlayheadTimer: false);
        var window = new TimelineEditorWindow { DataContext = editor, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new TimelineScene(window, (Visual)window.GetVisualChildren().First());
    }

    public IEnumerable<Visual> LaneLabelBorders() =>
        Root.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is CueNodeViewModel);

    public void Dispose() => window.Close();
}

/// <summary>One element that escaped the innermost container that could still have shown it.</summary>
internal sealed record LayoutEscape(string Path, Rect Element, Rect Scope, string ScopeName, string Axis)
{
    public override string ToString() =>
        $"{Axis,-4} {Path}  el={Fmt(Element)} scope[{ScopeName}]={Fmt(Scope)}";

    private static string Fmt(Rect r) => $"({r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#})";
}

/// <summary>
/// Walks a realised visual tree and reports laid-out rectangles that leave the container that was
/// supposed to bound them. Uses <see cref="Visual.TransformToVisual"/> rather than
/// <c>TransformedBounds</c>: the latter is only populated by a render pass, while the former is pure
/// layout and is therefore reliable headless.
/// </summary>
internal static class LayoutProbe
{
    private const double Epsilon = 0.5;

    /// <summary>Every visible control whose rectangle leaves the innermost enclosing scope on an axis
    /// that scope cannot scroll. A <see cref="ScrollViewer"/> opens a new scope and exempts the axes it
    /// can actually scroll (Avalonia's default for the horizontal axis is
    /// <see cref="ScrollBarVisibility.Disabled"/>, so horizontal overflow inside one really is clipped
    /// away for good). When a control escapes, its subtree is not descended into - the children's
    /// escape is a consequence, not a second defect.</summary>
    /// <param name="root">Coordinate space and outermost bound - normally the workspace view.</param>
    /// <param name="start">Sub-tree to inspect; defaults to <paramref name="root"/>.</param>
    public static List<LayoutEscape> FindEscapes(Visual root, Visual? start = null)
    {
        var result = new List<LayoutEscape>();
        Walk(start ?? root, root, new Rect(root.Bounds.Size), "window", false, false, string.Empty, result);
        return result;
    }

    /// <summary>Every visible descendant of <paramref name="container"/> must lie inside it. For
    /// containers whose whole purpose is to bound their content (a Now Playing row border, a timeline
    /// lane label): unlike <see cref="FindEscapes"/> this ignores scrollability - the container IS the
    /// bound.</summary>
    public static List<LayoutEscape> FindEscapesWithin(Visual container, Visual root)
    {
        var result = new List<LayoutEscape>();
        if (RectIn(container, root) is not { } rect)
            return result;
        var name = Describe(container);
        foreach (var child in container.GetVisualChildren())
            WalkStrict(child, root, rect, name, name, result);
        return result;
    }

    public static Rect? RectIn(Visual v, Visual root) =>
        v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : null;

    private static void WalkStrict(
        Visual v, Visual root, Rect scope, string scopeName, string path, List<LayoutEscape> result)
    {
        if (!v.IsVisible)
            return;
        var here = path + " > " + Describe(v);
        if (RectIn(v, root) is { Width: > 0, Height: > 0 } rect && EscapeAxis(rect, scope) is { } axis)
        {
            result.Add(new LayoutEscape(here, rect, scope, scopeName, axis));
            return;
        }

        foreach (var child in v.GetVisualChildren())
            WalkStrict(child, root, scope, scopeName, here, result);
    }

    private static void Walk(
        Visual v, Visual root, Rect scope, string scopeName,
        bool scrollH, bool scrollV, string path, List<LayoutEscape> result)
    {
        if (!v.IsVisible)
            return;

        var here = path.Length == 0 ? Describe(v) : path + " > " + Describe(v);
        var rect = RectIn(v, root);
        if (!ReferenceEquals(v, root)
            && rect is { Width: > 0, Height: > 0 } r
            && EscapeAxis(r, scope, scrollH, scrollV) is { } axis)
        {
            result.Add(new LayoutEscape(here, r, scope, scopeName, axis));
            return;
        }

        if (v is ScrollViewer sv && rect is { } viewport)
        {
            scope = viewport;
            scopeName = Describe(sv);
            scrollH = sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
            scrollV = sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
        }

        foreach (var child in v.GetVisualChildren())
            Walk(child, root, scope, scopeName, scrollH, scrollV, here, result);
    }

    private static string? EscapeAxis(Rect r, Rect scope, bool ignoreH = false, bool ignoreV = false)
    {
        var h = !ignoreH && (r.X < scope.X - Epsilon || r.Right > scope.Right + Epsilon);
        var v = !ignoreV && (r.Y < scope.Y - Epsilon || r.Bottom > scope.Bottom + Epsilon);
        return (h, v) switch
        {
            (true, true) => "H+V",
            (true, false) => "H",
            (false, true) => "V",
            _ => null,
        };
    }

    private static string Describe(Visual v)
    {
        var name = v is Control { Name: { Length: > 0 } n } ? $"#{n}" : string.Empty;
        var classes = v is Control control
            ? string.Concat(control.Classes.Where(c => !c.StartsWith(':')).Select(c => "." + c))
            : string.Empty;
        var text = v switch
        {
            TextBlock { Text: { Length: > 0 } t } => $" '{Trunc(t)}'",
            ContentControl { Content: string s and { Length: > 0 } } => $" '{Trunc(s)}'",
            _ => string.Empty,
        };
        var dc = v is StyledElement { DataContext: { } d and not CuePlayerViewModel }
            ? $"<{d.GetType().Name}>"
            : string.Empty;
        return v.GetType().Name + name + classes + text + dc;
    }

    private static string Trunc(string s) => s.Length <= 28 ? s : s[..28] + "…";
}
