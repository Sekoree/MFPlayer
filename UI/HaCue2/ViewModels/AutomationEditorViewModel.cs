using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using HaCue2.Engine;
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
    private readonly HashSet<Guid> _selection = [];
    private List<AutomationKeyframe> _visibleKeys = [];
    private IDisposable? _drag;
    private Guid? _gestureKeyId;

    public AutomationEditorViewModel()
    {
        _descriptor = AutomationPropertyCatalog.Get(AutomationPropertyIds.CueVolume)!;
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
        ShowHost? host = null)
    {
        _journal = journal;
        _cue = cue;
        _track = track;
        _host = host;
        _descriptor = AutomationPropertyCatalog.Get(track.Target.PropertyId)
                      ?? throw new ArgumentException($"unknown automation property '{track.Target.PropertyId}'", nameof(track));
        CanExtend = duration is null;
        var lastKeyMs = track.Keyframes.Select(key => key.TimeMs).DefaultIfEmpty(0).Max();
        DurationMs = Math.Max(
            1_000,
            (long)(duration?.TotalMilliseconds
                   ?? Math.Max(30_000, lastKeyMs + 30_000)));
        _viewLengthMs = Math.Min(DurationMs, 30_000);
        Title = $"Automation · Q{CuePresentation.Number(cue.Number)} · {TargetName(cue, track)}";
        Reload();
    }

    public string Title { get; }
    public long DurationMs { get; private set; }
    public bool CanExtend { get; }
    public string DurationLabel => (CanExtend ? "open · " : "")
                                   + ClipTimes.Format((int)Math.Min(int.MaxValue, DurationMs));
    public string Unit => _descriptor.Value.Unit;
    public string Hint =>
        $"{DurationLabel} · absolute cue time · double-click to add · drag keys; scroll or zoom for long media";

    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _points = [];

    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _shape = [];

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
    private Guid? _primaryKeyId;

    public bool HasSelection => PrimaryKeyId is not null;
    public int SelectionCount => _selection.Count;
    public bool HasMultipleSelected => _selection.Count > 1;

    public IReadOnlyList<string> Segments { get; } = ["linear", "equal power", "exponential", "S-curve"];

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
            if (SelectedKey() is not { } key)
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
            if (SelectedKey() is { } key && key.Hold != value)
                ReplaceKey(key.Id, current => current with { Hold = value }, "toggle automation hold");
        }
    }

    public bool IsEnabled
    {
        get => _track?.Enabled ?? true;
        set
        {
            if (_track is not { } track || _journal is null || track.Enabled == value)
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

        var timeMs = Math.Clamp(
            (long)Math.Round(ViewStartMs + (Math.Clamp(gesture.X, 0, 1) * ViewLengthMs)), 0, DurationMs);
        var value = ValueAtCanvasY(gesture.Y);
        var keys = _track.Keyframes.Select(Clone).ToList();

        switch (gesture.Kind)
        {
            case CurveGestureKind.Add when keys.All(key => Math.Abs(key.TimeMs - timeMs) > 4):
                var added = new AutomationKeyframe { TimeMs = timeMs, Value = value };
                keys.Add(added);
                _selection.Clear();
                _selection.Add(added.Id);
                PrimaryKeyId = added.Id;
                break;
            case CurveGestureKind.Move when GestureKey(gesture.Index) is { } moved:
                if (!_selection.Contains(moved.Id))
                    SelectOnly(moved);
                var original = keys.First(key => key.Id == moved.Id);
                var deltaTime = timeMs - original.TimeMs;
                var deltaValue = value - original.Value;
                foreach (var key in keys.Where(key => _selection.Contains(key.Id)))
                {
                    key.TimeMs = Math.Clamp(key.TimeMs + deltaTime, 0, DurationMs);
                    key.Value = _descriptor.Value.Clamp(key.Value + deltaValue);
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

        _drag ??= _journal.Composite("edit automation keyframes", "cues", quiet: true);
        Write(keys, "edit automation keyframes");
        Reload();
    }

    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
        _gestureKeyId = null;
        _journal?.CloseGroup();
    }

    public void AddKeyAtCursor()
    {
        if (_track is null || _journal is null)
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

        var ordered = _track.Keyframes.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).ToList();
        var selected = ordered.Select((key, index) => (key, index))
            .Where(pair => _selection.Contains(pair.key.Id))
            .Select(pair => pair.index)
            .ToHashSet();
        Problem = $"copied {selected.Count} keyframe(s)";
        return LaneKeyframeClipboard.Encode([.. ordered.Select(ToClipboardKnot)], selected);
    }

    public bool Paste(string? text)
    {
        if (_track is null || _journal is null
            || LaneKeyframeClipboard.DecodeKnots(text) is not { Count: > 0 } decoded)
        {
            Problem = "clipboard has no HaCue2 keyframes";
            return false;
        }

        var span = decoded[^1].X - decoded[0].X;
        var at = Math.Clamp(CursorMs / DurationMs, 0, Math.Max(0, 1 - span));
        var placed = decoded.Select(knot => knot with { X = at + knot.X }).ToList();
        var fromMs = (long)Math.Round(placed[0].X * DurationMs);
        var toMs = (long)Math.Round(placed[^1].X * DurationMs);
        var pasted = placed.Select(FromClipboardKnot).ToList();
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
        if (SelectedKey() is not { } key || ClipTimes.Parse(text, TimeSpan.FromMilliseconds(DurationMs)) is not { } time)
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
        if (SelectedKey() is not { } key
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
        var targetCueId = _track?.Target.CueId ?? _cue.Id;
        Problem = await _host.SeekCueAsync(targetCueId, TimeSpan.FromMilliseconds(CursorMs)).ConfigureAwait(true) ?? "";
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
            ? _track?.Keyframes.FirstOrDefault(candidate => candidate.Id == id)
            : KeyAtVisibleIndex(index);
        _gestureKeyId ??= key?.Id;
        return key;
    }

    private AutomationKeyframe? SelectedKey() =>
        _track?.Keyframes.FirstOrDefault(key => key.Id == PrimaryKeyId);

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

    private CurveKnot ToClipboardKnot(AutomationKeyframe key)
    {
        var range = _descriptor.Value.Maximum - _descriptor.Value.Minimum;
        return new CurveKnot(
            Math.Clamp((double)key.TimeMs / DurationMs, 0, 1),
            range <= 0 ? 0 : Math.Clamp((key.Value - _descriptor.Value.Minimum) / range, 0, 1),
            key.Hold,
            key.Curve.Law);
    }

    private AutomationKeyframe FromClipboardKnot(CurveKnot knot) => new()
    {
        TimeMs = Math.Clamp((long)Math.Round(knot.X * DurationMs), 0, DurationMs),
        Value = _descriptor.Value.Clamp(
            _descriptor.Value.Minimum
            + ((_descriptor.Value.Maximum - _descriptor.Value.Minimum) * knot.Y)),
        Hold = knot.Hold,
        Curve = new CurveSpec { Law = knot.CurveToNext },
    };

    private void ReplaceKey(Guid id, Func<AutomationKeyframe, AutomationKeyframe> replace, string description)
    {
        if (_track is null)
            return;
        var keys = _track.Keyframes.Select(key => key.Id == id ? replace(Clone(key)) : Clone(key)).ToList();
        Write(keys, description);
        _journal?.CloseGroup();
        Problem = "";
        Reload();
    }

    private void Write(IEnumerable<AutomationKeyframe> keys, string description)
    {
        if (_track is null || _journal is null || _cue is null)
            return;
        var next = keys.OrderBy(key => key.TimeMs).ThenBy(key => key.Id).Select(Clone).ToList();
        _journal.Do(new SetValueCommand<List<AutomationKeyframe>>(
            _cue.Id, $"automation:{_track.Id}", "cues",
            () => _track.Keyframes.Select(Clone).ToList(),
            value => _track.Keyframes = value.Select(Clone).ToList(),
            next,
            description));
    }

    private void Reload()
    {
        if (_track is null)
        {
            Points = [];
            Shape = [];
            return;
        }

        var end = ViewStartMs + ViewLengthMs;
        _visibleKeys = _track.Keyframes
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
                    _track, _journal?.Project ?? new HaCueProject(), time, _descriptor.Value.Default);
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
