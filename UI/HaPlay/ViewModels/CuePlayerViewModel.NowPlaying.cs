using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// What the operator SEES while cues run: the active-cue set the engine reports, the Now Playing rows built
/// from it (including the group rows and their foreign-list labels), the upcoming list, per-row status
/// refresh, scrubbing/seeking an active cue, and the transient status message.
/// <para>Split out of the root file (2026-07-30 review §3). Everything here is downstream of an engine
/// callback - <c>OnCueStarted</c>, <c>OnCueEnded</c>, <c>OnCueProgress</c> - which is what separates it from
/// the authoring and execution halves.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    /// <summary>Set of cue ids the playback engine reports as currently active. Maintained via
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/> from the host (MainViewModel wires
    /// these to the engine's events). Used by <see cref="RefreshRowStatuses"/> so every active
    /// cue lights up - the singular <see cref="CurrentCueNode"/> only tracks the last-started
    /// one for AutoFollow / transport-state purposes.</summary>
    private readonly HashSet<Guid> _activeCueIds = new();

    /// <summary>True while any cue is currently playing (fired via <see cref="OnCueStarted"/>, not yet
    /// <see cref="OnCueEnded"/>). The authoritative "is something playing" signal - used to defer the ShowSession
    /// document rebuild on an in-place edit so it never stops a running cue (unlike a clock's <c>IsRunning</c>,
    /// which is unreliable for a video-only held/text clip).</summary>
    public bool HasActiveCues => _activeCueIds.Count > 0;

    /// <summary>Rows visible in the right-side Now Playing panel. Maintained by
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/>; their progress fields update via
    /// <see cref="OnCueProgress"/>.</summary>
    public ObservableCollection<ActiveCueViewModel> ActiveCues { get; } = new();

    /// <summary>
    /// P4 (plan §3.2) - what the Now Playing panel renders: <see cref="ActiveCueViewModel"/> for
    /// standalone cues, <see cref="ActiveGroupViewModel"/> aggregating active cues that share a
    /// parent group node. <see cref="ActiveCues"/> stays the flat source of truth.
    /// </summary>
    public ObservableCollection<object> NowPlayingRows { get; } = new();

    /// <summary>Host-provided coordinated multi-cue seek (engine.SeekCuesAsync): all targets pause,
    /// seek in parallel and resume through one barrier so group children stay aligned. When null
    /// (tests), group seeks fall back to sequential per-cue <see cref="SeekCueCallback"/> calls.</summary>
    public Func<IReadOnlyList<(Guid CueId, TimeSpan Position)>, Task>? SeekCuesCallback { get; set; }

    /// <summary>Group-row seek: every child seeks to the same fraction of ITS OWN duration (keeps
    /// proportional alignment for staggered-length children). Same padlock gate as single rows.</summary>
    public async Task SeekActiveGroupToFractionAsync(ActiveGroupViewModel group, double fraction)
    {
        if (!NowPlayingSeekUnlocked)
            return;

        if (SeekCuesCallback is { } batched)
        {
            var clamped = Math.Clamp(fraction, 0.0, 1.0);
            var targets = group.Children
                .Where(child => child.DurationMs > 0)
                .Select(child => (child.CueId, TimeSpan.FromMilliseconds(child.DurationMs * clamped)))
                .ToList();
            if (targets.Count > 0)
                await batched(targets).ConfigureAwait(false);
            return;
        }

        foreach (var child in group.Children.ToArray())
            await SeekActiveCueToFractionAsync(child, fraction).ConfigureAwait(false);
    }

    /// <summary>The group node a cue sits under, or null for top-level cues. Searches the tree of the
    /// list that OWNS the cue - the selected one for a visible cue, otherwise the loaded list a
    /// cross-list schedule/trigger fired from (its cues share the merged session's Now-Playing panel).</summary>
    private CueNodeViewModel? FindParentGroupOf(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } owner ? Search(owner.Nodes, null, node) : null;

    private static CueNodeViewModel? Search(
        IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel? parent, CueNodeViewModel node)
    {
        foreach (var candidate in nodes)
        {
            if (ReferenceEquals(candidate, node))
                return parent is { IsGroup: true } ? parent : null;
            if (Search(candidate.Children, candidate, node) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>The owning list's name when <paramref name="node"/> lives in a list OTHER than the
    /// selected one, else null (no prefix for the visible list's own rows).</summary>
    private string? ForeignListNameOf(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } owner && !ReferenceEquals(owner, SelectedCueList)
            ? owner.Name
            : null;

    /// <summary>Re-stamps the Now-Playing list qualifier after a list switch: rows fired from what is now
    /// the selected list drop their prefix, and rows of the list just left gain one.</summary>
    private void RefreshNowPlayingListNames()
    {
        foreach (var row in ActiveCues)
            row.ListName = ForeignListNameOf(row.Node);
        foreach (var group in NowPlayingRows.OfType<ActiveGroupViewModel>())
            group.ListName = ForeignListNameOf(group.GroupNode);
    }

    private void AddNowPlayingRow(ActiveCueViewModel entry)
    {
        if (FindParentGroupOf(entry.Node) is { } groupNode)
        {
            var group = NowPlayingRows.OfType<ActiveGroupViewModel>()
                .FirstOrDefault(g => g.GroupId == groupNode.Id);
            if (group is null)
            {
                group = new ActiveGroupViewModel(groupNode) { ListName = ForeignListNameOf(groupNode) };
                NowPlayingRows.Add(group);
            }

            group.Children.Add(entry);
            RefreshPlaylistNowPlayingStatus(groupNode.Id);
            return;
        }

        NowPlayingRows.Add(entry);
    }

    private void RemoveNowPlayingRow(Guid cueId)
    {
        for (var i = NowPlayingRows.Count - 1; i >= 0; i--)
        {
            switch (NowPlayingRows[i])
            {
                case ActiveCueViewModel single when single.CueId == cueId:
                    NowPlayingRows.RemoveAt(i);
                    break;
                case ActiveGroupViewModel group:
                    for (var c = group.Children.Count - 1; c >= 0; c--)
                        if (group.Children[c].CueId == cueId)
                            group.Children.RemoveAt(c);
                    if (group.Children.Count == 0)
                        NowPlayingRows.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>Cues that *will* fire once the operator presses Go from the current Standby
    /// position - used by the Now Playing panel's Upcoming section.</summary>
    public ObservableCollection<CueNodeViewModel> UpcomingCues { get; } = new();

    /// <summary>Host-provided per-cue stop callback (engine.StopCueAsync). The Now Playing
    /// panel's per-row ✕ button forwards through this; null in tests.</summary>
    public Func<Guid, Task>? CancelCueCallback { get; set; }

    // ----- UI rewrite P4: Now Playing row seek (with lock) --------------------------------------

    /// <summary>Unlocks dragging/tapping the Now Playing progress bars to seek. Default locked -
    /// the panel sits next to GO, so accidental seeks during a show must be opt-in (plan §3.2).</summary>
    [ObservableProperty]
    private bool _nowPlayingSeekUnlocked;

    /// <summary>Seeks an active cue to a 0..1 fraction of its duration. No-op while locked or when
    /// the cue has no known duration yet.</summary>
    public Task SeekActiveCueToFractionAsync(ActiveCueViewModel cue, double fraction)
    {
        if (!NowPlayingSeekUnlocked || cue.DurationMs <= 0 || SeekCueCallback is null)
            return Task.CompletedTask;
        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        return SeekCueCallback(cue.CueId, TimeSpan.FromMilliseconds(cue.DurationMs * clamped));
    }

    /// <summary>Host callback for mutating a placement's already-running compositor slot while the
    /// selected cue is active. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveCueVideoPlacementCallback { get; set; }

    /// <summary>Host callback raised when an <em>idle</em> (not-yet-fired) cue's clip model is edited in a way
    /// the backing show document captures only at (re)load - e.g. a video placement nudge. The host flags its
    /// document stale so the next GO rebuilds it with the current model, instead of firing stale geometry. A
    /// running cue is updated live via <see cref="UpdateActiveCueVideoPlacementCallback"/> and does not raise this.</summary>
    public Action? CueClipModelStaleCallback { get; set; }

    /// <summary>Host callback for reconciling the selected cue's running audio routes after route
    /// row edits. No-op in tests or when the cue is not playing.</summary>
    /// <summary>Host callback: re-apply a playing cue's audio routing live. Receives the edited routes
    /// plus the cue's master <see cref="CueNodeViewModel.LevelDb"/> (baked into the routed gains by the
    /// mapper, so every live re-apply must carry it or the cue would pop to unity).</summary>
    public Func<Guid, IReadOnlyList<CueAudioRoute>, double, Task>? UpdateActiveCueAudioRoutesCallback { get; set; }

    /// <summary>Host callback to live-re-render a playing text cue after a text/style edit (so it updates in
    /// place instead of only on the next fire). No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, MediaCueNode, Task>? UpdateActiveCueTextCallback { get; set; }

    /// <summary>Host callback - live-applies an output mapping (warp sections) to a running
    /// composition: (compositionId, outputLineId, mapping). No-op when the composition isn't live.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool>? UpdateOutputMappingCallback { get; set; }

    /// <summary>Host callback - live-applies a composition-level video FX mapping to a running composition.</summary>
    public Func<Guid, CueOutputMapping?, bool>? UpdateCompositionVideoFxCallback { get; set; }

    /// <summary>Host callback - shows/hides the mapping calibration grid for one composition output.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool, bool>? SetCompositionTestPatternCallback { get; set; }

    /// <summary>Engine callback - cue began playing. Marks its row Current and pushes a new
    /// <see cref="ActiveCueViewModel"/> into <see cref="ActiveCues"/>.</summary>
    public void OnCueStarted(Guid cueId)
    {
        _activeCueIds.Add(cueId);
        RefreshRowStatuses();

        var node = FindNodeById(cueId);
        if (node is not null && !ActiveCues.Any(a => a.CueId == cueId))
        {
            var entry = new ActiveCueViewModel(node, cueId, id => _ = CancelActiveCueAsync(id))
            {
                DurationMs = Math.Max(0, node.EffectiveDurationMs),
                // Cross-list fire: the cue plays in the SAME merged session, so it belongs in
                // Now-Playing - qualified with its list name so the operator can tell it apart from
                // the visible list's rows.
                ListName = ForeignListNameOf(node),
            };
            ActiveCues.Add(entry);
            AddNowPlayingRow(entry);
        }
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - preview stopped. Clears preview state on the VM.</summary>
    public void OnPreviewEnded(Guid cueId)
    {
        _ = cueId;
        if (PreviewingCueId is null) return;
        PreviewingCueId = null;
        StatusMessage = Strings.PreviewStoppedStatus;
    }

    /// <summary>Engine callback - cue stopped (natural end, Stop, or Panic). Clears Current
    /// status and removes the matching <see cref="ActiveCueViewModel"/>.</summary>
    public void OnCueEnded(Guid cueId)
    {
        _activeCueIds.Remove(cueId);
        RefreshRowStatuses();

        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (ActiveCues[i].CueId == cueId)
                ActiveCues.RemoveAt(i);
        RemoveNowPlayingRow(cueId);
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - progress sample for one active cue. Updates the row's
    /// position so the progress bar and "mm:ss / mm:ss" display advance.</summary>
    public void OnCueProgress(CuePlaybackProgress p)
    {
        foreach (var a in ActiveCues)
        {
            if (a.CueId != p.CueId) continue;
            a.PositionMs = (long)p.Position.TotalMilliseconds;
            if (p.Duration > TimeSpan.Zero)
                a.DurationMs = (long)p.Duration.TotalMilliseconds;
            break;
        }

        if (SelectedCueNode?.Id == p.CueId && p.Duration > TimeSpan.Zero)
            CueScrubberValue = p.Position.TotalMilliseconds * 1000.0 / p.Duration.TotalMilliseconds;
    }

    private void SyncCueScrubberFromActiveSelection()
    {
        if (SelectedCueNode is null)
            return;
        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;
        var positionMs = active?.PositionMs ?? 0;
        CueScrubberValue = positionMs * 1000.0 / durationMs;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreview))]
    private async Task TogglePreviewAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } node)
            return;

        if (IsPreviewingSelectedCue)
        {
            if (StopPreviewCallback is not null)
                await StopPreviewCallback();
            PreviewingCueId = null;
            StatusMessage = Strings.PreviewStoppedStatus;
            return;
        }

        if (PreviewCueCallback is null)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        if (node.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueInvalidMediaCue;
            return;
        }

        using var cts = new CancellationTokenSource();
        var err = await PreviewCueCallback(media, cts.Token);
        if (!string.IsNullOrWhiteSpace(err))
        {
            StatusMessage = err;
            return;
        }

        PreviewingCueId = node.Id;
        StatusMessage = Strings.Format(nameof(Strings.PreviewingCueStatusFormat), CueDisplay(node));
    }

    private bool CanTogglePreview() =>
        SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanSeekActiveCueFromScrubber))]
    private async Task SeekActiveCueFromScrubberAsync()
    {
        if (SelectedCueNode is null || SeekCueCallback is null)
            return;

        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;

        var position = TimeSpan.FromMilliseconds(CueScrubberValue * durationMs / 1000.0);
        await SeekCueCallback(SelectedCueNode.Id, position);
    }

    private bool CanSeekActiveCueFromScrubber() => IsCueScrubberVisible;

    /// <summary>The cue node with this id in ANY loaded list (selected first). Cue ids are Guids, so the
    /// cross-list lookup can never alias; it is what lets a cue fired from a non-selected list appear in
    /// Now-Playing and answer progress/end events from the merged session.</summary>
    private CueNodeViewModel? FindNodeById(Guid id)
    {
        foreach (var node in EnumerateAllCueNodesAcrossLists())
            if (node.Id == id)
                return node;
        return null;
    }

    /// <summary>Operator-facing "number label" for a cue id (status/alert messages), or null when the id
    /// isn't in the loaded lists. UI thread.</summary>
    internal string? DescribeCue(Guid id) =>
        FindNodeById(id) is { } node
            ? string.IsNullOrWhiteSpace(node.Number) ? node.Label : $"{node.Number} {node.Label}".TrimEnd()
            : null;

    /// <summary>Host callback - the set of warmed (standby-ready) cues changed. Snapshot lists the cue ids
    /// that are currently warmed. Walks every loaded cue node and sets <c>IsPreRollWarm</c>
    /// accordingly so the status badge column can render the warming indicator (Phase 5.7.2).
    /// <para>This method does not marshal threads on its own; the host wiring (MainViewModel)
    /// hops onto the UI dispatcher before invoking, because the underlying
    /// <c>ShowSession.PreparedCuesChanged</c> can fire from any thread.</para>
    /// </summary>
    public void OnPreRollCacheChanged(IReadOnlyCollection<Guid> warmCueIds)
    {
        var warm = warmCueIds as HashSet<Guid> ?? new HashSet<Guid>(warmCueIds);
        foreach (var node in EnumerateAllCueNodes())
        {
            var shouldBeWarm = warm.Contains(node.Id);
            if (node.IsPreRollWarm != shouldBeWarm)
                node.IsPreRollWarm = shouldBeWarm;
        }
    }

    /// <summary>Host callback - richer per-cue standby preparation states changed (Idle/Preparing/
    /// Ready/Failed). Cues absent from the snapshot are Idle. Drives the status badge + tooltip and,
    /// via <see cref="CueNodeViewModel.PreRollState"/>, keeps <c>IsPreRollWarm</c> in sync.</summary>
    public void OnPreparedCueStatesChanged(IReadOnlyList<Playback.CuePreparationStatus> states)
    {
        var byId = states.ToDictionary(s => s.CueId);
        foreach (var node in EnumerateAllCueNodes())
        {
            if (byId.TryGetValue(node.Id, out var status))
            {
                node.PreRollState = status.State;
                node.PreRollError = status.Error;
            }
            else
            {
                node.PreRollState = PreparedCueState.Idle;
                node.PreRollError = null;
            }
        }
    }

    private void RebuildUpcomingCues()
    {
        UpcomingCues.Clear();
        if (SelectedCueList is null) return;
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            foreach (var c in simultaneousGroup)
            {
                if (_activeCueIds.Contains(c.Id)) continue;
                UpcomingCues.Add(c);
            }
            return;
        }

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0) return;

        var anchor = StandbyCueNode ?? ordered.FirstOrDefault();
        if (anchor is null) return;
        var startIdx = ordered.FindIndex(c => ReferenceEquals(c, ResolveFireableCue(anchor) ?? anchor));
        if (startIdx < 0) return;

        for (var i = startIdx; i < ordered.Count; i++)
        {
            var c = ordered[i];
            // Don't list already-active cues as upcoming - they're in the Active section.
            if (_activeCueIds.Contains(c.Id)) continue;
            UpcomingCues.Add(c);
        }
    }

    private void RefreshRowStatuses()
    {
        foreach (var node in EnumerateAllCueNodes())
        {
            var status = _activeCueIds.Contains(node.Id)
                ? CueRowStatus.Current
                : ReferenceEquals(node, StandbyCueNode)
                    ? CueRowStatus.Standby
                    : CueRowStatus.Idle;
            if (node.RowStatus != status)
                node.RowStatus = status;
        }
    }

    partial void OnIsTransportPausedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TransportState));
        OnPropertyChanged(nameof(TransportStateColor));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        _statusMessageClearCts?.Cancel();
        _statusMessageClearCts?.Dispose();
        _statusMessageClearCts = null;

        if (string.IsNullOrWhiteSpace(value))
            return;

        // Status surfaces as a top-right toast (MainView overlay) instead of the old inline banner,
        // which pushed the whole cue list down mid-click. Severity is a keyword heuristic - cue
        // status strings carry no structured level.
        ToastCenter.Post(ClassifyStatusSeverity(value), value);

        var cts = new CancellationTokenSource();
        _statusMessageClearCts = cts;
        _ = ClearStatusMessageLaterAsync(value, cts.Token);
    }

    private static ToastSeverity ClassifyStatusSeverity(string message) =>
        message.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || message.Contains("error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drift", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drop", StringComparison.OrdinalIgnoreCase)
            ? ToastSeverity.Warning
            : ToastSeverity.Info;

    private async Task ClearStatusMessageLaterAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusMessageAutoClearDelay, token).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && string.Equals(StatusMessage, message, StringComparison.Ordinal))
                    StatusMessage = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }
}
