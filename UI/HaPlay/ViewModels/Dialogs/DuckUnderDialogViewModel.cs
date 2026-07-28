using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;
using HaPlay.Views.Controls;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>One overlapping media lane the duck can apply to (checked = duck under it).</summary>
public sealed partial class DuckLaneChoiceViewModel : ObservableObject
{
    public DuckLaneChoiceViewModel(CueNodeViewModel node) => Node = node;

    public CueNodeViewModel Node { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    public string Display
    {
        get
        {
            var label = $"{Node.Number} {Node.Label}".Trim();
            return label.Length > 0 ? label : Node.KindLabel;
        }
    }
}

/// <summary>
/// VM for the timeline editor's "Duck under…" dialog (timeline doc Phase D): pick which overlapping
/// media lanes duck the bed and with what depth/ramp/lead/curve, then <see cref="Apply"/> writes the
/// dip keyframes into the bed's <see cref="CueNodeViewModel.VolumeEnvelope"/> (undo/persistence ride
/// the ordinary envelope path - this is pure authoring, no runtime concept of ducking exists). The
/// candidate list is fixed at open time (the <see cref="RenameCueDialogViewModel"/> copy-then-commit
/// pattern); all the interval/splice math lives in <see cref="TimelineDuckMath"/>.
/// </summary>
public sealed partial class DuckUnderDialogViewModel : ViewModelBase
{
    public const double DefaultDepthDb = -12;
    public const int DefaultRampMs = 300;
    public const int DefaultLeadMs = 0;

    private DuckUnderDialogViewModel(CueNodeViewModel bed, IReadOnlyList<DuckLaneChoiceViewModel> lanes)
    {
        Bed = bed;
        Lanes = lanes;
        foreach (var lane in lanes)
            lane.PropertyChanged += OnLaneChoiceChanged;
    }

    /// <summary>The media block whose envelope receives the dip.</summary>
    public CueNodeViewModel Bed { get; }

    /// <summary>The OTHER media lanes overlapping the bed on the timeline - default all selected.</summary>
    public IReadOnlyList<DuckLaneChoiceViewModel> Lanes { get; }

    public string DialogTitle => Strings.Format(
        nameof(Strings.DuckDialogTitleFormat),
        string.IsNullOrWhiteSpace($"{Bed.Number} {Bed.Label}".Trim())
            ? Bed.KindLabel
            : $"{Bed.Number} {Bed.Label}".Trim());

    public bool HasOverlappingLanes => Lanes.Count > 0;

    public bool CanApply => Lanes.Any(l => l.IsSelected);

    /// <summary>How far the bed dips BELOW its own surrounding envelope level (relative, in dB).</summary>
    [ObservableProperty]
    private decimal? _depthDb = (decimal)DefaultDepthDb;

    [ObservableProperty]
    private decimal? _rampMs = DefaultRampMs;

    [ObservableProperty]
    private decimal? _leadMs = DefaultLeadMs;

    public IReadOnlyList<CueFadeCurve> CurveChoices { get; } = Enum.GetValues<CueFadeCurve>();

    [ObservableProperty]
    private CueFadeCurve _curve = CueFadeCurve.EqualPower;

    /// <summary>Build the dialog for one bed: candidates are the group's OTHER media lanes whose
    /// audible block (start incl. pre-wait) overlaps the bed's trimmed span on the timeline.</summary>
    public static DuckUnderDialogViewModel For(CueNodeViewModel bed, IEnumerable<CueNodeViewModel> laneNodes)
    {
        var bedStart = TimelineMath.BlockStartMs(bed);
        var bedInterval = new TimelineIntervalMs(bedStart, bedStart + Math.Max(0, bed.EffectiveDurationMs));
        var lanes = laneNodes
            .Where(node => !ReferenceEquals(node, bed) && node.Kind == CueNodeKind.Media)
            .Where(node => TimelineDuckMath.Overlaps(bedInterval, TimelineDuckMath.BlockIntervalMs(node)))
            .Select(node => new DuckLaneChoiceViewModel(node))
            .ToList();
        return new DuckUnderDialogViewModel(bed, lanes);
    }

    /// <summary>Write the dip(s) into the bed's envelope. Returns false when nothing changed (no
    /// lanes selected / no overlap after trims). Null numeric fields fall back to the defaults.</summary>
    public bool Apply()
    {
        var voices = Lanes.Where(l => l.IsSelected)
            .Select(l => TimelineDuckMath.BlockIntervalMs(l.Node))
            .ToList();
        if (voices.Count == 0)
            return false;

        var updated = TimelineDuckMath.ApplyDucks(
            Bed.VolumeEnvelope,
            TimelineMath.BlockStartMs(Bed),
            Bed.EffectiveDurationMs,
            voices,
            (double)(DepthDb ?? (decimal)DefaultDepthDb),
            (int)(RampMs ?? DefaultRampMs),
            (int)(LeadMs ?? DefaultLeadMs),
            Curve);
        if (ReferenceEquals(updated, Bed.VolumeEnvelope))
            return false;

        Bed.VolumeEnvelope = updated;
        return true;
    }

    private void OnLaneChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DuckLaneChoiceViewModel.IsSelected))
            OnPropertyChanged(nameof(CanApply));
    }
}
