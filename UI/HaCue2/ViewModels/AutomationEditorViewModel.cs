using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Presentation;
using S.Media.Session;

namespace HaCue2.ViewModels;

/// <summary>A scrollable absolute-time editor for one concrete animatable property.</summary>
public sealed partial class AutomationEditorViewModel : ObservableObject
{
    private readonly ProjectJournal? _journal;
    private readonly CueNode? _cue;
    private readonly AutomationTrack? _track;
    private readonly AutomationPropertyDescriptor _descriptor;
    private readonly ShowHost? _host;
    private readonly MediaCueNode? _waveformCue;
    private readonly string _waveformPath = "";
    private readonly string _cacheRoot = "";
    private readonly long? _waveformCacheBytes;
    private readonly TimeSpan? _waveformSourceDuration;
    private CancellationTokenSource? _waveformScan;
    private IReadOnlyList<float>? _cuePeaks;
    private readonly HashSet<Guid> _selection = [];
    private List<AutomationKeyframe> _visibleKeys = [];
    private Guid? _gestureKeyId;
    private List<AutomationKeyframe>? _gestureOriginalKeys;
    private List<AutomationKeyframe>? _gestureDraftKeys;
    private double _gestureViewStartMs;
    private double _gestureViewLengthMs;
    private int _gestureAxis;

    public AutomationEditorViewModel()
    {
        _descriptor = AutomationPropertyCatalog.Get(AutomationPropertyIds.CueVolume)!;
        IsResolved = true;
        Title = "Automation";
        DurationMs = 120_000;
        _viewLengthMs = 30_000;
        Reload();
    }

    public AutomationEditorViewModel(
        ProjectJournal journal,
        CueNode cue,
        AutomationTrack track,
        TimeSpan? duration,
        ShowHost? host = null,
        MediaCueNode? waveformCue = null,
        string? waveformPath = null,
        TimeSpan? waveformSourceDuration = null,
        string cacheRoot = "",
        long? waveformCacheBytes = null)
    {
        _journal = journal;
        _cue = cue;
        _track = track;
        _host = host;
        _waveformCue = waveformCue;
        _waveformPath = waveformPath ?? "";
        _waveformSourceDuration = waveformSourceDuration;
        _cacheRoot = cacheRoot;
        _waveformCacheBytes = waveformCacheBytes;
        var descriptor = AutomationPropertyCatalog.Get(track.Target.PropertyId);
        IsResolved = descriptor is not null;
        _descriptor = descriptor ?? UnresolvedDescriptor(track);
        CanExtend = duration is null;
        var lastKeyMs = track.Keyframes.Select(key => key.TimeMs).DefaultIfEmpty(0).Max();
        DurationMs = Math.Max(
            1_000,
            (long)(duration?.TotalMilliseconds
                   ?? Math.Max(30_000, lastKeyMs + 30_000)));
        _viewLengthMs = Math.Min(DurationMs, 30_000);
        Title = $"Automation · Q{CuePresentation.Number(cue.Number)} · {TargetName(cue, track)}";
        Reload();
        if (!IsResolved)
            Problem = $"'{track.Target.PropertyId}' is unavailable on this machine; the track is preserved read-only";
    }

