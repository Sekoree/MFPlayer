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
        // External triggers reach every loaded list (cross-list merged session), and cue numbers restart
        // per list - so the status names the list too whenever the cue is not in the selected one.
        var display = CueDisplayQualified(cue);
        if (!CanFireCue(cue))
        {
            // The same refusal GoCore makes, but reported HERE: the trigger status below is stamped
            // OVER whatever the fire head left on the strip, so a failure raised inside the fire
            // would be replaced by a "MIDI trigger fire: …" line for a cue that never played.
            StatusMessage = Strings.Format(nameof(Strings.CueNotFireableStatusFormat), display);
            return;
        }

        try
        {
            var fire = FireScheduledCueAsync(cue);
            StatusMessage = Strings.Format(statusFormatKey, display);
            await fire;
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), display, ex.Message);
        }
    }

    /// <summary>True when <paramref name="cue"/> would actually fire something right now - the
    /// synchronous pre-check the remote API's per-cue <c>/go</c> needs (it answers before the fire
    /// runs, so it cannot report a failure afterwards). False for the two cases
    /// <see cref="GoCore"/> refuses: a group with no fireable child, and a playlist / armed-list
    /// group with no items. Read-only - unlike <c>BuildTriggerPlan</c> it never consumes or
    /// restarts a playlist run.</summary>
    public bool CanFireCue(CueNodeViewModel cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        // Visualizers and media inside a group fire independently of the transport plan.
        if (cue.Kind is CueNodeKind.Visualizer or CueNodeKind.Media)
            return true;
        if (cue.Kind != CueNodeKind.Group)
            return true;
        // A FINISHED playlist run restarts from the top on the next GO (BuildTriggerPlan's rule), so
        // such a group stays fireable as long as it still has items.
        return IsPlaylistGroup(cue)
            ? PlaylistItems(cue).Count > 0
            : EnumerateFireableCueOrder(cue.Children).Any();
    }

    /// <summary>Per-cue hotkey dispatch (the drawer's Triggers section): fires the first cue in tree
    /// order whose <see cref="CueNodeViewModel.HotkeyGesture"/> matches <paramref name="e"/> through
    /// the operator-selected fire path. The cue view's transport-key handler calls this LAST (the
    /// configurable transport keys always win a clash). Returns false when no cue claims the gesture.
    /// <para>Gated exactly like a <see cref="CueTriggerKind.Hotkey"/> binding in
    /// <c>CueTriggerService</c> - <see cref="TriggersArmed"/> ON and <see cref="IsCueEditMode"/> OFF.
    /// The legacy field and a Hotkey binding are the same feature with two editors, so they must
    /// answer to the same arm switch; without it the master Triggers toggle only disabled half the
    /// keyboard surface, which is the opposite of what an arm control is for.</para></summary>
    public bool TryFireCueHotkey(Avalonia.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!TriggersArmed || IsCueEditMode)
            return false;
        // All loaded lists, selected first (a gesture claimed in both resolves to the visible list) -
        // the legacy hotkey field and a Hotkey trigger binding are one feature with two editors, so
        // they must share the cross-list scope as well as the arm switch.
        foreach (var cue in EnumerateAllCueNodesAcrossLists())
        {
            if (string.IsNullOrWhiteSpace(cue.HotkeyGesture)
                || !CueHotkeyGesture.Matches(cue.HotkeyGesture, e))
                continue;
            _ = FireTriggeredCueSafeAsync(cue, nameof(Strings.CueHotkeyFiredStatusFormat));
            return true;
        }

        return false;
    }

    /// <summary>Resolves a remote per-cue reference: the operator-facing cue NUMBER in the SELECTED
    /// list first (tree order, case-insensitive - numbers restart per list, so the visible list has to
    /// win), then the cue's Guid id in ANY loaded list, then a number in the other loaded lists (list
    /// order). Everything past the first step exists because the cross-list merged session makes cues
    /// in non-selected lists fireable too. Null when nothing matches.</summary>
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

        if (Guid.TryParse(trimmed, out var id))
            return EnumerateAllCueNodesAcrossLists().FirstOrDefault(cue => cue.Id == id);

        return EnumerateAllCueNodesAcrossLists().FirstOrDefault(
            cue => string.Equals(cue.Number?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
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
        // Cross-list merged session: a cue in another loaded list plays into the SAME ShowSession, but
        // through a headless run - GoCore owns the VISIBLE transport (standby pointer, Current row,
        // the tree's fireable order) and must never be moved by a schedule/trigger/remote fire aimed at
        // a list the operator is not looking at. The two independent paths above already avoid it.
        return IsForeignListNode(cue) ? GoForeignListAsync(cue) : GoCore(cue);
    }

    /// <summary>Fires <paramref name="fire"/> in a list OTHER than the selected one: the same trigger
    /// plan, pre-waits, group fire modes and playlist-pick consumption as <see cref="GoCore"/>, resolved
    /// against the cue's OWN list, but with no write to <see cref="CuePlayerViewModel.StandbyCueNode"/> /
    /// <see cref="CuePlayerViewModel.CurrentCueNode"/> and its own per-list cancellation scope (so
    /// re-firing the same list replaces its previous run without cancelling the visible transport's).
    /// The fired clips land in the merged session and appear in Now-Playing with their list name.</summary>
    private async Task GoForeignListAsync(
        CueNodeViewModel fire,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        // Built BEFORE anything is cancelled so a request with nothing to fire leaves the run alone.
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueNotFireableStatusFormat), CueDisplayQualified(fire));
            return;
        }

        if (IsPlaylistGroup(fire))
        {
            // GO on a PLAYING crossfade playlist takes over with the dual-voice window (GoCore's DJ-style
            // skip), then consumes the armed pick - the run state is keyed by the group's Guid, so it is
            // already per-list without any extra bookkeeping.
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

            ConsumePlaylistPick(fire);
        }

        var owner = FindOwningCueList(fire);
        var cts = new CancellationTokenSource();
        if (owner is not null)
        {
            if (_foreignListRuns.Remove(owner, out var previous))
            {
                try { previous.Cancel(); } catch { /* best effort */ }
                try { previous.Dispose(); } catch { /* best effort */ }
            }

            _foreignListRuns[owner] = cts;
        }

        StatusMessage = Strings.Format(
            nameof(Strings.CueGoStatusFormat),
            CueDisplayQualified(fire),
            plan.Count,
            plan.Count == 1 ? string.Empty : Strings.PluralSuffixS);

        try
        {
            await RunTriggerPlanAsync(plan, cts.Token, advanceCrossfade, trackCurrentCue: false);
        }
        catch (OperationCanceledException)
        {
            // A later fire in the same list (or Stop/Panic) cancelled this run.
        }
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
        {
            // A plain GO on an empty list is a no-op; an explicit request still deserves an answer.
            if (operatorSelectedCue is not null)
            {
                StatusMessage = Strings.Format(
                    nameof(Strings.CueNotFireableStatusFormat), CueDisplay(operatorSelectedCue));
            }
            return;
        }

        if (CurrentCueNode is not null && IsTransportPaused)
        {
            IsTransportPaused = false;
            _ = SetPlaybackPausedCallback?.Invoke(false);
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
            return;
        }

        // A newly selected row is a one-shot operator override. Otherwise GO follows the live standby;
        // keeping the properties drawer on an older cue must not cause that cue to repeat forever.
        // An EXPLICITLY requested cue is never silently swapped for the standby cue: the old
        // "resolve, and fall through when the resolution comes back null" shape dropped an empty
        // group / a playlist group with no next pick and fired STANDBY instead - and triggers, the
        // scheduler and POST /api/v1/cues/{ref}/go all arrive here. It now fails loudly below.
        var fire = operatorSelectedCue ?? StandbyCueNode ?? ordered.FirstOrDefault();
        if (fire is null)
            return;

        // Built BEFORE the cancel so a request with nothing to fire leaves the running show alone.
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
        {
            if (operatorSelectedCue is not null)
            {
                StatusMessage = Strings.Format(
                    nameof(Strings.CueNotFireableStatusFormat), CueDisplay(fire));
            }
            return;
        }

        CancelTransportRun();

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
        CancelForeignListRuns(); // Stop is session-wide: cross-list runs end with the clips they started
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
        CancelForeignListRuns();
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
