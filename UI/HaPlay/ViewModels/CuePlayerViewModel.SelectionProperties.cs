using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// The cue drawer's two-way bound facade over whatever cue is selected: number, label, visualizer feed and
/// duration, end target, and the comma-separated target lists of jump and fade cues (with the cue-number
/// range parsing those depend on).
/// <para>These are all the same SHAPE - read a projection of the selected node, validate, write it back,
/// raise the dependent notifications - and there are enough of them to bury the transport logic they used to
/// sit inside. Split out of the root file, 2026-07-30 review §3.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    /// <summary>Drawer-editable cue number with the same uniqueness rule as the rename dialog: a
    /// duplicate anywhere in the list is rejected (number-based jump/feed references need it).</summary>
    public string SelectedCueNumber
    {
        get => SelectedCueNode?.Number ?? string.Empty;
        set
        {
            if (SelectedCueNode is not { } cue)
                return;
            var trimmed = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && EnumerateAllCueNodes().Any(c => !ReferenceEquals(c, cue)
                    && string.Equals(c.Number?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = Strings.Format(nameof(Strings.CueNumberDuplicateFormat), trimmed);
                OnPropertyChanged(nameof(SelectedCueNumber));
                OnPropertyChanged(nameof(SelectedEndTargetText)); // revert the editor to the old value
                return;
            }

            cue.Number = trimmed;
            StatusMessage = null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedCueNumber));
            OnPropertyChanged(nameof(SelectedEndTargetText));
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
            OnPropertyChanged(nameof(SelectedFadeTargetsText));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    public string SelectedCueLabel
    {
        get => SelectedCueNode?.Label ?? string.Empty;
        set
        {
            if (SelectedCueNode is { } cue)
                cue.Label = value ?? string.Empty;
            OnPropertyChanged(nameof(SelectedCueLabel));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    /// <summary>Visualizer-cue feed sources as cue numbers (same pattern as jump targets).</summary>
    public string SelectedVisualizerFeedText
    {
        get
        {
            if (SelectedVisualizerCue is not { } viz)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", viz.VisualizerFeedCueIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedVisualizerCue is not { } viz)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var token in tokens)
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    c.Kind == CueNodeKind.Media
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !resolved.Contains(match.Id))
                    resolved.Add(match.Id);
                else if (match is null)
                    unknown.Add(token);
            }

            viz.VisualizerFeedCueIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Visualizer))
                if (!ReferenceEquals(target, viz))
                    target.VisualizerFeedCueIds = [.. resolved];
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        }
    }

    public bool SelectedVisualizerFeedAll
    {
        get => SelectedVisualizerCue is not { } v || v.VisualizerFeedAll;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerFeedAll = value;
                OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
            }
        }
    }

    /// <summary>Media end-trigger target (on natural end), entered by number and stored as a stable id.</summary>
    public string SelectedEndTargetText
    {
        get
        {
            if (SelectedMediaCue is not { } m || m.EndTargetCueId is not { } id)
                return string.Empty;
            var target = EnumerateAllCueNodes().FirstOrDefault(c => c.Id == id);
            return target is null ? "?" : (string.IsNullOrWhiteSpace(target.Number) ? target.Label : target.Number);
        }
        set
        {
            if (SelectedMediaCue is not { } m)
                return;
            var token = (value ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                m.EndTargetCueId = null;
            }
            else
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    !ReferenceEquals(c, m) && c.Kind is not CueNodeKind.Comment
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    StatusMessage = Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), token);
                }
                else
                {
                    m.EndTargetCueId = match.Id;
                    StatusMessage = null;
                }
            }

            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedEndTargetText));
        }
    }

    /// <summary>Visualizer timeline duration in seconds (0 = infinite).</summary>
    public double SelectedVisualizerDurationSeconds
    {
        get => SelectedVisualizerCue is { } v ? v.VisualizerDurationMs / 1000.0 : 0;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerDurationMs = (int)Math.Clamp(value * 1000.0, 0, int.MaxValue);
                OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
            }
        }
    }

    /// <summary>Visualizer-cue resolution preset ("WxH@F") - the media player dialog's buttons.</summary>
    [RelayCommand]
    private void SetVisualizerCueResolution(string spec)
    {
        if (SelectedVisualizerCue is not { } viz)
            return;
        var parts = spec.Split(['x', '@']);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var w)
            || !int.TryParse(parts[1], out var h)
            || !int.TryParse(parts[2], out var f))
            return;
        viz.VisualizerRenderWidth = w;
        viz.VisualizerRenderHeight = h;
        viz.VisualizerRenderFps = f;
        OnPropertyChanged(nameof(SelectedVisualizerCue)); // refresh the numerics
    }

    [RelayCommand(CanExecute = nameof(CanNextVisualizerPreset))]
    private async Task NextVisualizerPresetAsync()
    {
        if (SelectedVisualizerCue is not { } cue
            || NextVisualizerPresetCallback is not { } callback)
            return;

        var compositionId = VisualizerTargetComposition(cue);
        if (compositionId != Guid.Empty)
            await callback(compositionId);
    }

    private bool CanNextVisualizerPreset() =>
        SelectedVisualizerCue is not null
        && NextVisualizerPresetCallback is not null;

    /// <summary>Drawer gate for the jump-cue section.</summary>
    public bool IsJumpCueSelected => SelectedJumpCue is not null;

    /// <summary>Drawer gate for the visualizer-cue section (#26).</summary>
    public bool IsVisualizerCueSelected => SelectedVisualizerCue is not null;

    public Guid SelectedVisualizerCompositionId
    {
        get => SelectedVisualizerCue?.VisualizerCompositionId ?? Guid.Empty;
        set
        {
            if (SelectedVisualizerCue is { } v)
                v.VisualizerCompositionId = value;
        }
    }

    public bool SelectedVisualizerStarts
    {
        get => SelectedVisualizerCue is { } v
               && !string.Equals(v.Extra, "Stop", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.Extra = value ? "Start" : "Stop";
                OnPropertyChanged(nameof(SelectedVisualizerStarts));
            }
        }
    }

    /// <summary>Jump targets as CUE NUMBERS (display/entry); stored as stable cue IDs (renumber-safe).
    /// Setting parses a comma/space separated list (including inclusive hierarchical ranges such as
    /// 2.2-2.4), resolves each number against the list, keeps the resolvable ones, and reports unknowns
    /// in the status bar.</summary>
    public string SelectedJumpTargetsText
    {
        get
        {
            if (SelectedJumpCue is not { } jump)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", jump.JumpTargetIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedJumpCue is not { } jump)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var enteredToken in tokens)
            {
                foreach (var token in ExpandCueNumberToken(enteredToken))
                {
                    var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                        !ReferenceEquals(c, jump)
                        && c.Kind is not CueNodeKind.Comment
                        && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !resolved.Contains(match.Id))
                        resolved.Add(match.Id);
                    else if (match is null)
                        unknown.Add(token);
                }
            }

            jump.JumpTargetIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                if (!ReferenceEquals(target, jump))
                    target.JumpTargetIds = [.. resolved];
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                _lastRandomJumpTargetIds.Remove(target.Id);
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
        }
    }

    private const int MaximumCueNumberRangeSize = 10_000;

    /// <summary>
    /// Expands only unambiguous numeric, dot-hierarchical ranges. Other hyphenated values remain
    /// literal cue numbers, so a custom number such as "intro-1" retains its existing meaning.
    /// </summary>
    private static IReadOnlyList<string> ExpandCueNumberToken(string token)
    {
        var separator = token.IndexOf('-');
        if (separator <= 0
            || separator != token.LastIndexOf('-')
            || separator >= token.Length - 1
            || !TryParseHierarchicalCueNumber(token[..separator], out var start)
            || !TryParseHierarchicalCueNumber(token[(separator + 1)..], out var end)
            || start.Length != end.Length)
            return [token];

        for (var i = 0; i < start.Length - 1; i++)
        {
            if (start[i] != end[i])
                return [token];
        }

        var distance = Math.Abs((long)end[^1] - start[^1]);
        if (distance >= MaximumCueNumberRangeSize)
            return [token];

        var prefix = start.Length == 1
            ? string.Empty
            : $"{string.Join('.', start[..^1])}.";
        var step = start[^1] <= end[^1] ? 1 : -1;
        var expanded = new List<string>((int)distance + 1);
        for (var value = start[^1];; value += step)
        {
            expanded.Add($"{prefix}{value.ToString(CultureInfo.InvariantCulture)}");
            if (value == end[^1])
                break;
        }

        return expanded;
    }

    private static bool TryParseHierarchicalCueNumber(string text, out int[] components)
    {
        var parts = text.Split('.');
        components = new int[parts.Length];
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        return true;
    }

    public bool SelectedJumpRandom
    {
        get => SelectedJumpCue is { } j && j.JumpRandom;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpRandom = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpRandom));
            }
        }
    }

    public bool SelectedJumpAvoidImmediateRepeat
    {
        get => SelectedJumpCue is { } j && j.JumpAvoidImmediateRepeat;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpAvoidImmediateRepeat = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
            }
        }
    }

    public bool SelectedJumpFiresTarget
    {
        get => SelectedJumpCue is { } j
               && !string.Equals(j.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.SourceOrAction = value ? "fire" : "standby";
                RefreshCueTargetDisplays();
            }
        }
    }

    /// <summary>Drawer gate for the fade-cue section.</summary>
    public bool IsFadeCueSelected => SelectedFadeCue is not null;

    /// <summary>Fade targets as CUE NUMBERS (display/entry) stored as stable cue IDs - the Jump-targets
    /// pattern, including inclusive hierarchical ranges (2.2-2.4). Media cues and groups are valid
    /// targets (a group fades its descendant media cues).</summary>
    public string SelectedFadeTargetsText
    {
        get
        {
            if (SelectedFadeCue is not { } fade)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", fade.FadeTargetIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedFadeCue is not { } fade)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var enteredToken in tokens)
            {
                foreach (var token in ExpandCueNumberToken(enteredToken))
                {
                    var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                        !ReferenceEquals(c, fade)
                        && c.Kind is CueNodeKind.Media or CueNodeKind.Group
                        && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !resolved.Contains(match.Id))
                        resolved.Add(match.Id);
                    else if (match is null)
                        unknown.Add(token);
                }
            }

            fade.FadeTargetIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Fade))
                if (!ReferenceEquals(target, fade))
                    target.FadeTargetIds = [.. resolved];
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedFadeTargetsText));
        }
    }

    /// <summary>Resolves the cue ids a Fade cue actually ramps (UI thread - reads transport state):
    /// every active cue for TargetAllPlaying, else the explicit targets with groups expanded to their
    /// descendant media cues.
    /// <para>Explicit targets resolve inside the fade cue's OWN list, exactly like a Jump cue's targets
    /// (<see cref="ExecuteJumpCueOnUi"/>): fade target ids are authored links WITHIN one list, and the
    /// cross-list merged session fires a foreign list's cues headlessly. Resolving against the SELECTED
    /// list instead found none of them, so a scheduled/triggered Fade in another list silently ramped
    /// nothing (or, worse, matched an unrelated id).</para></summary>
    internal IReadOnlyList<Guid> ResolveFadeCueTargetsOnUi(CueNodeViewModel fade)
    {
        if (fade.FadeTargetAllPlaying)
            return _activeCueIds.ToList();

        var byId = EnumerateAllCueNodesFor(fade).ToDictionary(c => c.Id, c => c);
        var resolved = new List<Guid>();
        foreach (var id in fade.FadeTargetIds)
        {
            if (!byId.TryGetValue(id, out var target))
                continue;
            if (target.Kind == CueNodeKind.Group)
            {
                foreach (var media in EnumerateMediaNodes(target.Children))
                    if (!resolved.Contains(media.Id))
                        resolved.Add(media.Id);
            }
            else if (target.Kind == CueNodeKind.Media && !resolved.Contains(target.Id))
            {
                resolved.Add(target.Id);
            }
        }

        return resolved;
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        // Programmatic selections (add/duplicate/load) do not necessarily pass through
        // UpdateSelection. Keep the multi-edit subscriptions aligned in that path too.
        ResubscribeMultiEditSelection();
        _selectedCuePendingForGo = value is not null;
        // Loaded trigger rows carry no edit-time transport-clash veto (FromModel has no player
        // context); stamp it when the row surfaces in the drawer. Selecting away cancels MIDI learn.
        MidiLearnTarget = null;
        if (value is not null)
        {
            foreach (var row in value.Triggers)
                row.HotkeyConflictProbe ??= ProbeTriggerHotkeyConflict;
        }
        OnPropertyChanged(nameof(SelectedCueNumber));
        OnPropertyChanged(nameof(SelectedEndTargetText));
        OnPropertyChanged(nameof(SelectedCueLabel));
        OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
        OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
        OnPropertyChanged(nameof(IsJumpCueSelected));
        OnPropertyChanged(nameof(IsVisualizerCueSelected));
        OnPropertyChanged(nameof(SelectedVisualizerCompositionId));
        OnPropertyChanged(nameof(SelectedVisualizerStarts));
        OnPropertyChanged(nameof(SelectedJumpTargetsText));
        OnPropertyChanged(nameof(SelectedJumpRandom));
        OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
        OnPropertyChanged(nameof(SelectedJumpFiresTarget));
        OnPropertyChanged(nameof(SelectedFadeCue));
        OnPropertyChanged(nameof(IsFadeCueSelected));
        OnPropertyChanged(nameof(SelectedFadeTargetsText));
        // The selected cue's probe fields can land AFTER selection (when the operator picks a
        // file via "Browse media…"; the probe is async). Re-subscribe so the Video tab visibility
        // re-evaluates when the probe finishes.
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged -= OnSelectedCueProbeChanged;
        _watchedSelectedCueForProbe = value;
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged += OnSelectedCueProbeChanged;

        // In-place edits to the selected cue's routes/placements/offsets don't go through the
        // add/remove commands (those already suggest a refresh), so watch the node directly to keep
        // its standby pre-roll warm after gain/channel/opacity/offset tweaks. Debounced downstream.
        WatchSelectedCueForPreRoll(value);

        // Cues loaded from disk have no probed track list yet - fill the audio-track picker lazily
        // on first selection (stream-table probe only, no decoder build).
        if (value is { Kind: CueNodeKind.Media })
            _ = EnsureAudioTrackChoicesAsync(value);

        SelectedAudioRoute = SelectedAudioCue?.AudioRoutes.FirstOrDefault();
        SelectedVideoPlacement = SelectedVideoCue?.VideoPlacements.FirstOrDefault();
        RemoveNodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedTextCue));
        OnPropertyChanged(nameof(HasSelectedStaticCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(HasSelectedCue));
        OnPropertyChanged(nameof(IsSelectedCueInTimelineGroup));
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        ApplyCueDownmixPresetCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        FireSelectedVisualizerCommand.NotifyCanExecuteChanged();
        FireSelectedCueNowCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
        NextVisualizerPresetCommand.NotifyCanExecuteChanged();
        BrowseMediaSourceCommand.NotifyCanExecuteChanged();
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
        EditActionCueCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPreviewingSelectedCue));
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        RefreshVideoFrameRateMismatchWarning();
        ExtractCueWaveform(value);
        RefreshMultiEditSelectionState(resetSelectedItems: false);

        if (SelectedActionCue is { } actionCue && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        else
            SelectedActionEndpoint = null;
    }

    partial void OnSelectedAudioRouteChanged(CueAudioRouteViewModel? value)
    {
        WatchSelectedAudioRouteForMultiEdit(value);
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoPlacementChanged(CueVideoPlacementViewModel? value)
    {
        WatchSelectedVideoPlacementForMultiEdit(value);
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        ApplyPlacementLayoutCommand.NotifyCanExecuteChanged();
        EditSelectedPlacementVideoFxCommand.NotifyCanExecuteChanged();
        ApplyCropPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
        RefreshVideoFrameRateMismatchWarning();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCueEditModeChanged(bool value)
    {
        _ = value;
        MoveSelectedCueUpCommand.NotifyCanExecuteChanged();
        MoveSelectedCueDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnStandbyCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        RebuildUpcomingCues();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        if (!_suppressStandbyPreRollRefresh)
            SuggestPreRollRefresh();
    }
}
