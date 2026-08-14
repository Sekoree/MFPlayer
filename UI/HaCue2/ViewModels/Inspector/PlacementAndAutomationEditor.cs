using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Compile;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.Session;
using S.Media.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// The Video pane AND the automation lanes, as one editor (review F-11): placements, geometry,
/// crop, the layer-effect rack, the placement drag with its live-edit publisher, placement
/// management, and every automation-lane projection. One editor rather than two because they are
/// one feature - the lane surface derives from the selected placement and the effect rack, and a
/// video field's change re-announces automation projections. Writes only through the journal.
/// </summary>
public sealed partial class PlacementAndAutomationEditor(
    ProjectJournal journal, IInspectorEditorContext context) : ObservableObject
{
    private CueNode? Cue => context.Cue;
    private HaCueProject Project => context.Project;
    private ShowHost? Host => context.Host;
    private MediaFacts? Facts => context.LeadFacts;
    private Func<MediaCueNode, MediaFacts?>? MediaFacts => context.MediaFactsFor;
    private TimeSpan? ClipDuration => context.LeadClipDuration;
    private string? ClipPath(MediaCueNode media) => context.ClipPathFor(media);
    private string CacheRoot => context.CacheRoot;
    private long? WaveformCacheBytes => context.WaveformCacheBytes;

    /// <summary>Open for the duration of one canvas drag, so the gesture is a single undo step.</summary>
    private IDisposable? _drag;
    private readonly object _livePlacementGate = new();
    private readonly Dictionary<LivePlacementKey, LivePlacementEdit> _pendingLivePlacements = [];
    private bool _livePlacementPublisherRunning;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(PlacementGuidesX));
        OnPropertyChanged(nameof(PlacementGuidesY));
        OnPropertyChanged(nameof(PlacementAspect));
        OnPropertyChanged(nameof(HasPlacement));
        OnPropertyChanged(nameof(PlacementList));
        OnPropertyChanged(nameof(PlacementHeaders));
        OnPropertyChanged(nameof(PlacementCompositions));
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacity));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
        OnPropertyChanged(nameof(PlacementRotation));
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        OnPropertyChanged(nameof(EffectRackRows));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(HasColorAdjust));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
        OnPropertyChanged(nameof(EffectLanes));
        OnPropertyChanged(nameof(HasEffectLanes));
        OnPropertyChanged(nameof(CanCarryLanes));
        OnPropertyChanged(nameof(CanAddVolumeLane));
        OnPropertyChanged(nameof(CanAddOpacityLane));
        OnPropertyChanged(nameof(CanAddPlacementXLane));
        OnPropertyChanged(nameof(CanAddPlacementYLane));
        OnPropertyChanged(nameof(CanAddPlacementWidthLane));
        OnPropertyChanged(nameof(CanAddPlacementHeightLane));
        OnPropertyChanged(nameof(CanAddPlacementRotationLane));
        OnPropertyChanged(nameof(CanAddChromaSimilarityLane));
        OnPropertyChanged(nameof(CanAddChromaSmoothnessLane));
        OnPropertyChanged(nameof(CanAddChromaSpillLane));
        OnPropertyChanged(nameof(CanAddColorBrightnessLane));
        OnPropertyChanged(nameof(CanAddColorContrastLane));
        OnPropertyChanged(nameof(HasAutomationChromaKey));
        OnPropertyChanged(nameof(HasAutomationColorAdjust));
        OnPropertyChanged(nameof(HasAutomationAudioGain));
        OnPropertyChanged(nameof(CanAddOutboundLane));
        OnPropertyChanged(nameof(VolumeLaneLabel));
        OnPropertyChanged(nameof(OpacityLaneLabel));
        OnPropertyChanged(nameof(IsAutomationCue));
        OnPropertyChanged(nameof(AutomationTargetCues));
        OnPropertyChanged(nameof(AutomationTargetPlacements));
        OnPropertyChanged(nameof(HasMultipleAutomationTargetPlacements));
        OnPropertyChanged(nameof(AutomationDuration));
        OnPropertyChanged(nameof(AutomationRestoreBase));
    }

    public IReadOnlyList<PlacementBox> Placements
    {
        get
        {
            // The canvas the SELECTED placement is on - a cue can be on several at once, and the
            // preview shows the one being edited.
            if (Placement is not { } placement)
                return [];

            var composition = Project.Compositions
                .FirstOrDefault(item => item.Id == placement.CompositionId);

            return composition is null
                ? []
                : VideoPresentation.Layers(Project, composition, Cue?.Id, MediaFacts);
        }
    }

    /// <summary>
    /// The seams between the screens showing this canvas, as snap targets for the placement drag.
    /// </summary>
    /// <remarks>
    /// What makes dividing a composition between projectors worth more than a picture: a cue can be
    /// dropped exactly onto ONE screen of a wall without anybody working out what fraction that is.
    /// Scoped to the composition the SELECTED placement is on, which is the same canvas
    /// <see cref="Placements"/> draws - a cue can be on several at once, and the guides of a canvas it
    /// is not being dragged on would snap it to seams that are not there.
    /// </remarks>
    public IReadOnlyList<double> PlacementGuidesX =>
        Placement is { } placement
            ? VideoPresentation.SliceGuides(Project, placement.CompositionId, horizontal: true)
            : [];

    public IReadOnlyList<double> PlacementGuidesY =>
        Placement is { } placement
            ? VideoPresentation.SliceGuides(Project, placement.CompositionId, horizontal: false)
            : [];

    /// <summary>The selected placement's real composition shape.</summary>
    public double PlacementAspect =>
        Placement is { } placement
        && Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId) is { Height: > 0 } composition
            ? composition.Width / (double)composition.Height
            : 16d / 9d;

    /// <summary>Editor preferences, shared by this inspector's placement canvases and not project data.</summary>
    [ObservableProperty]
    private bool _preservePlacementAspect = true;

    [ObservableProperty]
    private bool _snapPlacement = true;

    /// <summary>Every canvas the cue appears on.</summary>
    public IReadOnlyList<LayerPlacement> PlacementList =>
        Cue is null ? [] : CuePlacements.Of(Cue);

    /// <summary>Which one the fields edit. A cue can be on several canvases at once.</summary>
    /// <remarks>
    /// Both the boxes and the guides follow it, because switching placement can switch COMPOSITION -
    /// and a canvas drawn from one composition with the seams of another is worse than no seams: it
    /// offers snap targets that do not exist on the screen the cue is actually going to.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placements))]
    [NotifyPropertyChangedFor(nameof(PlacementGuidesX))]
    [NotifyPropertyChangedFor(nameof(PlacementGuidesY))]
    [NotifyPropertyChangedFor(nameof(PlacementAspect))]
    [NotifyPropertyChangedFor(nameof(PlacementHeaders))]
    private int _selectedPlacement;

    partial void OnSelectedPlacementChanged(int value)
    {
        // Every field projects the selected list entry. An expander changes that entry without
        // changing the cue, so a normal document Reload is neither necessary nor desirable (it would
        // also reset the selected tab); announce the projection properties directly.
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(PlacementAspect));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacity));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
        OnPropertyChanged(nameof(PlacementRotation));
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(HasColorAdjust));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
        OnPropertyChanged(nameof(CanAddOpacityLane));
        OnPropertyChanged(nameof(CanAddPlacementXLane));
        OnPropertyChanged(nameof(CanAddPlacementYLane));
        OnPropertyChanged(nameof(CanAddPlacementWidthLane));
        OnPropertyChanged(nameof(CanAddPlacementHeightLane));
        OnPropertyChanged(nameof(CanAddPlacementRotationLane));
        OnPropertyChanged(nameof(CanAddChromaSimilarityLane));
        OnPropertyChanged(nameof(CanAddChromaSmoothnessLane));
        OnPropertyChanged(nameof(CanAddChromaSpillLane));
        OnPropertyChanged(nameof(CanAddColorBrightnessLane));
        OnPropertyChanged(nameof(CanAddColorContrastLane));
        OnPropertyChanged(nameof(HasAutomationChromaKey));
        OnPropertyChanged(nameof(HasAutomationColorAdjust));
    }

    private LayerPlacement? Placement =>
        SelectedPlacement >= 0 && SelectedPlacement < PlacementList.Count
            ? PlacementList[SelectedPlacement]
            : PlacementList.FirstOrDefault();

    /// <summary>
    /// Whether this cue is on a canvas at all.
    /// </summary>
    /// <remarks>
    /// A media cue always OFFERS the Video tab, because putting one on a canvas is a thing you do to
    /// an existing cue. Without this the pane draws an empty canvas and four fields describing a
    /// placement that does not exist, which reads exactly like a layer sized to nothing.
    /// </remarks>
    public bool HasPlacement => Placement is not null;

    public IReadOnlyList<string> PlacementCompositions =>
        [.. Project.Compositions.Select(composition =>
            $"{composition.Name} · {composition.Width}×{composition.Height}")];

    public int PlacementCompositionIndex
    {
        get => Placement is { } placement
            ? Project.Compositions.FindIndex(composition => composition.Id == placement.CompositionId)
            : -1;
        set
        {
            if (Placement is not { } placement || value < 0 || value >= Project.Compositions.Count)
                return;

            var composition = Project.Compositions[value];
            if (composition.Id == placement.CompositionId)
                return;

            Edit("composition", "video",
                () => placement.CompositionId, id => placement.CompositionId = id, composition.Id,
                $"move to {composition.Name}");
        }
    }

    public string LayerValue
    {
        get => Placement is { } placement ? placement.LayerIndex.ToString(CultureInfo.CurrentCulture) : "-";
        set
        {
            if (Placement is not { } placement
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var layer))
                return;

            Edit("layer", "video",
                () => placement.LayerIndex, index => placement.LayerIndex = Math.Max(0, index), layer,
                "set layer");
        }
    }

    /// <summary>The six the compositor can do. It offered three, so half of them were unreachable.</summary>
    public IReadOnlyList<string> FitModes { get; } =
        ["contain", "cover", "stretch", "center", "fill width", "fill height"];

    public int FitIndex
    {
        get => Placement is { } placement ? (int)placement.Fit : -1;
        set
        {
            if (Placement is not { } placement || value < 0 || (LayerFit)value == placement.Fit)
                return;

            Edit("fit", "video",
                () => placement.Fit, fit => placement.Fit = fit, (LayerFit)value,
                $"set fit {FitModes[value]}");
            ApplyLivePlacement(Cue, placement);
        }
    }

    public double PlacementOpacity
    {
        get => Placement?.Opacity ?? 1;
        set => Write("opacity", 0, 1,
            placement => placement.Opacity, (placement, number) => placement.Opacity = number, value,
            "set layer opacity");
    }

    // ── the destination rectangle, as numbers ─────────────────────────────────────────────────
    // The same four a drag moves. A canvas cannot hit exactly half, and half is what a split screen
    // is made of.

    public double PlacementX
    {
        get => Placement?.X ?? 0;
        set => Write("destX", -1, 2,
            placement => placement.X, (placement, number) => placement.X = number, value, "move layer");
    }

    public double PlacementY
    {
        get => Placement?.Y ?? 0;
        set => Write("destY", -1, 2,
            placement => placement.Y, (placement, number) => placement.Y = number, value, "move layer");
    }

    public double PlacementWidth
    {
        get => Placement?.Width ?? 1;
        set => Write("destWidth", 0.001, 2,
            placement => placement.Width, (placement, number) => placement.Width = number, value,
            "resize layer");
    }

    public double PlacementHeight
    {
        get => Placement?.Height ?? 1;
        set => Write("destHeight", 0.001, 2,
            placement => placement.Height, (placement, number) => placement.Height = number, value,
            "resize layer");
    }

    public double PlacementRotation
    {
        get => Placement?.RotationDegrees ?? 0;
        set => Write("rotation", -360, 360,
            placement => placement.RotationDegrees,
            (placement, number) => placement.RotationDegrees = number, value, "rotate layer");
    }

    // ── the source crop ───────────────────────────────────────────────────────────────────────
    // Which part of the PICTURE to use, which is a different question from where to put it. Baked-in
    // letterbox bars come off with this; shrinking the destination instead would move the picture too.

    public double CropLeft
    {
        get => Placement?.CropLeft ?? 0;
        set => Write("cropLeft", 0, 0.49,
            placement => placement.CropLeft, (placement, number) => placement.CropLeft = number, value,
            "crop layer");
    }

    public double CropTop
    {
        get => Placement?.CropTop ?? 0;
        set => Write("cropTop", 0, 0.49,
            placement => placement.CropTop, (placement, number) => placement.CropTop = number, value,
            "crop layer");
    }

    public double CropRight
    {
        get => Placement?.CropRight ?? 0;
        set => Write("cropRight", 0, 0.49,
            placement => placement.CropRight, (placement, number) => placement.CropRight = number, value,
            "crop layer");
    }

    public double CropBottom
    {
        get => Placement?.CropBottom ?? 0;
        set => Write("cropBottom", 0, 0.49,
            placement => placement.CropBottom, (placement, number) => placement.CropBottom = number,
            value, "crop layer");
    }

    /// <summary>Writes one placement number through the journal, clamped to what it can mean.</summary>
    private void Write(
        string property,
        double minimum,
        double maximum,
        Func<LayerPlacement, double> read,
        Action<LayerPlacement, double> write,
        double value,
        string description)
    {
        if (Placement is not { } placement || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);

        if (Math.Abs(read(placement) - clamped) < 0.000001)
            return;

        Edit(property, "video",
            () => read(placement), number => write(placement, number), clamped, description);
        ApplyLivePlacement(Cue, placement);
    }

    /// <summary>
    /// Quick destination layouts: the rectangles a split screen is actually made of.
    /// </summary>
    /// <remarks>
    /// Exact by construction. Half is 0.5 and a quadrant is 0.25, and neither is reachable by dragging
    /// - which is why HaPlay grew the same set of buttons and why they are the fastest correct route to
    /// a two-up or a four-up.
    /// </remarks>
    public void ApplyLayout(string preset)
    {
        if (Placement is not { } placement || Cue is null)
            return;

        var (x, y, width, height) = preset switch
        {
            "full" => (0d, 0d, 1d, 1d),
            "left" => (0d, 0d, 0.5d, 1d),
            "right" => (0.5d, 0d, 0.5d, 1d),
            "top" => (0d, 0d, 1d, 0.5d),
            "bottom" => (0d, 0.5d, 1d, 0.5d),
            "tl" => (0d, 0d, 0.5d, 0.5d),
            "tr" => (0.5d, 0d, 0.5d, 0.5d),
            "bl" => (0d, 0.5d, 0.5d, 0.5d),
            "br" => (0.5d, 0.5d, 0.5d, 0.5d),
            _ => (double.NaN, 0d, 0d, 0d),
        };

        if (double.IsNaN(x))
            return;

        // ONE undo step for the whole rectangle: a layout half-applied is a rectangle nobody chose.
        using (journal.Composite($"layout: {preset}", "video"))
        {
            journal.Do(RectEdits.Placement(Cue, placement, new NormalizedRect(x, y, width, height)));
        }

        journal.CloseGroup();
        ApplyLivePlacement(Cue, placement);
        context.Reload();
    }

    /// <summary>Quick source crops, including the one that takes them all off again.</summary>
    public void ApplyCrop(string preset)
    {
        if (Placement is not { } placement)
            return;

        var (left, top, right, bottom) = preset switch
        {
            "none" => (0d, 0d, 0d, 0d),
            "wide" => (0.25d, 0d, 0.25d, 0d),
            "tall" => (0d, 0.25d, 0d, 0.25d),
            "centre" => (0.25d, 0.25d, 0.25d, 0.25d),
            _ => (double.NaN, 0d, 0d, 0d),
        };

        if (double.IsNaN(left))
            return;

        using (journal.Composite($"crop: {preset}", "video"))
        {
            journal.Do(new SetValueCommand<double>(
                Cue!.Id, "cropLeft", "video",
                () => placement.CropLeft, n => placement.CropLeft = n, left, "crop"));
            journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropTop", "video",
                () => placement.CropTop, n => placement.CropTop = n, top, "crop"));
            journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropRight", "video",
                () => placement.CropRight, n => placement.CropRight = n, right, "crop"));
            journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropBottom", "video",
                () => placement.CropBottom, n => placement.CropBottom = n, bottom, "crop"));
        }

        journal.CloseGroup();
        ApplyLivePlacement(Cue, placement);
        context.Reload();
    }

    // ── ordered layer-effect rack ─────────────────────────────────────────────────────────────
    // The built-in focused editors below are projections of the same generic rack shown above them.

    private const string ChromaEffectType = S.Media.Compositor.Effects.ChromaKeyVideoEffect.EffectId;
    private const string ColourEffectType =
        S.Media.Compositor.Effects.BrightnessContrastVideoEffect.EffectId;

    private LayerEffectInstance? ChromaEffect => LayerEffectRack.Find(Placement, ChromaEffectType);
    private LayerEffectInstance? ColourEffect => LayerEffectRack.Find(Placement, ColourEffectType);

    public IReadOnlyList<LayerEffectRackRow> EffectRackRows => Placement is not { } placement
        ? []
        :
        [
            .. placement.Effects.Select((effect, index) => new LayerEffectRackRow(
                effect.Id,
                index + 1,
                LayerEffectCatalog.Get(effect.EffectTypeId)?.DisplayName ?? effect.EffectTypeId,
                effect.Enabled,
                index > 0,
                index + 1 < placement.Effects.Count)),
        ];

    public bool HasChromaKey => ChromaEffect is not null;

    public bool ChromaKeyOn
    {
        get => ChromaEffect is { Enabled: true };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (journal.Composite(value ? "chroma key on" : "chroma key off", "video"))
            {
                if (value && ChromaEffect is null)
                    journal.Do(new AddItemCommand<LayerEffectInstance>(
                        placement.Effects,
                        LayerEffectCatalog.Create(ChromaEffectType),
                        placement.Effects.Count,
                        "video",
                        "add chroma key"));

                if (ChromaEffect is { } effect && effect.Enabled != value)
                    journal.Do(new SetValueCommand<bool>(
                        Cue.Id, $"effect:{effect.Id}:enabled", "video",
                        () => effect.Enabled, flag => effect.Enabled = flag, value,
                        value ? "chroma key on" : "chroma key off"));
            }

            journal.CloseGroup();
            context.Reload();
        }
    }

    public double ChromaSimilarity
    {
        get => ChromaEffect?.Read("similarity", 0.4) ?? 0.4;
        set => WriteEffectParameter(ChromaEffect, "similarity", 0, 1, value, "adjust key");
    }

    public double ChromaSmoothness
    {
        get => ChromaEffect?.Read("smoothness", 0.1) ?? 0.1;
        set => WriteEffectParameter(ChromaEffect, "smoothness", 0, 1, value, "adjust key");
    }

    public double ChromaSpill
    {
        get => ChromaEffect?.Read("spill", 0.1) ?? 0.1;
        set => WriteEffectParameter(ChromaEffect, "spill", 0, 1, value, "adjust key");
    }

    /// <summary>The key colour as "#RRGGBB" - what a designer is given on a call sheet.</summary>
    public string ChromaColour
    {
        get => ChromaEffect is { } key
            ? $"#{Channel(key.Read("keyR", 0))}{Channel(key.Read("keyG", 1))}{Channel(key.Read("keyB", 0))}"
            : "#00FF00";
        set
        {
            if (ChromaEffect is not { } key || Cue is null || !TryColour(value, out var rgb))
                return;

            using (journal.Composite("chroma key colour", "video"))
            {
                WriteEffectParameterCommand(key, "keyR", rgb.Red, "key colour");
                WriteEffectParameterCommand(key, "keyG", rgb.Green, "key colour");
                WriteEffectParameterCommand(key, "keyB", rgb.Blue, "key colour");
            }

            journal.CloseGroup();
            context.Reload();
        }
    }

    private static string Channel(double value) =>
        ((int)Math.Round(Math.Clamp(value, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture);

    private static bool TryColour(string text, out (double Red, double Green, double Blue) rgb)
    {
        rgb = default;
        var hex = (text ?? "").Trim().TrimStart('#');

        if (hex.Length != 6
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;

        rgb = (((packed >> 16) & 0xFF) / 255d, ((packed >> 8) & 0xFF) / 255d, (packed & 0xFF) / 255d);
        return true;
    }

    public bool HasColorAdjust => ColourEffect is not null;

    public bool ColorAdjustOn
    {
        get => ColourEffect is { Enabled: true };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (journal.Composite(value ? "colour adjust on" : "colour adjust off", "video"))
            {
                if (value && ColourEffect is null)
                    journal.Do(new AddItemCommand<LayerEffectInstance>(
                        placement.Effects,
                        LayerEffectCatalog.Create(ColourEffectType),
                        placement.Effects.Count,
                        "video",
                        "add colour adjust"));

                if (ColourEffect is { } effect && effect.Enabled != value)
                    journal.Do(new SetValueCommand<bool>(
                        Cue.Id, $"effect:{effect.Id}:enabled", "video",
                        () => effect.Enabled, flag => effect.Enabled = flag,
                        value, value ? "colour adjust on" : "colour adjust off"));
            }

            journal.CloseGroup();
            context.Reload();
        }
    }

    public double LayerBrightness
    {
        get => ColourEffect?.Read("brightness", 0) ?? 0;
        set => WriteEffectParameter(ColourEffect, "brightness", -1, 1, value, "adjust layer colour");
    }

    public double LayerContrast
    {
        get => ColourEffect?.Read("contrast", 1) ?? 1;
        set => WriteEffectParameter(ColourEffect, "contrast", 0, 4, value, "adjust layer colour");
    }

    private void WriteEffectParameter(
        LayerEffectInstance? effect,
        string parameterId,
        double minimum,
        double maximum,
        double value,
        string description)
    {
        if (effect is null || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);
        var parameter = effect.Parameters.FirstOrDefault(candidate => candidate.ParameterId == parameterId);
        if (parameter is null || Math.Abs(parameter.Value - clamped) < 0.000001)
            return;
        Edit($"effect:{effect.Id}:{parameterId}", "video",
            () => parameter.Value, number => parameter.Value = number, clamped, description);
    }

    private void WriteEffectParameterCommand(
        LayerEffectInstance effect, string parameterId, double value, string description)
    {
        var parameter = effect.Parameters.First(candidate => candidate.ParameterId == parameterId);
        journal.Do(new SetValueCommand<double>(
            Cue!.Id, $"effect:{effect.Id}:{parameterId}", "video",
            () => parameter.Value, number => parameter.Value = number, value, description));
    }

    public void AddLayerEffect(string typeId)
    {
        if (Placement is not { } placement || Cue is null || LayerEffectCatalog.Get(typeId) is not { } definition)
            return;
        journal.Do(new AddItemCommand<LayerEffectInstance>(
            placement.Effects,
            LayerEffectCatalog.Create(typeId),
            placement.Effects.Count,
            "video",
            $"add {definition.DisplayName}"));
        journal.CloseGroup();
        context.Reload();
    }

    public void ToggleLayerEffect(Guid effectId)
    {
        if (Placement?.Effects.FirstOrDefault(effect => effect.Id == effectId) is not { } effect || Cue is null)
            return;
        journal.Do(new SetValueCommand<bool>(
            Cue.Id, $"effect:{effect.Id}:enabled", "video",
            () => effect.Enabled, value => effect.Enabled = value, !effect.Enabled,
            effect.Enabled ? "bypass effect" : "enable effect"));
        journal.CloseGroup();
        context.Reload();
    }

    public void MoveLayerEffect(Guid effectId, int direction)
    {
        if (Placement is not { } placement || direction is not (-1 or 1))
            return;
        var index = placement.Effects.FindIndex(effect => effect.Id == effectId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= placement.Effects.Count)
            return;
        var effect = placement.Effects[index];
        using (journal.Composite("reorder layer effects", "video"))
        {
            journal.Do(new RemoveItemCommand<LayerEffectInstance>(
                placement.Effects, effect, "video", "move layer effect"));
            journal.Do(new AddItemCommand<LayerEffectInstance>(
                placement.Effects, effect, target, "video", "move layer effect"));
        }
        journal.CloseGroup();
        context.Reload();
    }

    public void RemoveLayerEffect(Guid effectId)
    {
        if (Placement is not { } placement
            || placement.Effects.FirstOrDefault(effect => effect.Id == effectId) is not { } effect)
            return;
        using (journal.Composite("remove layer effect", "video"))
        {
            foreach (var owner in Project.AllCues())
            {
                if (CueAutomation.ListOf(owner) is not { } tracks)
                    continue;
                foreach (var track in tracks.Where(track => track.Target.ObjectId == effectId).ToArray())
                    journal.Do(new RemoveItemCommand<AutomationTrack>(
                        tracks, track, "cues", "remove effect automation"));
            }
            journal.Do(new RemoveItemCommand<LayerEffectInstance>(
                placement.Effects, effect, "video", "remove layer effect"));
        }
        journal.CloseGroup();
        context.Reload();
    }

    private void Edit<T>(
        string property, string domain, Func<T> read, Action<T> write, T value, string description)
    {
        if (Cue is not { } cue)
            return;

        journal.Do(new SetValueCommand<T>(cue.Id, property, domain, read, write, value, description));
        journal.CloseGroup();
        context.Reload();
    }

    /// <summary>
    /// A drag on the inspector's placement preview.
    /// </summary>
    /// <remarks>
    /// The preview shows the WHOLE composition, not just the selected cue, so a drag here can move any
    /// layer on it - which is the point of showing the neighbours at all. The command is the same one
    /// the Video view builds, keyed to the cue, so the two canvases share one undo step rather than
    /// producing two that disagree.
    /// </remarks>
    public void ApplyPlacementGesture(PlacementGesture gesture)
    {
        if (Project.FindCue(gesture.SubjectId) is not { } cue)
            return;

        var compositionId = Placement?.CompositionId;
        if (CuePlacements.Of(cue).FirstOrDefault(item => item.LayerIndex == gesture.Layer
                                                  && item.CompositionId == compositionId)
            is not { } placement)
            return;

        // The view-model raises the placement properties below, so journal observers do not need to
        // rebuild the entire shell for every pointer pixel. They see one finished edit on release.
        _drag ??= journal.Composite("move layer", "video", quiet: true);
        journal.Do(RectEdits.Placement(cue, placement, gesture.AuthoredRect()));
        ApplyLivePlacement(cue, placement);

        // These are the only values a move/resize changes. Keeping the hot pointer path focused is
        // important on the compact inspector canvas: rebuilding every combo, crop field, effect lane
        // and guide collection for each native motion event can make the backend coalesce a whole drag
        // into its first pixel. The complete inspector refresh still happens once when the quiet undo
        // scope closes on release.
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
    }

    /// <summary>Closes the drag's undo step. Called when the pointer is released or a nudge lands.</summary>
    public void EndPlacementGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    private async Task PublishLivePlacementsAsync()
    {
        while (true)
        {
            LivePlacementEdit edit;
            lock (_livePlacementGate)
            {
                if (_pendingLivePlacements.Count == 0)
                {
                    _livePlacementPublisherRunning = false;
                    return;
                }

                edit = _pendingLivePlacements.Values.First();
                _pendingLivePlacements.Remove(edit.Key);
            }

            try
            {
                await edit.Key.Host.UpdateActivePlacementAsync(
                        edit.Key.CueId,
                        edit.Key.CompositionId,
                        edit.Key.LayerIndex,
                        edit.Placement)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A pointer release can race application/engine shutdown; the persisted edit stands.
            }
            catch (OperationCanceledException)
            {
                // Likewise during shutdown/reload. A later engine sync reads the document value.
            }
            catch (Exception exception)
            {
                // A hot update is best-effort; losing it must not strand the publisher or the authored
                // gesture. The normal release-time project sync remains the recovery path.
                System.Diagnostics.Trace.TraceWarning(
                    $"Live placement update failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private void ApplyLivePlacement(CueNode? cue, LayerPlacement placement)
    {
        if (cue is null || Host is not { } host)
            return;

        var key = new LivePlacementKey(
            host, cue.Id, placement.CompositionId, placement.LayerIndex);
        var edit = new LivePlacementEdit(key, ShowCompiler.VideoPlacement(placement));
        var startPublisher = false;

        lock (_livePlacementGate)
        {
            // Latest wins for this layer while one compositor update is in flight. Queuing every
            // native pointer pixel can leave the projected picture seconds behind the mouse and can
            // starve ordinary playback work on the same serialized session dispatcher.
            _pendingLivePlacements[key] = edit;
            if (!_livePlacementPublisherRunning)
            {
                _livePlacementPublisherRunning = true;
                startPublisher = true;
            }
        }

        if (startPublisher)
            _ = PublishLivePlacementsAsync();
    }

    private readonly record struct LivePlacementKey(
        ShowHost Host, Guid CueId, Guid CompositionId, int LayerIndex);

    private readonly record struct LivePlacementEdit(
        LivePlacementKey Key, ShowVideoPlacement Placement);


    /// <summary>Which composition a new placement would go on. Index into <see cref="PlacementCompositions"/>.</summary>
    [ObservableProperty]
    private int _placementTarget;

    /// <summary>
    /// Puts this cue on a composition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full-canvas at the next free layer, which is what somebody placing a cue almost always wants
    /// and is trivial to drag from. The canvas is right there to adjust it on.
    /// </para>
    /// <para>
    /// <b>Cover art is selected explicitly here.</b> The decoder's automatic election deliberately
    /// SKIPS attached pictures, so an MP3 placed with no video-track choice would put nothing on the
    /// canvas - the operator would see an empty layer and no reason for it. Naming the stream is what
    /// makes "place it and the album art appears" true.
    /// </para>
    /// </remarks>
    public void PlaceOnComposition()
    {
        if (Cue is not MediaCueNode and not VisualizerCueNode
            || Cue is null
            || PlacementTarget < 0
            || PlacementTarget >= Project.Compositions.Count)
            return;

        var composition = Project.Compositions[PlacementTarget];
        var cue = Cue;
        var list = CuePlacements.ListOf(cue);
        var still = Facts is { IsCoverArtOnly: true } ? Facts.PlaceableVideoTrack : null;

        if (list is null)
            return;

        using (journal.Composite($"place on {composition.Name}", "video"))
        {
            // APPENDED, not assigned: a cue can be on several canvases at once and the engine fans one
            // decoded source to all of them. Replacing would silently drop the mirror somebody added.
            journal.Do(new AddItemCommand<LayerPlacement>(
                list,
                new LayerPlacement
                {
                    CompositionId = composition.Id,
                    LayerIndex = NextLayer(composition),
                },
                list.Count,
                "video",
                $"place on {composition.Name}"));

            if (still is { } track && cue is MediaCueNode media && media.VideoTrackIndex is null)
            {
                journal.Do(new SetValueCommand<int?>(
                    cue.Id, "videoTrack", "video",
                    () => media.VideoTrackIndex, index => media.VideoTrackIndex = index, track.Index,
                    "show the cover art"));

                journal.Do(new SetValueCommand<string>(
                    cue.Id, "videoSignature", "video",
                    () => media.VideoTrackSignature,
                    signature => media.VideoTrackSignature = signature, track.Signature,
                    "remember which track"));
            }
        }

        SelectedPlacement = list.Count - 1;
        context.Reload();
    }

    /// <summary>Removes the SELECTED placement - the cue may still appear on other canvases.</summary>
    public void RemovePlacement()
    {
        if (Cue is not { } cue
            || CuePlacements.ListOf(cue) is not { } list
            || Placement is not { } placement)
            return;

        journal.Do(new RemoveItemCommand<LayerPlacement>(
            list, placement, "video", "remove from composition"));
        journal.CloseGroup();

        SelectedPlacement = Math.Clamp(SelectedPlacement, 0, Math.Max(0, list.Count - 1));
        context.Reload();
    }

    /// <summary>One above whatever is already there, so a new layer lands on top rather than under.</summary>
    private int NextLayer(CompositionDefinition composition) =>
        Project.AllCues()
            .SelectMany(CuePlacements.Of)
            .Where(placement => placement.CompositionId == composition.Id)
            .Select(placement => placement.LayerIndex + 1)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// One expander header per placement, so several can be organised without one anonymous editor.
    /// </summary>
    /// <remarks>
    /// A placement carries geometry, a fit, an opacity, a crop, a chroma key and a colour adjust - the
    /// better part of a screen each. With a picker above one editor, a cue on three canvases meant
    /// three screens of settings reachable only by remembering which entry was which. Each is now an
    /// expander, and opening one selects the placement that its nested editor projects.
    /// </remarks>
    public IReadOnlyList<PlacementHeader> PlacementHeaders =>
    [
        .. PlacementList.Select((placement, index) => new PlacementHeader(
            index,
            Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId)?.Name
            ?? "no composition",
            $"L{placement.LayerIndex}",
            Summarize(placement),
            index == SelectedPlacement)),
    ];

    /// <summary>Where the layer sits, as the one line a collapsed row can afford.</summary>
    private static string Summarize(LayerPlacement placement)
    {
        var full = placement is { X: 0, Y: 0, Width: 1, Height: 1 };
        var geometry = full
            ? "full frame"
            : $"{placement.X:0.##}, {placement.Y:0.##} · {placement.Width:0.##}×{placement.Height:0.##}";

        var extras = new List<string>();
        if (placement.Opacity < 1)
            extras.Add($"{placement.Opacity:0.##} opacity");
        if (placement.RotationDegrees != 0)
            extras.Add($"{placement.RotationDegrees:0.#}°");
        if (placement is { CropLeft: > 0 } or { CropTop: > 0 } or { CropRight: > 0 } or { CropBottom: > 0 })
            extras.Add("cropped");
        if (LayerEffectRack.Find(placement, ChromaEffectType) is { Enabled: true })
            extras.Add("keyed");
        if (LayerEffectRack.Find(placement, ColourEffectType) is { Enabled: true })
            extras.Add("graded");
        if (placement.HasVideoFx)
            extras.Add("mapped");

        return extras.Count == 0 ? geometry : $"{geometry} · {string.Join(" · ", extras)}";
    }

    /// <summary>Opens one placement's row, which is also how it becomes the one being edited.</summary>
    public void ExpandPlacement(int index)
    {
        if (index >= 0 && index < PlacementList.Count)
            SelectedPlacement = index;
    }

    // ── Automation properties ────────────────────────────────────────────────────────────────

    private List<AutomationTrack>? Lanes => Cue is null ? null : CueAutomation.ListOf(Cue);

    public bool CanCarryLanes => Lanes is not null;

    public IReadOnlyList<EffectLaneRow> EffectLanes =>
        Lanes is not { } tracks || Cue is not { } cue
            ? []
            : [.. tracks.Select((track, index) => new EffectLaneRow(
                index,
                AutomationName(cue, track),
                Describe(track),
                AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External))];

    public bool HasEffectLanes => EffectLanes.Count > 0;

    private string Describe(AutomationTrack track)
    {
        var detail = track.Keyframes.Count == 0
            // The redesign replaced double-click-only adding: click empty canvas, or press ＋ KEY.
            ? "empty - click the lane or use ＋ KEY to add one"
            : $"{track.Keyframes.Count} keyframe{(track.Keyframes.Count == 1 ? "" : "s")} · absolute time";
        return AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External
            ? track.Target.EndpointId is null ? detail + " · no endpoint" : detail + $" → {track.Target.Address}"
            : detail;
    }

    private string AutomationName(CueNode cue, AutomationTrack track)
    {
        var name = AutomationPropertyCatalog.Get(track.Target.PropertyId)?.DisplayName
                   ?? track.Target.PropertyId;
        var targetCue = track.Target.CueId is { } cueId ? Project.FindCue(cueId) : cue;
        if (track.Target.CueId is { } targetId)
            name = Project.FindCue(targetId) is { } target
                ? $"Q{CuePresentation.Number(target.Number)} · {name}"
                : $"missing cue · {name}";
        if (track.Target.ObjectId is { } objectId
            && targetCue is not null
            && CuePlacements.Of(targetCue).FirstOrDefault(placement => placement.Id == objectId) is { } placement)
            name += $" · layer {placement.LayerIndex}";
        return name;
    }

    public bool CanAddVolumeLane => CanAddLane(AutomationPropertyIds.CueVolume);
    public bool CanAddOpacityLane => CanAddLane(AutomationPropertyIds.PlacementOpacity);
    public bool CanAddPlacementXLane => CanAddLane(AutomationPropertyIds.PlacementX);
    public bool CanAddPlacementYLane => CanAddLane(AutomationPropertyIds.PlacementY);
    public bool CanAddPlacementWidthLane => CanAddLane(AutomationPropertyIds.PlacementWidth);
    public bool CanAddPlacementHeightLane => CanAddLane(AutomationPropertyIds.PlacementHeight);
    public bool CanAddPlacementRotationLane => CanAddLane(AutomationPropertyIds.PlacementRotation);
    public bool CanAddChromaSimilarityLane => CanAddLane(AutomationPropertyIds.ChromaSimilarity);
    public bool CanAddChromaSmoothnessLane => CanAddLane(AutomationPropertyIds.ChromaSmoothness);
    public bool CanAddChromaSpillLane => CanAddLane(AutomationPropertyIds.ChromaSpillReduction);
    public bool CanAddColorBrightnessLane => CanAddLane(AutomationPropertyIds.ColorBrightness);
    public bool CanAddColorContrastLane => CanAddLane(AutomationPropertyIds.ColorContrast);
    public bool HasAutomationChromaKey =>
        LayerEffectRack.Find(AutomationPlacement, ChromaEffectType) is not null;
    public bool HasAutomationColorAdjust =>
        LayerEffectRack.Find(AutomationPlacement, ColourEffectType) is not null;
    public bool HasAutomationAudioGain => AutomationTargetCue is MediaCueNode media
        ? media.AudioEffects.Any(effect => effect.EffectTypeId == S.Media.Routing.GainAudioEffect.EffectId)
        : Cue is MediaCueNode own
          && own.AudioEffects.Any(effect => effect.EffectTypeId == S.Media.Routing.GainAudioEffect.EffectId);
    public bool CanAddOutboundLane => CanCarryLanes;
    public string VolumeLaneLabel => AutomationTargetCue is GroupCueNode ? "Group audio trim" : "Volume";
    public string OpacityLaneLabel => AutomationTargetCue is GroupCueNode ? "Group video opacity" : "Opacity";

    public bool IsAutomationCue => Cue is AutomationCueNode;

    public string AutomationDuration
    {
        get => Cue is AutomationCueNode automation
            ? (automation.DurationMs / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        set
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                Edit(
                    "automationDuration",
                    cue => cue is AutomationCueNode automation ? automation.DurationMs : 0,
                    (cue, milliseconds) =>
                    {
                        if (cue is AutomationCueNode automation)
                            automation.DurationMs = milliseconds;
                    },
                    Math.Max(1, (int)Math.Round(seconds * 1000)));
        }
    }

    public bool AutomationRestoreBase
    {
        get => Cue is AutomationCueNode { Completion: AutomationCompletion.RestoreBase };
        set => Edit(
            "automationCompletion",
            cue => cue is AutomationCueNode automation ? automation.Completion : AutomationCompletion.HoldFinal,
            (cue, completion) =>
            {
                if (cue is AutomationCueNode automation)
                    automation.Completion = completion;
            },
            value ? AutomationCompletion.RestoreBase : AutomationCompletion.HoldFinal);
    }

    public IReadOnlyList<string> AutomationTargetCues =>
        [.. AutomationTargets.Select(target => $"Q{CuePresentation.Number(target.Number)} · {target.Label}")];

    public IReadOnlyList<string> AutomationTargetPlacements => AutomationTargetCue is not { } target
        ? []
        :
        [
            .. CuePlacements.Of(target).Select(placement =>
                $"{Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId)?.Name ?? "missing composition"}"
                + $" · L{placement.LayerIndex}"),
        ];

    public bool HasMultipleAutomationTargetPlacements => AutomationTargetPlacements.Count > 1;

    private IReadOnlyList<CueNode> AutomationTargets =>
        [.. Project.AllCues().Where(candidate => candidate.Id != Cue?.Id
            && candidate is not CommentCueNode and not ActionCueNode and not AutomationCueNode)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddVolumeLane))]
    [NotifyPropertyChangedFor(nameof(CanAddOpacityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementXLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementYLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementWidthLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementHeightLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementRotationLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSimilarityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSmoothnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSpillLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorBrightnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorContrastLane))]
    [NotifyPropertyChangedFor(nameof(HasAutomationChromaKey))]
    [NotifyPropertyChangedFor(nameof(HasAutomationColorAdjust))]
    [NotifyPropertyChangedFor(nameof(VolumeLaneLabel))]
    [NotifyPropertyChangedFor(nameof(OpacityLaneLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationTargetPlacements))]
    [NotifyPropertyChangedFor(nameof(HasMultipleAutomationTargetPlacements))]
    private int _automationTargetCueIndex;

    partial void OnAutomationTargetCueIndexChanged(int value) => AutomationTargetPlacementIndex = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddOpacityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementXLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementYLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementWidthLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementHeightLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementRotationLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSimilarityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSmoothnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSpillLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorBrightnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorContrastLane))]
    [NotifyPropertyChangedFor(nameof(HasAutomationChromaKey))]
    [NotifyPropertyChangedFor(nameof(HasAutomationColorAdjust))]
    private int _automationTargetPlacementIndex;

    private CueNode? AutomationTargetCue => AutomationTargetCueIndex >= 0
        && AutomationTargetCueIndex < AutomationTargets.Count
            ? AutomationTargets[AutomationTargetCueIndex]
            : null;

    private LayerPlacement? AutomationPlacement => Cue switch
    {
        AutomationCueNode when AutomationTargetCue is { } target =>
            CuePlacements.Of(target).ElementAtOrDefault(AutomationTargetPlacementIndex),
        not null => Placement,
        _ => null,
    };

    private AutomationTargetRef? TargetFor(string propertyId)
    {
        if (Cue is not { } cue)
            return null;
        var targetCue = cue is AutomationCueNode ? AutomationTargetCue : cue;
        var targetCueId = cue is AutomationCueNode ? targetCue?.Id : null;
        var placement = AutomationPlacement;
        if (targetCue is MediaCueNode audioCue
            && AudioEffectCatalog.TryResolveProperty(propertyId, out var audioEffectType, out _)
            && audioCue.AudioEffects.FirstOrDefault(effect =>
                effect.EffectTypeId == audioEffectType.TypeId) is { } audioEffect)
            return NewPlacementTarget(targetCueId, audioEffect.Id, propertyId);
        if (placement is not null
            && LayerEffectCatalog.TryResolveProperty(propertyId, out var effectType, out _)
            && LayerEffectRack.Effective(placement).FirstOrDefault(effect =>
                string.Equals(effect.EffectTypeId, effectType.TypeId, StringComparison.Ordinal)) is { } effect)
            return NewPlacementTarget(targetCueId, effect.Id, propertyId);
        return propertyId switch
        {
            AutomationPropertyIds.CueVolume when targetCue is MediaCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.CueVolume },
            AutomationPropertyIds.CueVolume when cue is AutomationCueNode && targetCue is GroupCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.GroupAudioTrim },
            AutomationPropertyIds.PlacementOpacity when cue is AutomationCueNode && targetCue is GroupCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.GroupVideoOpacity },
            AutomationPropertyIds.PlacementOpacity when placement is not null =>
                new AutomationTargetRef
                {
                    CueId = targetCueId,
                    PropertyId = AutomationPropertyIds.PlacementOpacity,
                    ObjectId = placement.Id,
                },
            AutomationPropertyIds.OscValue => new AutomationTargetRef { PropertyId = AutomationPropertyIds.OscValue },
            AutomationPropertyIds.MidiControlValue => new AutomationTargetRef { PropertyId = AutomationPropertyIds.MidiControlValue },
            AutomationPropertyIds.PlacementX when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementX),
            AutomationPropertyIds.PlacementY when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementY),
            AutomationPropertyIds.PlacementWidth when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementWidth),
            AutomationPropertyIds.PlacementHeight when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementHeight),
            AutomationPropertyIds.PlacementRotation when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementRotation),
            _ => null,
        };
    }

    private static AutomationTargetRef NewPlacementTarget(Guid? cueId, Guid placementId, string propertyId) =>
        new() { CueId = cueId, ObjectId = placementId, PropertyId = propertyId };

    public void AddLane(string propertyId)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || TargetFor(propertyId) is not { } target
            || tracks.Any(track => CueAutomation.SameTarget(track.Target, target)))
            return;

        var descriptor = AutomationPropertyCatalog.Get(target.PropertyId)!;
        var authoredCue = cue is AutomationCueNode ? AutomationTargetCue : cue;
        var authored = (authoredCue is null ? [] : AutomationPropertyCatalog.ForCue(authoredCue))
            .FirstOrDefault(option => option.Target.PropertyId == target.PropertyId
                && option.Target.ObjectId == target.ObjectId)?.AuthoredValue
            ?? descriptor.Value.Default;
        var durationMs = Math.Max(1_000, (long)(LaneDuration(cue)?.TotalMilliseconds ?? 30_000));
        var added = new AutomationTrack
        {
            Target = target,
            Keyframes =
            [
                new AutomationKeyframe { TimeMs = 0, Value = authored },
                new AutomationKeyframe { TimeMs = durationMs, Value = authored },
            ],
        };

        journal.Do(new AddItemCommand<AutomationTrack>(
            tracks, added, tracks.Count, "cues", $"add {descriptor.DisplayName} automation"));
        journal.CloseGroup();
        context.Reload();
    }

    public bool CanAddLane(string propertyId) =>
        Lanes is { } tracks
        && TargetFor(propertyId) is { } target
        && tracks.All(track => !CueAutomation.SameTarget(track.Target, target));

    public void RemoveLane(int index)
    {
        if (Lanes is not { } tracks || index < 0 || index >= tracks.Count)
            return;
        var track = tracks[index];
        journal.Do(new RemoveItemCommand<AutomationTrack>(
            tracks, track, "cues", $"remove {track.Target.PropertyId} automation"));
        journal.CloseGroup();
        context.Reload();
    }

    public AutomationEditorViewModel? LaneEditor(int index)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || index < 0 || index >= tracks.Count)
            return null;
        var track = tracks[index];
        // A controller cue owns its own time coordinate; the targeted clip may already be minutes
        // into playback, so stretching that clip's waveform across the controller duration would be
        // actively misleading. Waveform context is exact only for a media cue's own track.
        var waveformCue = cue as MediaCueNode;
        var sourceDuration = waveformCue is null
            ? null
            : waveformCue.Id == Cue?.Id
                ? ClipDuration
                : MediaFacts?.Invoke(waveformCue)?.Duration
                  ?? (waveformCue.SourceDurationMs > 0
                      ? TimeSpan.FromMilliseconds(waveformCue.SourceDurationMs)
                      : null);
        return new AutomationEditorViewModel(
            journal,
            cue,
            track,
            LaneDuration(cue),
            Host,
            waveformCue,
            waveformCue is null ? null : ClipPath(waveformCue),
            sourceDuration,
            CacheRoot,
            WaveformCacheBytes);
    }

    private TimeSpan? LaneDuration(CueNode cue) => cue switch
    {
        MediaCueNode media => media.TrimmedLength(ClipDuration),
        TextCueNode { DurationMs: > 0 } text => TimeSpan.FromMilliseconds(text.DurationMs),
        VisualizerCueNode { HoldMs: > 0 } visualizer => TimeSpan.FromMilliseconds(visualizer.HoldMs),
        AutomationCueNode { DurationMs: > 0 } automation => TimeSpan.FromMilliseconds(automation.DurationMs),
        _ => null,
    };

    public PromptViewModel? ConfigureLane(int index)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || index < 0 || index >= tracks.Count)
            return null;

        var track = tracks[index];
        if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain != AutomationDomain.External)
            return null;

        var kind = track.Target.PropertyId == AutomationPropertyIds.OscValue
            ? EndpointKind.OscOut : EndpointKind.MidiOut;
        var endpoints = journal.Project.ActionEndpoints.Where(endpoint => endpoint.Kind == kind).ToList();
        var options = endpoints.Count == 0 ? ["(no compatible endpoints)"] : endpoints.Select(e => e.Name).ToList();
        var selected = Math.Max(0, endpoints.FindIndex(endpoint => endpoint.Id == track.Target.EndpointId));

        return new PromptViewModel(
            $"Configure {AutomationName(cue, track)}",
            endpoints.Count == 0
                ? $"Add a {kind} endpoint in Targets first."
                : "Automation sends its current value at the bounded rate below.",
            [
                new PromptField
                {
                    Label = "Endpoint",
                    Kind = PromptFieldKind.Choice,
                    Options = options,
                    SelectedIndex = selected,
                },
                new PromptField
                {
                    Label = "Address",
                    Value = track.Target.Address,
                    Hint = kind == EndpointKind.OscOut
                        ? "/osc/address - the automated value is its argument"
                        : "cc <channel> <controller> - automation supplies value 0–127",
                },
                new PromptField
                {
                    Label = "Send rate (Hz)",
                    Value = track.Target.SendRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Hint = "1–120; 25 is a good default",
                },
                new PromptField
                {
                    Label = "On STOP / PANIC",
                    Kind = PromptFieldKind.Choice,
                    Options = ["Freeze at current value", "Land on final key"],
                    SelectedIndex = track.Interruption == AutomationInterruption.LandFinal ? 1 : 0,
                    Hint = "Freeze is safer for lights, machinery and external faders.",
                },
            ],
            prompt =>
            {
                if (endpoints.Count == 0)
                    return;

                var endpointIndex = Math.Clamp(prompt["Endpoint"].SelectedIndex, 0, endpoints.Count - 1);
                _ = int.TryParse(prompt["Send rate (Hz)"].Value, out var parsedRate);
                var rate = Math.Clamp(parsedRate, 1, 120);
                using (journal.Composite("configure outbound automation", "cues"))
                {
                    journal.Do(new SetValueCommand<Guid?>(
                        cue.Id, $"automation:{track.Id}:endpoint", "cues",
                        () => track.Target.EndpointId, value => track.Target.EndpointId = value,
                        endpoints[endpointIndex].Id, "choose automation endpoint"));
                    journal.Do(new SetValueCommand<string>(
                        cue.Id, $"automation:{track.Id}:address", "cues",
                        () => track.Target.Address, value => track.Target.Address = value,
                        prompt["Address"].Value.Trim(), "set automation address"));
                    journal.Do(new SetValueCommand<int>(
                        cue.Id, $"automation:{track.Id}:rate", "cues",
                        () => track.Target.SendRateHz, value => track.Target.SendRateHz = value,
                        rate, "set automation send rate"));
                    var interruption = prompt["On STOP / PANIC"].SelectedIndex == 1
                        ? AutomationInterruption.LandFinal
                        : AutomationInterruption.Freeze;
                    journal.Do(new SetValueCommand<AutomationInterruption>(
                        cue.Id, $"automation:{track.Id}:interruption", "cues",
                        () => track.Interruption, value => track.Interruption = value,
                        interruption, "set automation interruption policy"));
                }
                context.Reload();
            },
            confirm: "APPLY");
    }

    /// <summary>The inspector's multi-selection single-value edit, needed by the automation
    /// duration/completion fields (same semantics as the General fields' form).</summary>
    private void Edit<T>(string property, Func<CueNode, T> read, Action<CueNode, T> write, T value)
    {
        var targets = context.Selected;
        if (targets.Count == 0)
            return;

        // A multi-selection edit is one thing the operator did, so it is one thing to undo.
        if (targets.Count > 1)
        {
            using (journal.Composite($"set {property} on {targets.Count} cues", "cues"))
                foreach (var cue in targets)
                    journal.Do(Command(cue, property, read, write, value));
        }
        else
        {
            journal.Do(Command(targets[0], property, read, write, value));
        }

        context.Reload();
    }

    private static SetValueCommand<T> Command<T>(
        CueNode cue, string property, Func<CueNode, T> read, Action<CueNode, T> write, T value) =>
        new(cue.Id, property, "cues", () => read(cue), parsed => write(cue, parsed), value,
            $"set {property} on Q{CuePresentation.Number(cue.Number)}");
}