    public string Title { get; }
    public bool IsResolved { get; }
    public bool CanEdit => IsResolved;
    public long DurationMs { get; private set; }
    public bool CanExtend { get; }
    public string DurationLabel => (CanExtend ? "open · " : "")
                                   + ClipTimes.Format((int)Math.Min(int.MaxValue, DurationMs));
    public string Unit => IsResolved ? _descriptor.Value.Unit : "unresolved";
    public string Hint => IsResolved
        ? $"{DurationLabel} · absolute cue time · click-drag to add · Alt bypasses snap · Shift constrains"
        : $"{DurationLabel} · unresolved property · preserved read-only until its effect/plugin is available";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaveform))]
    [NotifyPropertyChangedFor(nameof(WaveformStatus))]
    private IReadOnlyList<float>? _waveformPeaks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaveformStatus))]
    private bool _isScanningWaveform;

    public bool HasWaveform => WaveformPeaks is { Count: > 0 };
    public string WaveformStatus => IsScanningWaveform
        ? "reading waveform…"
        : _waveformPath.Length > 0 && !HasWaveform ? "no audio waveform" : "";

    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _points = [];

    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _shape = [];

    [ObservableProperty]
    private IReadOnlyList<string> _rulerTicks = [];

    public IReadOnlyList<CurveTangent> Tangents => [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewEndLabel))]
    [NotifyPropertyChangedFor(nameof(ViewLabel))]
    private double _viewStartMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewEndLabel))]
    [NotifyPropertyChangedFor(nameof(ViewLabel))]
    [NotifyPropertyChangedFor(nameof(ViewMaxStartMs))]
    private double _viewLengthMs = 30_000;

    public double ViewMaxStartMs => Math.Max(0, DurationMs - ViewLengthMs);
    public string ViewStartLabel => ClipTimes.Format((int)ViewStartMs);
    public string ViewEndLabel => ClipTimes.Format((int)Math.Min(DurationMs, ViewStartMs + ViewLengthMs));
    public string ViewLabel => $"{ViewStartLabel} – {ViewEndLabel}";

    partial void OnViewStartMsChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, ViewMaxStartMs);
        if (Math.Abs(clamped - value) > 0.01)
        {
            ViewStartMs = clamped;
            return;
        }
        OnPropertyChanged(nameof(ViewStartLabel));
        RefreshWaveformViewport();
        Reload();
    }

    partial void OnViewLengthMsChanged(double value)
    {
        var clamped = Math.Clamp(value, Math.Min(500, DurationMs), DurationMs);
        if (Math.Abs(clamped - value) > 0.01)
        {
            ViewLengthMs = clamped;
            return;
        }
        ViewStartMs = Math.Clamp(ViewStartMs, 0, ViewMaxStartMs);
        RefreshWaveformViewport();
        Reload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorLabel))]
    private double _cursorMs;

    public string CursorLabel => ClipTimes.Format((int)Math.Clamp(CursorMs, 0, DurationMs));

    partial void OnCursorMsChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, DurationMs);
        if (Math.Abs(clamped - value) > 0.01)
            CursorMs = clamped;
    }

    [ObservableProperty]
    private string _problem = "";

    [ObservableProperty]
    private string _pointTime = "—";

    [ObservableProperty]
    private string _pointValue = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanEditSelection))]
    private Guid? _primaryKeyId;

    public bool HasSelection => PrimaryKeyId is not null;
    public bool CanEditSelection => CanEdit && HasSelection;
    public int SelectionCount => _selection.Count;
    public bool HasMultipleSelected => _selection.Count > 1;

    public IReadOnlyList<string> Segments { get; } = ["linear", "equal power", "exponential", "S-curve"];
    public IReadOnlyList<int> SnapTimeOptions { get; } = [0, 10, 40, 100, 1_000];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapLabel))]
    private int _snapTimeMs = 100;

    public string SnapLabel => SnapTimeMs <= 0 ? "off" : $"{SnapTimeMs} ms";

    private double ValueSnap => _descriptor.Value.Scale switch
    {
        AutomationScale.Decibels => 0.1,
        AutomationScale.Percentage => 0.01,
        AutomationScale.Midi7Bit => 1,
        _ when _descriptor.Value.Unit == "°" => 1,
        _ => Math.Max(0.001, (_descriptor.Value.Maximum - _descriptor.Value.Minimum) / 100d),
    };

    public string Segment
    {
        get => SelectedKey()?.Curve.Law switch
        {
            FadeCurve.EqualPower => "equal power",
            FadeCurve.Exponential => "exponential",
            FadeCurve.SCurve => "S-curve",
            _ => "linear",
        };
        set
        {
            if (!CanEdit || SelectedKey() is not { } key)
                return;
            var law = value switch
            {
                "equal power" => FadeCurve.EqualPower,
                "exponential" => FadeCurve.Exponential,
                "S-curve" => FadeCurve.SCurve,
                _ => FadeCurve.Linear,
            };
            ReplaceKey(key.Id, current => current with
            {
                Curve = current.Curve with { Law = law, PresetId = null, Points = null },
            }, "set automation segment curve");
        }
    }

    public bool IsHold
    {
        get => SelectedKey()?.Hold ?? false;
        set
        {
            if (CanEdit && SelectedKey() is { } key && key.Hold != value)
                ReplaceKey(key.Id, current => current with { Hold = value }, "toggle automation hold");
        }
    }

    public bool IsEnabled
    {
        get => _track?.Enabled ?? true;
        set
        {
            if (!CanEdit || _track is not { } track || _journal is null || track.Enabled == value)
                return;
            _journal.Do(new SetValueCommand<bool>(
                _cue!.Id, $"automation:{track.Id}:enabled", "cues",
                () => track.Enabled, enabled => track.Enabled = enabled,
                value, value ? "enable automation" : "disable automation"));
            _journal.CloseGroup();
            OnPropertyChanged();
        }
    }

    public void Apply(CurveGesture gesture)
    {
        if (_track is null || _journal is null)
            return;

        if (gesture.Kind is CurveGestureKind.Select or CurveGestureKind.ToggleSelection
            or CurveGestureKind.RangeSelection or CurveGestureKind.ClearSelection)
        {
            Select(gesture);
            return;
        }

        if (!CanEdit)
            return;

        BeginGestureDraft();
        var keys = _gestureDraftKeys!;

        if (gesture.Kind == CurveGestureKind.Move && gesture.IsNudge)
        {
            if (GestureKey(gesture.Index) is not { } nudged)
                return;
            if (!_selection.Contains(nudged.Id))
                SelectOnly(nudged);
            var multiplier = gesture.Accelerated ? 5 : 1;
            var timeStep = SnapTimeMs > 0
                ? SnapTimeMs
                : Math.Max(1, (int)Math.Round(ViewLengthMs / 1_000d));
            foreach (var key in keys.Where(key => _selection.Contains(key.Id)))
            {
                key.TimeMs = Math.Clamp(
                    key.TimeMs + ((long)(gesture.X * timeStep * multiplier)), 0, DurationMs);
                key.Value = _descriptor.Value.Clamp(
                    key.Value - (gesture.Y * ValueSnap * multiplier));
            }
            CursorMs = nudged.TimeMs;
            Reload();
            return;
        }

        var timeMs = Math.Clamp(
            (long)Math.Round(_gestureViewStartMs
                             + (Math.Clamp(gesture.X, 0, 1) * _gestureViewLengthMs)),
            0,
            DurationMs);
        var value = ValueAtCanvasY(gesture.Y);
        if (!gesture.BypassSnap)
        {
            if (SnapTimeMs > 0)
                timeMs = Math.Clamp(
                    (long)Math.Round((double)timeMs / SnapTimeMs) * SnapTimeMs, 0, DurationMs);
            var valueStep = ValueSnap;
            value = _descriptor.Value.Clamp(Math.Round(value / valueStep) * valueStep);
        }

        switch (gesture.Kind)
        {
            case CurveGestureKind.Add when keys.All(key => Math.Abs(key.TimeMs - timeMs) > 4):
                var added = new AutomationKeyframe { TimeMs = timeMs, Value = value };
                keys.Add(added);
                _selection.Clear();
                _selection.Add(added.Id);
                PrimaryKeyId = added.Id;
                _gestureKeyId = added.Id;
                _gestureOriginalKeys = keys.Select(Clone).ToList();
                // Tells the canvas a key really was created, so it may take capture and begin dragging it.
                gesture.Accepted = true;
                break;
            case CurveGestureKind.Move when GestureKey(gesture.Index) is { } moved:
                if (!_selection.Contains(moved.Id))
                    SelectOnly(moved);
                var originalKeys = _gestureOriginalKeys!;
                var original = originalKeys.First(key => key.Id == moved.Id);
                var deltaTime = timeMs - original.TimeMs;
                var deltaValue = value - original.Value;
                if (gesture.ConstrainAxis)
                {
                    if (_gestureAxis == 0 && (deltaTime != 0 || Math.Abs(deltaValue) > double.Epsilon))
                    {
                        var timeDistance = Math.Abs(deltaTime / Math.Max(1d, _gestureViewLengthMs));
                        var valueDistance = Math.Abs(deltaValue
                                                     / Math.Max(double.Epsilon,
                                                         _descriptor.Value.Maximum - _descriptor.Value.Minimum));
                        _gestureAxis = timeDistance >= valueDistance ? 1 : 2;
                    }
                    if (_gestureAxis == 1)
                        deltaValue = 0;
                    else if (_gestureAxis == 2)
                        deltaTime = 0;
                }
                foreach (var key in keys.Where(key => _selection.Contains(key.Id)))
                {
                    var baseline = originalKeys.First(item => item.Id == key.Id);
                    key.TimeMs = Math.Clamp(baseline.TimeMs + deltaTime, 0, DurationMs);
                    key.Value = _descriptor.Value.Clamp(baseline.Value + deltaValue);
                }
                CursorMs = timeMs;
                break;
            case CurveGestureKind.Remove when GestureKey(gesture.Index) is { } removed:
                keys.RemoveAll(key => key.Id == removed.Id);
                _selection.Remove(removed.Id);
                if (PrimaryKeyId == removed.Id)
                    PrimaryKeyId = _selection.FirstOrDefault() is var next && next != Guid.Empty ? next : null;
                break;
            case CurveGestureKind.RemoveSelection:
                keys.RemoveAll(key => _selection.Contains(key.Id));
                _selection.Clear();
                PrimaryKeyId = null;
                break;
            default:
                return;
        }

        Reload();
    }

    public void EndGesture()
    {
        var draft = _gestureDraftKeys;
        ClearGestureDraft();
        // Only journal when the gesture actually changed something. A press that was refused (a key
        // already occupies that time) or a drag that ended exactly where it began used to push an undo
        // step whose before and after were identical - so the project went dirty and the operator's next
        // Undo appeared to do nothing at all.
        if (draft is not null && ChangesAnything(draft))
            Write(draft, "edit automation keyframes");
        _gestureKeyId = null;
        _journal?.CloseGroup();
        Reload();
    }

    private bool ChangesAnything(IReadOnlyList<AutomationKeyframe> draft)
    {
        if (_track is null || draft.Count != _track.Keyframes.Count)
            return true;

        var before = _track.Keyframes.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).ToList();
        var after = draft.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).ToList();
        for (var index = 0; index < after.Count; index++)
        {
            var (was, now) = (before[index], after[index]);
            if (was.Id != now.Id
                || was.TimeMs != now.TimeMs
                || was.Value != now.Value
                || was.Hold != now.Hold
                || was.Curve != now.Curve)
                return true;
        }

        return false;
    }

    public void CancelGesture()
    {
        ClearGestureDraft();
        _gestureKeyId = null;
        Reload();
    }

    public CurveEditorViewModel? SegmentCurveEditor()
    {
        if (!CanEdit || _journal is null || _cue is null || _track is null
            || SelectedKey() is not { } key)
            return null;

        return new CurveEditorViewModel(
            _journal,
            new AutomationSegmentCurveTarget(_cue.Id, _track, key.Id, _journal.Project),
            $"{Title} · segment shape");
    }

    public void Refresh() => Reload();

    public void AddKeyAtCursor()
    {
        if (!CanEdit || _track is null || _journal is null)
            return;

        var timeMs = (long)Math.Round(Math.Clamp(CursorMs, 0, DurationMs));
        if (_track.Keyframes.FirstOrDefault(key => Math.Abs(key.TimeMs - timeMs) <= 1) is { } existing)
        {
            SelectOnly(existing);
            Reveal(existing.TimeMs);
            Reload();
            return;
        }

        var value = AutomationEvaluator.Sample(_track, _journal.Project, timeMs, AuthoredValue());
        var added = new AutomationKeyframe { TimeMs = timeMs, Value = value };
        Write([.. _track.Keyframes.Select(Clone), added], "add automation keyframe at cursor");
        _journal.CloseGroup();
        SelectOnly(added);
        Reveal(timeMs);
        Problem = "";
        Reload();
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0)
            return;
        Apply(new CurveGesture(CurveGestureKind.RemoveSelection, -1, 0, 0));
        EndGesture();
    }

    public void SelectAll()
    {
        if (_track is null)
            return;
        _selection.Clear();
        foreach (var key in _track.Keyframes)
            _selection.Add(key.Id);
        PrimaryKeyId = _track.Keyframes.OrderBy(key => key.TimeMs).ThenBy(key => key.Id)
            .FirstOrDefault()?.Id;
        Reload();
    }

    public string? Copy()
    {
        if (_track is null || _selection.Count == 0)
        {
            Problem = "select one or more keyframes to copy";
            return null;
        }

        // Native milliseconds and native value: normalizing through DurationMs is what made a copy out of a
        // 45-minute cue collapse when pasted into a short one, and rescaled a dB value into a % range.
        var ordered = _track.Keyframes.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).ToList();
        Problem = $"copied {_selection.Count} keyframe(s)";
        return LaneKeyframeClipboard.EncodeAutomation(ordered, _selection);
    }

    public bool Paste(string? text)
    {
        if (!CanEdit || _track is null || _journal is null
            || LaneKeyframeClipboard.DecodeAutomation(text) is not { Count: > 0 } decoded)
        {
            Problem = "clipboard has no HaCue2 keyframes";
            return false;
        }

        // The shape keeps its authored millisecond spacing; only its ORIGIN moves to the playhead. It is
        // clamped so the tail lands inside the cue, never squeezed to fit.
        var span = decoded[^1].OffsetMs - decoded[0].OffsetMs;
        var at = (long)Math.Round(Math.Clamp(CursorMs, 0, Math.Max(0, DurationMs - span)));
        var pasted = decoded
            .Select(clip => new AutomationKeyframe
            {
                TimeMs = Math.Clamp(at + clip.OffsetMs, 0, DurationMs),
                Value = _descriptor.Value.Clamp(clip.Value),
                Hold = clip.Hold,
                Curve = new CurveSpec { Law = clip.Law },
            })
            .ToList();
        var fromMs = pasted[0].TimeMs;
        var toMs = pasted[^1].TimeMs;
        var kept = _track.Keyframes.Where(key => key.TimeMs < fromMs || key.TimeMs > toMs);
        Write(kept.Concat(pasted), $"paste {pasted.Count} automation keyframes");
        _journal.CloseGroup();

        _selection.Clear();
        foreach (var key in pasted)
            _selection.Add(key.Id);
        PrimaryKeyId = pasted[0].Id;
        CursorMs = pasted[0].TimeMs;
        Problem = $"pasted {pasted.Count} keyframe(s)";
        Reload();
        return true;
    }

    public void CommitPointTime(string text)
    {
        if (!CanEdit || SelectedKey() is not { } key
            || ClipTimes.Parse(text, TimeSpan.FromMilliseconds(DurationMs)) is not { } time)
        {
            Problem = "enter a cue time such as 1:23.450";
            ReloadFields();
            return;
        }
        ReplaceKey(key.Id, current => current with { TimeMs = Math.Clamp(time, 0, DurationMs) },
            "set automation key time");
        CursorMs = time;
    }

    public void CommitPointValue(string text)
    {
        if (!CanEdit || SelectedKey() is not { } key
            || !double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            Problem = "enter a numeric property value";
            ReloadFields();
            return;
        }
        if (_descriptor.Value.Scale == AutomationScale.Percentage && text.Contains('%'))
            value /= 100;
        ReplaceKey(key.Id, current => current with { Value = _descriptor.Value.Clamp(value) },
            "set automation key value");
    }

    public void Zoom(double factor)
    {
        var anchor = Math.Clamp(CursorMs, ViewStartMs, ViewStartMs + ViewLengthMs);
        var fraction = ViewLengthMs <= 0 ? 0.5 : (anchor - ViewStartMs) / ViewLengthMs;
        ViewLengthMs = Math.Clamp(ViewLengthMs * factor, Math.Min(500, DurationMs), DurationMs);
        ViewStartMs = Math.Clamp(anchor - (fraction * ViewLengthMs), 0, ViewMaxStartMs);
    }

    public void Pan(double fraction) =>
        ViewStartMs = Math.Clamp(ViewStartMs + (ViewLengthMs * fraction), 0, ViewMaxStartMs);

    public void Fit()
    {
        ViewLengthMs = DurationMs;
        ViewStartMs = 0;
    }

    public void Extend()
    {
        if (!CanExtend)
            return;
        DurationMs = Math.Min(int.MaxValue, DurationMs + (30 * 60 * 1000L));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(ViewMaxStartMs));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(ViewLabel));
    }

    public void JumpKey(int direction)
    {
        if (_track is null)
            return;
        var ordered = _track.Keyframes.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).ToList();
        var key = direction < 0
            ? ordered.LastOrDefault(candidate => candidate.TimeMs < CursorMs)
            : ordered.FirstOrDefault(candidate => candidate.TimeMs > CursorMs);
        if (key is null)
            return;
        CursorMs = key.TimeMs;
        PrimaryKeyId = key.Id;
        _selection.Clear();
        _selection.Add(key.Id);
        Reveal(key.TimeMs);
        Reload();
    }

    public async Task SeekAsync()
    {
        if (_host is null || _cue is null)
        {
            Problem = "the cue is not attached to a playback host";
            return;
        }
        var targetCueId = _cue is AutomationCueNode
            ? _cue.Id
            : _track?.Target.CueId ?? _cue.Id;
        Problem = await _host.SeekCueAsync(targetCueId, TimeSpan.FromMilliseconds(CursorMs)).ConfigureAwait(true) ?? "";
    }

    /// <summary>Loads audio context lazily when the editor window becomes visible.</summary>
    public async void BeginWaveform()
    {
        if (_waveformPath.Length == 0 || _waveformCue is null || _waveformScan is not null)
            return;

        _waveformScan = new CancellationTokenSource();
        var token = _waveformScan.Token;
        IsScanningWaveform = true;
        try
        {
            var peaks = WaveformCache.Read(_cacheRoot, _waveformPath)
                        ?? await MediaScan.WaveformAsync(_waveformPath, cancellationToken: token)
                            .ConfigureAwait(true);
            if (token.IsCancellationRequested || peaks is not { Length: > 0 })
                return;
            WaveformCache.Write(_cacheRoot, _waveformPath, peaks, _waveformCacheBytes);
            _cuePeaks = TrimToCue(peaks);
            RefreshWaveformViewport();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A waveform is context, never a prerequisite for editing automation.
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsScanningWaveform = false;
        }
    }

    public void EndWaveform()
    {
        _waveformScan?.Cancel();
        _waveformScan?.Dispose();
        _waveformScan = null;
    }

    private void Select(CurveGesture gesture)
    {
        if (gesture.Kind == CurveGestureKind.ClearSelection)
        {
            _selection.Clear();
            PrimaryKeyId = null;
            Reload();
            return;
        }
        if (KeyAtVisibleIndex(gesture.Index) is not { } key)
            return;
        if (gesture.Kind == CurveGestureKind.ToggleSelection)
        {
            if (!_selection.Add(key.Id))
                _selection.Remove(key.Id);
        }
        else if (gesture.Kind == CurveGestureKind.RangeSelection
                 && PrimaryKeyId is { } anchorId
                 && _visibleKeys.FindIndex(candidate => candidate.Id == anchorId) is var anchor
                 && anchor >= 0)
        {
            var to = _visibleKeys.FindIndex(candidate => candidate.Id == key.Id);
            _selection.Clear();
            for (var index = Math.Min(anchor, to); index <= Math.Max(anchor, to); index++)
                _selection.Add(_visibleKeys[index].Id);
        }
        else
        {
            _selection.Clear();
            _selection.Add(key.Id);
        }
        PrimaryKeyId = _selection.Contains(key.Id) ? key.Id : _selection.FirstOrDefault() is var next && next != Guid.Empty ? next : null;
        CursorMs = key.TimeMs;
        Reload();
    }

    private AutomationKeyframe? KeyAtVisibleIndex(int index) =>
        index >= 0 && index < _visibleKeys.Count ? _visibleKeys[index] : null;

    private AutomationKeyframe? GestureKey(int index)
    {
        var key = _gestureKeyId is { } id
            ? EditableKeys().FirstOrDefault(candidate => candidate.Id == id)
            : KeyAtVisibleIndex(index);
        _gestureKeyId ??= key?.Id;
        return key;
    }

    private AutomationKeyframe? SelectedKey() =>
        EditableKeys().FirstOrDefault(key => key.Id == PrimaryKeyId);

    private IReadOnlyList<AutomationKeyframe> EditableKeys() =>
        _gestureDraftKeys ?? _track?.Keyframes ?? [];

    /// <summary>Keys past the cue's end. Absolute time deliberately does not rescale when a cue is
    /// shortened, so these are legitimate and preserved - but the ruler stops at the out-point, so without
    /// this they are invisible and unreachable: the tail of a carefully drawn track silently stops playing
    /// with nothing on screen to say so.</summary>
    public int OutOfRangeKeyCount => EditableKeys().Count(key => key.TimeMs > DurationMs);

    public bool HasOutOfRangeKeys => OutOfRangeKeyCount > 0;

    public string OutOfRangeLabel => OutOfRangeKeyCount switch
    {
        0 => "",
        1 => "1 keyframe sits past the end of this cue and never plays",
        var count => $"{count} keyframes sit past the end of this cue and never play",
    };

    /// <summary>Deletes exactly the keys past the cue's end, in one undo step. The explicit command the
    /// design asks for, so an operator can resolve the warning without hunting for keys they cannot see.
    /// </summary>
    public bool DeleteOutOfRangeKeys()
    {
        if (!CanEdit || _track is null || _journal is null || !HasOutOfRangeKeys)
        {
            Problem = "no keyframes past the end of this cue";
            return false;
        }

        var removed = OutOfRangeKeyCount;
        Write(_track.Keyframes.Where(key => key.TimeMs <= DurationMs), "delete out-of-range keyframes");
        _journal.CloseGroup();
        Problem = $"deleted {removed} keyframe(s) past the cue's end";
        Reload();
        return true;
    }

    private void BeginGestureDraft()
    {
        if (_gestureDraftKeys is not null)
            return;
        _gestureOriginalKeys = _track?.Keyframes.Select(Clone).ToList() ?? [];
        _gestureDraftKeys = _gestureOriginalKeys.Select(Clone).ToList();
        _gestureViewStartMs = ViewStartMs;
        _gestureViewLengthMs = ViewLengthMs;
        _gestureAxis = 0;
    }

    private void ClearGestureDraft()
    {
        _gestureOriginalKeys = null;
        _gestureDraftKeys = null;
        _gestureAxis = 0;
    }

    private void SelectOnly(AutomationKeyframe key)
    {
        _selection.Clear();
        _selection.Add(key.Id);
        PrimaryKeyId = key.Id;
        CursorMs = key.TimeMs;
    }

    private double AuthoredValue()
    {
        if (_track is null || _journal is null || _cue is null)
            return _descriptor.Value.Default;
        var targetCue = _track.Target.CueId is { } targetId
            ? _journal.Project.FindCue(targetId)
            : _cue;
        return targetCue is null
            ? _descriptor.Value.Default
            : AutomationPropertyCatalog.ForCue(targetCue)
                .FirstOrDefault(option =>
                    option.Target.PropertyId == _track.Target.PropertyId
                    && option.Target.ObjectId == _track.Target.ObjectId)
                ?.AuthoredValue ?? _descriptor.Value.Default;
    }

    private void ReplaceKey(Guid id, Func<AutomationKeyframe, AutomationKeyframe> replace, string description)
    {
        if (!CanEdit || _track is null)
            return;
        var keys = _track.Keyframes.Select(key => key.Id == id ? replace(Clone(key)) : Clone(key)).ToList();
        Write(keys, description);
        _journal?.CloseGroup();
        Problem = "";
        Reload();
    }

    private void Write(IEnumerable<AutomationKeyframe> keys, string description)
    {
        if (!CanEdit || _track is null || _journal is null || _cue is null)
            return;
        var next = keys.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).Select(Clone).ToList();
        _journal.Do(new SetValueCommand<List<AutomationKeyframe>>(
            _cue.Id, $"automation:{_track.Id}", "cues",
            () => _track.Keyframes.Select(Clone).ToList(),
            value => _track.Keyframes = value.Select(Clone).ToList(),
            next,
            description));
    }

    private static AutomationPropertyDescriptor UnresolvedDescriptor(AutomationTrack track)
    {
        var values = track.Keyframes.Where(key => double.IsFinite(key.Value)).Select(key => key.Value).ToArray();
        var minimum = values.Length == 0 ? 0 : values.Min();
        var maximum = values.Length == 0 ? 1 : values.Max();
        if (maximum <= minimum)
        {
            minimum -= 0.5;
            maximum += 0.5;
        }
        return new AutomationPropertyDescriptor(
            track.Target.PropertyId,
            track.Target.PropertyId,
            new AutomationValueSpec(minimum, maximum, values.FirstOrDefault(), "", AutomationScale.Linear),
            AutomationTargetKind.Cue,
            AutomationDomain.Host,
            AutomationComposition.ReplaceAuthored,
            "Unavailable",
            SupportsCueOwnedTrack: false,
            SupportsAutomationCue: false);
    }

    private void Reload()
    {
        if (_track is null)
        {
            Points = [];
            Shape = [];
            RulerTicks = [];
            return;
        }

        var end = ViewStartMs + ViewLengthMs;
        var editableKeys = EditableKeys();
        // FOUR ticks, one per quarter of the viewport, each labelled with the time at its own LEFT edge -
        // which is where the template draws its border. Emitting five (0, ¼, ½, ¾, 1) into a five-cell
        // uniform grid put every label a FIFTH of the width left of the time it named, so the last one was
        // off by 20 % of the visible span.
        const int ticks = 4;
        RulerTicks =
        [
            .. Enumerable.Range(0, ticks).Select(index =>
                ClipTimes.Format((int)Math.Clamp(
                    ViewStartMs + ((double)index / ticks * ViewLengthMs), 0, DurationMs))),
        ];
        _visibleKeys = editableKeys
            .Where(key => key.TimeMs >= ViewStartMs && key.TimeMs <= end)
            .OrderBy(key => key.TimeMs).ThenBy(key => key.Id)
            .ToList();
        Points = [.. _visibleKeys.Select(key => new CurvePoint(
            (key.TimeMs - ViewStartMs) / ViewLengthMs,
            CanvasY(key.Value),
            _selection.Contains(key.Id)))];

        const int samples = 128;
        Shape =
        [
            .. Enumerable.Range(0, samples + 1).Select(index =>
            {
                var x = (double)index / samples;
                var time = (long)Math.Round(ViewStartMs + (x * ViewLengthMs));
                var value = AutomationEvaluator.Sample(
                    _track with { Keyframes = editableKeys.Select(Clone).ToList() },
                    _journal?.Project ?? new HaCueProject(),
                    time,
                    _descriptor.Value.Default);
                return new CurvePoint(x, CanvasY(value));
            }),
        ];
        ReloadFields();
        OnPropertyChanged(nameof(Segment));
        OnPropertyChanged(nameof(IsHold));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(ViewStartLabel));
        OnPropertyChanged(nameof(OutOfRangeKeyCount));
        OnPropertyChanged(nameof(HasOutOfRangeKeys));
        OnPropertyChanged(nameof(OutOfRangeLabel));
    }

    private IReadOnlyList<float> TrimToCue(float[] peaks)
    {
        if (_waveformCue is not { } media
            || _waveformSourceDuration is not { TotalMilliseconds: > 0 } source)
            return peaks;
        var from = Math.Clamp(
            (int)Math.Floor(media.TrimInMs / source.TotalMilliseconds * peaks.Length),
            0,
            peaks.Length - 1);
        var outMs = media.TrimOutMs > media.TrimInMs
            ? media.TrimOutMs
            : source.TotalMilliseconds;
        var to = Math.Clamp(
            (int)Math.Ceiling(outMs / source.TotalMilliseconds * peaks.Length),
            from + 1,
            peaks.Length);
        return peaks[from..to];
    }

    private void RefreshWaveformViewport()
    {
        if (_cuePeaks is not { Count: > 0 } peaks || DurationMs <= 0)
        {
            WaveformPeaks = null;
            return;
        }
        var from = Math.Clamp(
            (int)Math.Floor(ViewStartMs / DurationMs * peaks.Count), 0, peaks.Count - 1);
        var to = Math.Clamp(
            (int)Math.Ceiling((ViewStartMs + ViewLengthMs) / DurationMs * peaks.Count),
            from + 1,
            peaks.Count);
        WaveformPeaks = peaks.Skip(from).Take(to - from).ToArray();
    }

    private void ReloadFields()
    {
        if (SelectedKey() is not { } key)
        {
            PointTime = "—";
            PointValue = "—";
            return;
        }
        PointTime = ClipTimes.Format((int)Math.Min(int.MaxValue, key.TimeMs));
        PointValue = _descriptor.Value.Scale == AutomationScale.Percentage
            ? (key.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : key.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private double CanvasY(double value)
    {
        var spec = _descriptor.Value;
        var normalized = spec.Maximum <= spec.Minimum ? 0 : (spec.Clamp(value) - spec.Minimum) / (spec.Maximum - spec.Minimum);
        return 1 - Math.Clamp(normalized, 0, 1);
    }

    private double ValueAtCanvasY(double y)
    {
        var normalized = Math.Clamp(1 - y, 0, 1);
        return _descriptor.Value.Clamp(
            _descriptor.Value.Minimum + ((_descriptor.Value.Maximum - _descriptor.Value.Minimum) * normalized));
    }

    private void Reveal(long timeMs)
    {
        if (timeMs >= ViewStartMs && timeMs <= ViewStartMs + ViewLengthMs)
            return;
        ViewStartMs = Math.Clamp(timeMs - (ViewLengthMs / 2), 0, ViewMaxStartMs);
    }

    private static AutomationKeyframe Clone(AutomationKeyframe key) => key with
    {
        Curve = key.Curve with { Points = key.Curve.Points?.ToList() },
    };

    private static string TargetName(CueNode cue, AutomationTrack track)
    {
        var name = AutomationPropertyCatalog.Get(track.Target.PropertyId)?.DisplayName ?? track.Target.PropertyId;
        if (track.Target.ObjectId is { } objectId
            && CuePlacements.Of(cue).FirstOrDefault(placement => placement.Id == objectId) is { } placement)
            name += $" · layer {placement.LayerIndex}";
        return name;
    }
}
