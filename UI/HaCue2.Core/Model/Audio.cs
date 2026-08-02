namespace HaCue2.Core.Model;

/// <summary>
/// The project's audio vocabulary (logical outputs) and its patch onto this machine's lines.
/// </summary>
/// <remarks>
/// Two matrices, never one. A cue sends its source channels to LOGICAL outputs (N×V); the patch maps
/// logical outputs to real device channels (V×R). The show file therefore never contains a device
/// name, which is the whole venue-swap seam: the show keeps "Lobby", each venue patches it.
/// </remarks>
public sealed record ProjectAudioPatch
{
    public int MixSampleRate { get; set; } = 48_000;

    /// <summary>
    /// The line that paces the show clock. It must run natively at <see cref="MixSampleRate"/> — a
    /// resampled master would drift the clock against itself.
    /// </summary>
    public Guid? ClockMasterLineId { get; set; }

    public List<LogicalAudioChannel> LogicalChannels { get; set; } = [];

    /// <summary>
    /// Named linked-editing groups (register item 9). A stereo pair is simply a two-member group.
    /// Grouping affects EDITING and DISPLAY only — the mix math stays strictly per channel.
    /// </summary>
    public List<OutputGroup> Groups { get; set; } = [];

    /// <summary>The V×R cells. A cell absent from this list is not routed, which is not the same as muted.</summary>
    public List<PatchCell> Cells { get; set; } = [];

    public OutputGroup? GroupOf(Guid channelId) =>
        Groups.FirstOrDefault(group => group.MemberIds.Contains(channelId));
}

/// <summary>A named channel that exists whether or not any hardware does.</summary>
public sealed record LogicalAudioChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>
    /// Whether this channel's meter appears in the Output info drawer's compact row and in the
    /// status-bar health token. Was "pin to strip" before the strip was removed.
    /// </summary>
    public bool MeterInSummary { get; set; } = true;
}

/// <summary>A named group of logical outputs, linked for editing.</summary>
public sealed record OutputGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> MemberIds { get; set; } = [];
}

/// <summary>One V×R cell: a logical output feeding one channel of one real line, at a gain.</summary>
public sealed record PatchCell
{
    public Guid LogicalChannelId { get; set; }
    public Guid LineId { get; set; }

    /// <summary>Zero-based in the model and the runtime; the UI renders it one-based.</summary>
    public int LineChannel { get; set; }

    public double GainDb { get; set; }

    /// <summary>
    /// Muted keeps the routing and silences it. Deleting the cell instead would lose the operator's
    /// patch the moment they wanted it back — and on an absent device, losing it is permanent.
    /// </summary>
    public bool Muted { get; set; }

    public bool Matches(Guid channelId, Guid lineId, int lineChannel) =>
        LogicalChannelId == channelId && LineId == lineId && LineChannel == lineChannel;
}

/// <summary>
/// A machine-side audio line the project owns: an interface, an NDI sender, a recorder, a stream.
/// </summary>
public sealed record AudioLineDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public AudioLineKind Kind { get; set; } = AudioLineKind.PortAudio;

    /// <summary>
    /// How this line is found on a machine — a device name, an NDI source name, a file pattern. It is
    /// deliberately a hint, not an identity: on another machine it may match nothing, and that is a
    /// reported absence rather than a silent redirect to the default device.
    /// </summary>
    public string DeviceHint { get; set; } = "";

    public int Channels { get; set; } = 2;

    /// <summary>Null means "whatever the device offers"; a value here is the line's native rate.</summary>
    public int? SampleRate { get; set; }

    /// <summary>
    /// Register item 25: a REQUIRED line that is absent is an error, not a warning. The flag is
    /// inverted from the obvious design on purpose — it lets a show say "this cannot run without the
    /// main PA" instead of asking every optional output to excuse itself.
    /// </summary>
    public bool Required { get; set; }
}

public enum AudioLineKind
{
    PortAudio,
    Ndi,
    FileRecord,
    Stream,
}

/// <summary>
/// A named, recallable partial patch state.
/// </summary>
/// <remarks>
/// It stores ONLY the cells it was saved with. Recalling "Interval" touches Lobby and nothing else,
/// so two patch cues can own disjoint parts of the house without fighting. A whole-console reset
/// would make the second recall undo the first.
/// </remarks>
public sealed record PatchSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<PatchCell> Cells { get; set; } = [];
}

/// <summary>
/// One inline level move on a patch cue, for the common case that does not deserve a stored snapshot.
/// </summary>
/// <remarks>
/// The nullable line/channel widen the target: a null <see cref="LineId"/> means every cell fed by
/// this logical channel, and a null <see cref="LineChannel"/> with a line means every cell on that
/// line. That is how "Fold L/R up 6 dB" stays one entry when the foldback is patched to four places.
/// </remarks>
public sealed record PatchLevelChange
{
    public Guid LogicalChannelId { get; set; }
    public Guid? LineId { get; set; }
    public int? LineChannel { get; set; }
    public double GainDb { get; set; }
    public bool Muted { get; set; }
}

/// <summary>A cue's send from one source channel into one logical output (the N×V matrix).</summary>
public sealed record CueAudioSend
{
    /// <summary>Zero-based channel of the cue's own media.</summary>
    public int SourceChannel { get; set; }

    public Guid LogicalChannelId { get; set; }
    public double GainDb { get; set; }
    public bool Muted { get; set; }
}
