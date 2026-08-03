namespace HaCue2.Core.Model;

/// <summary>
/// A canvas cues are placed on.
/// </summary>
/// <remarks>
/// It owns exactly three things: size, frame rate, and an idle image (register item 21). There is no
/// visualizer flag — that was HaPlay residue — and no composition-level mapping: mapping belongs to
/// the OUTPUT BINDING, so the same composition can render warped to a projector and clean to a TV.
/// </remarks>
public sealed record CompositionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FramesPerSecond { get; set; } = 30;

    /// <summary>Shown when the canvas is empty. Takes precedence over an output's own fallback.</summary>
    public string IdleImagePath { get; set; } = "";
}

/// <summary>A video output and the composition it shows.</summary>
public sealed record VideoOutputDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public VideoOutputKind Kind { get; set; } = VideoOutputKind.LocalScreen;
    public Guid? CompositionId { get; set; }

    /// <summary>A hint, like an audio line's: on another machine it may match nothing.</summary>
    public string TargetHint { get; set; } = "";

    public bool Fullscreen { get; set; } = true;

    /// <summary>Used only when the composition has no idle image of its own (register item 23).</summary>
    public string IdleFallbackPath { get; set; } = "";

    /// <summary>Register item 25: a required output that is absent is an error, not a warning.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// Per-output mapping (register item 22). Null or empty means a clean feed — the common case, and
    /// the reason mapping is opt-in per output rather than a property of the composition.
    /// </summary>
    public List<MappingSection> Mapping { get; set; } = [];

    /// <summary>
    /// Whether the mapping is in force, separately from whether one is authored.
    /// </summary>
    /// <remarks>
    /// The two are different questions and the difference is an hour of somebody's evening. "Show this
    /// output clean tonight" is an ordinary thing to want — a projector swapped for a flat screen, a
    /// warp being checked against an unwarped feed — and if the only way to say it were to delete the
    /// sections, the warp would have to be authored again to get it back.
    /// </remarks>
    public bool MappingEnabled { get; set; } = true;

    public bool IsMapped => MappingEnabled && Mapping.Count > 0;

    /// <summary>
    /// Where a <see cref="VideoOutputKind.Record"/> output writes, or a
    /// <see cref="VideoOutputKind.Stream"/> output pushes. Null on the kinds that address a screen.
    /// </summary>
    public RecordTarget? Record { get; set; }
}

public enum VideoOutputKind
{
    LocalScreen,
    Ndi,
    Record,
    Stream,
}

/// <summary>
/// One piece of a composition, placed (and optionally warped) onto an output.
/// </summary>
/// <remarks>
/// Mesh warp first; corner-pin stays reserved in the model rather than implemented, so a project
/// written today does not have to change shape when it arrives.
/// </remarks>
public sealed record MappingSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The region of the composition this section samples, in fractions.</summary>
    public double SourceX { get; set; }

    public double SourceY { get; set; }
    public double SourceWidth { get; set; } = 1;
    public double SourceHeight { get; set; } = 1;

    /// <summary>Where it lands on the output, in fractions.</summary>
    public double TargetX { get; set; }

    public double TargetY { get; set; }
    public double TargetWidth { get; set; } = 1;
    public double TargetHeight { get; set; } = 1;

    public double RotationDegrees { get; set; }
    public double Opacity { get; set; } = 1;
    public double Brightness { get; set; } = 1;

    /// <summary>0 = no warp; otherwise the mesh is N×N.</summary>
    public int WarpGrid { get; set; }

    /// <summary>Row-major mesh offsets, 2 doubles per point. Empty until the mesh is touched.</summary>
    public List<double> WarpOffsets { get; set; } = [];
}

/// <summary>Somewhere action cues send to.</summary>
public sealed record ActionEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public EndpointKind Kind { get; set; } = EndpointKind.OscOut;
    public string Host { get; set; } = "";
    public int Port { get; set; }

    /// <summary>
    /// Register item 24: the test payload is stored PER endpoint. A generic ping proves the socket is
    /// open; it does not prove the desk understood you.
    /// </summary>
    public string TestMessage { get; set; } = "";
}

public enum EndpointKind
{
    OscOut,
    MidiOut,
}

/// <summary>An inbound source that can fire cues or move a parameter.</summary>
public sealed record TriggerInputDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public TriggerInputKind Kind { get; set; } = TriggerInputKind.MidiIn;
    public string DeviceHint { get; set; } = "";
    public int Port { get; set; }

    /// <summary>
    /// Per-source enable. The single External-input master toggle (register item 3) gates all of
    /// them at once; this is the "which of them" half, and it never gates GO.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public List<TriggerBinding> Bindings { get; set; } = [];
}

public enum TriggerInputKind
{
    MidiIn,
    OscIn,
    Keyboard,
}

/// <summary>
/// One input mapped to something.
/// </summary>
/// <remarks>
/// Continuous-controller bindings to PARAMETERS are v1, not just note→cue (register item 24), which
/// is why the target is a choice rather than a cue id: a fader riding the master trim is a first-class
/// binding, not a special case bolted on later.
/// </remarks>
public sealed record TriggerBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>How the input is recognised: "note 3 ch 1", "/hacue/go", "Space".</summary>
    public string Input { get; set; } = "";

    public TriggerTarget Target { get; set; } = TriggerTarget.Cue;
    public Guid? TargetCueId { get; set; }

    /// <summary>For <see cref="TriggerTarget.Parameter"/>: the parameter's registry id.</summary>
    public string ParameterId { get; set; } = "";

    public double RangeMin { get; set; }
    public double RangeMax { get; set; } = 1;

    /// <summary>Ignore repeats inside this window; 0 disables the filter.</summary>
    public int NoRepeatMs { get; set; }
}

public enum TriggerTarget
{
    Cue,
    Parameter,
    Transport,
}
