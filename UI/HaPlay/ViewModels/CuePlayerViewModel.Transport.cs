using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public partial class CuePlayerViewModel
{
    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private void StandbySelected()
    {
        if (SelectedCueNode is not null)
            StandbyCueFromView(SelectedCueNode);
    }

    /// <summary>Puts <paramref name="cue"/> on standby. Shared by the Standby command and the
    /// tree's double-click gesture (double-click a row = standby, GO then fires it). Groups with
    /// no fireable child are rejected, same as the command gate.</summary>
    public void StandbyCueFromView(CueNodeViewModel cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (cue.Kind == CueNodeKind.Group && ResolveFireableCue(cue) is null)
            return;
        StandbyCueNode = cue;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(cue));
    }

    private bool CanStandbySelected() =>
        SelectedCueNode is { Kind: CueNodeKind.Group } group
            ? ResolveFireableCue(group) is not null
            : SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanFireSelectedVisualizer))]
    private Task FireSelectedVisualizer()
    {
        if (SelectedVisualizerCue is not { } cue)
            return Task.CompletedTask;

        _selectedCuePendingForGo = false;
        return FireVisualizerIndependentlyAsync(cue);
    }

    private async Task FireVisualizerIndependentlyAsync(CueNodeViewModel cue)
    {
        // This is deliberately independent of GO/standby. A visualizer is commonly used as a
        // persistent overlay while the main cue-list transport keeps advancing, so applying an
        // edited Start/Stop cue must not cancel the current transport run or consume its standby.
        try
        {
            var result = await ExecuteCueAsync(cue, CancellationToken.None);
            ApplyCueExecutionResult(cue, result, mediaExecutionConfigured: false);
        }
        catch (Exception ex)
        {
            // Keep the main transport untouched even when this auxiliary operation fails.
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat),
                CueDisplay(cue),
                ex.Message);
        }
    }

    private bool CanFireSelectedVisualizer() =>
        SelectedVisualizerCue is not null;

    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private async Task FireSelectedCueNow()
    {
        // Applies to the whole multi-selection: right-click GO on N highlighted cues starts all
        // of them. Tree order (not click order) keeps the result deterministic.
        var targets = EffectiveSelection().ToArray();
        if (targets.Length == 0)
            return;
        _selectedCuePendingForGo = false;
        _immediateJumpChain.Clear();
        foreach (var cue in OrderInTreeOrder(targets))
            await FireOperatorSelectedCueAsync(cue);
    }

    /// <summary>Sorts a selection snapshot into visible tree order (selection order is click order).</summary>
    private List<CueNodeViewModel> OrderInTreeOrder(IReadOnlyList<CueNodeViewModel> nodes)
    {
        if (nodes.Count <= 1)
            return [.. nodes];
        var order = new Dictionary<CueNodeViewModel, int>();
        var i = 0;
        foreach (var node in EnumerateAllCueNodes())
            order[node] = i++;
        return [.. nodes.OrderBy(n => order.GetValueOrDefault(n, int.MaxValue))];
    }

    /// <summary>Entry point for non-GO trigger fires - wall-clock schedules (<c>CueSchedulerService</c>),
    /// per-cue hotkeys, and the remote API's per-cue <c>/go</c> (both via
    /// <see cref="FireTriggeredCueSafeAsync"/>). Exactly the operator-selected fire semantics of
    /// <see cref="FireSelectedCueNow"/>: the immediate Jump chain resets like any operator GO, a
    /// pending row-click override is consumed, and the cue then rides
    /// <see cref="FireOperatorSelectedCueAsync"/> so pre-waits, group modes, jump resolution and
    /// Now-Playing behave identically to a manual fire. Callers must be on the UI thread (the
    /// scheduler's DispatcherTimer already is).</summary>
    public Task FireScheduledCueAsync(CueNodeViewModel cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        _selectedCuePendingForGo = false;
        _immediateJumpChain.Clear();
        return FireOperatorSelectedCueAsync(cue);
    }

    /// <summary>Fires <paramref name="cue"/> for an external trigger (per-cue hotkey / remote API):
    /// the scheduler's exact fire semantics (<see cref="FireScheduledCueAsync"/>), then the trigger-
    /// specific status stamped over the generic GO status the synchronous fire head set (the
    /// <c>CueSchedulerService</c> pattern - the operator sees WHY the cue started), with failures
    /// surfaced on the status strip because external callers are fire-and-forget and must never
    /// observe the exception. UI thread only.</summary>
    /// <param name="statusFormatKey">A one-argument status resource key, e.g.
    /// <c>nameof(Strings.CueHotkeyFiredStatusFormat)</c>.</param>
    public async Task FireTriggeredCueSafeAsync(CueNodeViewModel cue, string statusFormatKey)
    {
        ArgumentNullException.ThrowIfNull(cue);
        try
        {
            var fire = FireScheduledCueAsync(cue);
            StatusMessage = Strings.Format(statusFormatKey, CueDisplay(cue));
            await fire;
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplay(cue), ex.Message);
        }
    }

    /// <summary>Per-cue hotkey dispatch (the drawer's Triggers section): fires the first cue in tree
    /// order whose <see cref="CueNodeViewModel.HotkeyGesture"/> matches <paramref name="e"/> through
    /// the operator-selected fire path. The cue view's transport-key handler calls this LAST (the
    /// configurable transport keys always win a clash) and only while cue edit mode is off - hotkeys
    /// are a show-mode surface for the same reason the scheduler is. Returns false when no cue claims
    /// the gesture.</summary>
    public bool TryFireCueHotkey(Avalonia.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var cue in EnumerateAllCueNodes())
        {
            if (string.IsNullOrWhiteSpace(cue.HotkeyGesture)
                || !CueHotkeyGesture.Matches(cue.HotkeyGesture, e))
                continue;
            _ = FireTriggeredCueSafeAsync(cue, nameof(Strings.CueHotkeyFiredStatusFormat));
            return true;
        }

        return false;
    }

    /// <summary>Resolves a remote per-cue reference in the SELECTED list (the transport per-cue
    /// fires ride, like scheduling): the operator-facing cue NUMBER first (tree order,
    /// case-insensitive), then the cue's Guid id. Null when nothing matches.</summary>
    public CueNodeViewModel? FindCueByReference(string cueRef)
    {
        if (string.IsNullOrWhiteSpace(cueRef))
            return null;
        var trimmed = cueRef.Trim();
        foreach (var cue in EnumerateAllCueNodes())
        {
            if (string.Equals(cue.Number?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return cue;
        }

        return Guid.TryParse(trimmed, out var id)
            ? EnumerateAllCueNodes().FirstOrDefault(cue => cue.Id == id)
            : null;
    }

    /// <summary>Stops one cue if it is currently running - the remote API's per-cue <c>/stop</c>.
    /// Exactly the per-row semantics of <see cref="StopSelectedCue"/> (running visualizer layer or
    /// active clip); returns false when the cue isn't running.</summary>
    public bool TryStopCue(CueNodeViewModel cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (_runningVisualizers.ContainsKey(cue.Id))
        {
            _ = StopVisualizerAsync(cue.Id);
            return true;
        }

        if (_activeCueIds.Contains(cue.Id))
        {
            _ = CancelCueCallback?.Invoke(cue.Id) ?? Task.CompletedTask;
            return true;
        }

        return false;
    }

    private Task FireOperatorSelectedCueAsync(CueNodeViewModel cue)
    {
        if (cue.Kind == CueNodeKind.Visualizer)
            return FireVisualizerIndependentlyAsync(cue);
        if (cue.Kind == CueNodeKind.Media && FindContainingGroupPath(cue).Count > 0)
            return FireGroupedMediaIndependentlyAsync(cue);
        return GoCore(cue);
    }

    private async Task FireGroupedMediaIndependentlyAsync(CueNodeViewModel cue)
    {
        if (MediaCueIndependentExecutor is not { } executor
            || cue.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        try
        {
            var result = await executor(media, CancellationToken.None);
            ApplyCueExecutionResult(cue, result, mediaExecutionConfigured: true);
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplay(cue), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopSelectedCue))]
    private async Task StopSelectedCue()
    {
        // Applies to the whole multi-selection: every highlighted cue that is running stops.
        foreach (var cue in EffectiveSelection().ToArray())
        {
            if (_runningVisualizers.ContainsKey(cue.Id))
                await StopVisualizerAsync(cue.Id);
            else if (_activeCueIds.Contains(cue.Id))
                await (CancelCueCallback?.Invoke(cue.Id) ?? Task.CompletedTask);
        }
    }

    private bool CanStopSelectedCue() =>
        EffectiveSelection().Any(cue =>
            _activeCueIds.Contains(cue.Id) || _runningVisualizers.ContainsKey(cue.Id));

    // Immediate Jump→Jump control flow carries this visited set across internally-triggered GO calls.
    // Any operator/Auto-Follow GO starts a fresh chain; landing on a non-jump clears it again.
    private readonly HashSet<Guid> _immediateJumpChain = [];

    // Per-Jump runtime history for the optional random no-repeat policy. This is deliberately session
    // state rather than project data: loading a project starts each random sequence afresh.
    private readonly Dictionary<Guid, Guid> _lastRandomJumpTargetIds = [];

    // A row click is a one-shot operator override for GO. After that selected cue fires, selection remains
    // in the properties drawer but subsequent GO presses return to the automatically advanced standby.
    private bool _selectedCuePendingForGo;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private Task Go()
    {
        _immediateJumpChain.Clear();
        var selectedIntent = _selectedCuePendingForGo ? SelectedCueNode : null;
        _selectedCuePendingForGo = false;

        // A visualizer is an auxiliary persistent overlay. Firing a deliberately selected visualizer via
        // GO must not replace the current song/playhead or consume the song group's standby.
        return selectedIntent is not null
            ? FireOperatorSelectedCueAsync(selectedIntent)
            : GoCore();
    }

    private async Task GoCore(
        CueNodeViewModel? operatorSelectedCue = null,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;

        if (CurrentCueNode is not null && IsTransportPaused)
        {
            IsTransportPaused = false;
            _ = SetPlaybackPausedCallback?.Invoke(false);
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
            return;
        }

        // A newly selected row is a one-shot operator override. Otherwise GO follows the live standby;
        // keeping the properties drawer on an older cue must not cause that cue to repeat forever.
        var selectedFire = operatorSelectedCue is not null
                           && ordered.Contains(ResolveFireableCue(operatorSelectedCue)!)
            ? operatorSelectedCue
            : null;
        var fire = selectedFire ?? StandbyCueNode ?? ordered.FirstOrDefault();
        if (fire is null)
            return;

        CancelTransportRun();
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
            return;

        var resolvedFire = ResolveFireableCue(fire) ?? fire;
        CueNodeViewModel? nextStandby;
        if (IsPlaylistGroup(fire))
        {
            // GO on a PLAYING crossfade playlist is the "fire next early" takeover (the design doc's
            // DJ-style skip): the manual skip rides the same dual-voice window as the automatic
            // pre-end advance instead of cutting the current item. Resolved BEFORE the pick is
            // consumed (CurrentItemId is still the playing item). Butt-splice lists (CrossfadeMs 0),
            // idle groups, armed lists, and hosts without the seam skip hard, exactly as before.
            if (advanceCrossfade is null
                && MediaCueCrossfadeExecutor is not null
                && ParseGroupFireMode(fire) == CueGroupFireMode.Playlist
                && fire.PlaylistCrossfadeMs > 0
                && _playlistRuns.TryGetValue(fire.Id, out var playingRun)
                && playingRun.CurrentItemId is { } playingItem
                && _activeCueIds.Contains(playingItem))
            {
                advanceCrossfade = (
                    TimeSpan.FromMilliseconds(fire.PlaylistCrossfadeMs),
                    S.Media.Session.FadeCurve.EqualPower);
            }

            // Firing a playlist/armed-list group consumes its armed pick. Standby stays ON the
            // group while the run continues (GO = skip / armed-advance, and pre-roll then warms the
            // NEXT pick), and moves past the group once the final pick has fired.
            ConsumePlaylistPick(fire);
            nextStandby = HasFinishedPlaylistRun(fire.Id)
                ? NextCueAfterGroup(fire, ordered)
                : fire;
        }
        else
        {
            nextStandby = NextCueAfter(resolvedFire, ordered);
        }

        CurrentCueNode = plan[0].Cue;
        IsTransportPaused = false;
        _suppressStandbyPreRollRefresh = true;
        try
        {
            StandbyCueNode = nextStandby;
        }
        finally
        {
            _suppressStandbyPreRollRefresh = false;
        }
        // Transport state is shown by the current/standby dots. Keep the editor selection untouched so
        // GO never replaces the properties drawer the operator is currently working in.
        StatusMessage = Strings.Format(
            nameof(Strings.CueGoStatusFormat),
            CueDisplay(fire),
            plan.Count,
            plan.Count == 1 ? string.Empty : Strings.PluralSuffixS);

        _transportRunCts = new CancellationTokenSource();
        try
        {
            await RunTriggerPlanAsync(plan, _transportRunCts.Token, advanceCrossfade);
            SuggestPreRollRefresh();
        }
        catch (OperationCanceledException)
        {
            // Stop/Panic/next GO cancelled the prior run.
        }
    }

    private bool CanGo() => EnumerateFireableCueOrder().Any();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (CurrentCueNode is null)
            return;
        IsTransportPaused = !IsTransportPaused;
        _ = SetPlaybackPausedCallback?.Invoke(IsTransportPaused);
        StatusMessage = IsTransportPaused
            ? Strings.Format(nameof(Strings.CuePausedStatusFormat), CueDisplay(CurrentCueNode))
            : Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
    }

    private bool CanPause() => CurrentCueNode is not null;

    [RelayCommand]
    private void Stop()
    {
        CancelTransportRun();
        ClearPlaylistRuns(); // Stop ends any playlist/armed-list run - a fresh GO starts over
        if (StopPlaybackCallback is { } stopPlayback)
        {
            // The host ShowSession owns the synchronized clip + persistent-surface fade. Retire the UI rows
            // immediately, but do not issue an individual visualizer Stop that would detach the surface early.
            _ = stopPlayback(ResolveStopFade());
            OnVisualizerLayersCleared();
        }
        else
        {
            StopAllVisualizers();
        }
        if (CurrentCueNode is null && StandbyCueNode is null && !IsTransportPaused)
            return;
        CurrentCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CueStoppedStatus;
    }

    /// <summary>The Stop button's effective fade: cue-list <see cref="CueListEditorViewModel.StopFadeMs"/>,
    /// else the app-settings default (750 ms out of the box). 0 = hard cut. Settings are re-read per press -
    /// the review-H5 "always load fresh" contract - so an edit in another window applies immediately.</summary>
    private CueStopFadeRequest ResolveStopFade()
    {
        var ms = SelectedCueList?.StopFadeMs ?? Models.AppSettings.Load().StopFadeMs;
        return new CueStopFadeRequest(
            Fade: ms > 0,
            FadeDuration: TimeSpan.FromMilliseconds(Math.Max(0, ms)),
            Curve: HaPlayShowMapper.MapFadeCurve(SelectedCueList?.StopFadeCurve ?? CueFadeCurve.Linear));
    }

    [RelayCommand]
    private void Panic()
    {
        CancelTransportRun();
        ClearPlaylistRuns();
        if (StopPlaybackCallback is { } stopPlayback)
        {
            // Panic's own app-level fade; the 0 ms default hard-cuts (panic means NOW), skipping even
            // per-clip configured fade-outs.
            var panicMs = Models.AppSettings.Load().PanicFadeMs;
            _ = stopPlayback(new CueStopFadeRequest(
                Fade: panicMs > 0,
                FadeDuration: TimeSpan.FromMilliseconds(Math.Max(0, panicMs)),
                Curve: S.Media.Session.FadeCurve.Linear));
            OnVisualizerLayersCleared();
        }
        else
        {
            StopAllVisualizers();
        }
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CuePanicStatus;
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;
        var anchor = StandbyCueNode ?? CurrentCueNode ?? ordered.First();
        var resolvedAnchor = ResolveFireableCue(anchor) ?? anchor;
        var idx = ordered.IndexOf(resolvedAnchor);
        if (idx < 0)
            return;
        var prev = idx > 0 ? ordered[idx - 1] : ordered[0];
        StandbyCueNode = prev;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(prev));
    }

    private bool CanBack() => EnumerateFireableCueOrder().Any();
}
