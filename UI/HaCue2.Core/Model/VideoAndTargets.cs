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

    /// <summary>
    /// The canvas rate. Sixty, because that is what the screens in the room run at.
    /// </summary>
    /// <remarks>
    /// It defaulted to 30, which halves the smoothness of every pan, wipe and fade the show contains
    /// for no gain — a compositor already running is not meaningfully cheaper at 30 than at 60, and a
    /// projector fed 30 into a 60 Hz panel judders visibly on horizontal movement. A show that wants 30
    /// can still say so.
    /// </remarks>
    public double FramesPerSecond { get; set; } = 60;

    /// <summary>Shown when the canvas is empty. Takes precedence over an output's own fallback.</summary>
    public string IdleImagePath { get; set; } = "";

    /// <summary>How the idle image fills the canvas.</summary>
    /// <remarks>
    /// A holding slate is rarely the canvas's own aspect ratio — it is a logo, or a photograph somebody
    /// had. Without a choice it was stretched, which is the one option that always looks wrong.
    /// </remarks>
    public LayerFit IdleImageFit { get; set; } = LayerFit.Contain;
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

    /// <summary>
    /// The window's size when it is NOT fullscreen. Zero takes the composition's own size.
    /// </summary>
    /// <remarks>
    /// Separate from the composition because they answer different questions: the composition is what
    /// the show is authored against, and this is how big the window on this machine happens to be. A
    /// 4K canvas monitored in a 960×540 window on a laptop is an ordinary way to work.
    /// </remarks>
    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    /// <summary>Used only when the composition has no idle image of its own (register item 23).</summary>
    public string IdleFallbackPath { get; set; } = "";

    /// <summary>How the fallback image fills this output, for the same reason the composition's has one.</summary>
    public LayerFit IdleFallbackFit { get; set; } = LayerFit.Contain;

    /// <summary>Register item 25: a required output that is absent is an error, not a warning.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// The output's own raster, in pixels, for mapping. Zero follows the composition's size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a video wall expressible. A 1920×2160 stacked canvas fed to two 1920×1080
    /// projectors needs each output's destination rectangles measured against 1920×1080 — with the
    /// composition's size standing in for every output, the top projector's section would be described
    /// as the top half of a 2160-tall frame and land at half height on a screen that is not 2160 tall.
    /// </para>
    /// <para>
    /// Zero rather than a guess, because on a fullscreen output the real raster is the screen's and this
    /// machine may not be the one the show runs on. The composition's size is the honest fallback: it is
    /// the one raster the document actually knows.
    /// </para>
    /// </remarks>
    public int MappingWidth { get; set; }

    public int MappingHeight { get; set; }

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

    /// <summary>
    /// Whether this section is drawn.
    /// </summary>
    /// <remarks>
    /// Separate from deleting it, for the same reason the output's own mapping toggle is: switching one
    /// panel of a blend off to check the one beside it is an ordinary thing to do at a get-in, and if
    /// the only way to say it were to delete the section, its geometry would have to be authored again
    /// to get it back. The engine's own section spec has always had this flag; the document had not.
    /// </remarks>
    public bool Enabled { get; set; } = true;

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

    /// <summary>
    /// The warp mesh, in control points across and down. Zero on either axis means no warp.
    /// </summary>
    /// <remarks>
    /// Independent because panels are: a three-projector blend across a flat cyc wants 5×2, and forcing
    /// it to 5×5 gives the operator fifteen handles they have to leave alone and can knock by accident.
    /// </remarks>
    public int MeshColumns { get; set; }

    public int MeshRows { get; set; }

    /// <summary>
    /// The square mesh older documents wrote, read once and folded into <see cref="MeshColumns"/>.
    /// </summary>
    /// <remarks>
    /// Write-only on purpose — it has no getter, so it is read from a file that has it and never
    /// written back to one. A document saved by this build carries the two axes and nothing else, and a
    /// document saved by the previous build still opens with its mesh intact.
    /// </remarks>
    [System.Text.Json.Serialization.JsonPropertyName("warpGrid")]
    public int LegacyWarpGrid
    {
        set
        {
            if (value < 2 || MeshColumns > 0 || MeshRows > 0)
                return;

            MeshColumns = value;
            MeshRows = value;
        }
    }

    /// <summary>Row-major mesh offsets, 2 doubles per point. Empty until the mesh is touched.</summary>
    public List<double> WarpOffsets { get; set; } = [];

    /// <summary>How many offsets a complete mesh for this section holds — zero when it has no warp.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int MeshPointCount => MeshColumns >= 2 && MeshRows >= 2 ? MeshColumns * MeshRows : 0;

    /// <summary>Whether the section carries a mesh the renderer can actually resolve.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMesh => MeshPointCount > 0 && WarpOffsets.Count == MeshPointCount * 2;
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

    /// <summary>The wall clock. A binding's input is a time of day: "22:30" or "22:30:00".</summary>
    Schedule,

    /// <summary>Incoming MIDI timecode. A binding's input is a label: "01:12:44:07".</summary>
    Timecode,
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

    /// <summary>
    /// A keyboard binding normally yields to a text editor. Global bindings remain live while typing;
    /// use this only for commands whose show-control value outweighs the risk of stealing a character.
    /// Ignored by non-keyboard sources.
    /// </summary>
    public bool AllowWhileTyping { get; set; }
}

public enum TriggerTarget
{
    Cue,
    Parameter,
    Transport,
}
