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
    /// The line that paces the show clock. It must run natively at <see cref="MixSampleRate"/> - a
    /// resampled master would drift the clock against itself.
    /// </summary>
    public Guid? ClockMasterLineId { get; set; }

    public List<LogicalAudioChannel> LogicalChannels { get; set; } = [];

    /// <summary>
    /// Named linked-editing groups (register item 9). A stereo pair is simply a two-member group.
    /// Grouping affects EDITING and DISPLAY only - the mix math stays strictly per channel.
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
    /// patch the moment they wanted it back - and on an absent device, losing it is permanent.
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
    public AudioLineKind Kind { get; set; } = AudioLineKind.LocalAudio;

    /// <summary>
    /// How this line is found on a machine - a device name, an NDI source name, a file pattern. It is
    /// deliberately a hint, not an identity: on another machine it may match nothing, and that is a
    /// reported absence rather than a silent redirect to the default device.
    /// </summary>
    public string DeviceHint { get; set; } = "";

    public int Channels { get; set; } = 2;

    /// <summary>Null means "whatever the device offers"; a value here is the line's native rate.</summary>
    public int? SampleRate { get; set; }

    /// <summary>
    /// Register item 25: a REQUIRED line that is absent is an error, not a warning. The flag is
    /// inverted from the obvious design on purpose - it lets a show say "this cannot run without the
    /// main PA" instead of asking every optional output to excuse itself.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Where a <see cref="AudioLineKind.FileRecord"/> line writes, or a
    /// <see cref="AudioLineKind.Stream"/> line pushes. Null on the kinds that address a device.
    /// </summary>
    /// <remarks>
    /// Nullable rather than always-present so a project full of ordinary interface lines does not
    /// carry a recording block per line that nothing reads.
    /// </remarks>
    public RecordTarget? Record { get; set; }
}

public enum AudioLineKind
{
    /// <summary>
    /// A sound device on this machine.
    /// </summary>
    /// <remarks>
    /// NOT named after a backend. Which library opens the device - PortAudio or miniaudio - is a
    /// MACHINE setting, so a line called "PortAudio" played through miniaudio would be a document
    /// contradicting itself. What the document means is "a local sound card", and that is stable.
    /// </remarks>
    LocalAudio,
    Ndi,
    FileRecord,
    Stream,
}

/// <summary>
/// Where a recording or a stream goes.
/// </summary>
/// <remarks>
/// <para>
/// Shared by audio lines and video outputs, because "record" means the same thing on both sides and a
/// second copy would drift. The FORMAT is the pattern's own extension (register item 30's patterns are
/// written as whole filenames - <c>show-{date}.mka</c>), so there is no separate container picker to
/// contradict the name the operator typed.
/// </para>
/// <para>
/// <b>Arming is an operator action, not a property of existing.</b> A record line that merely appears
/// in a show writes nothing until somebody arms it; the alternative - a show that starts recording
/// because it was opened - fills disks during rehearsal and overwrites nothing anybody wanted.
/// <see cref="ArmWithShow"/> is the opt-in for the rig where recording every performance IS the point.
/// </para>
/// </remarks>
public sealed record RecordTarget
{
    /// <summary>The folder files land in. Empty means the machine's own recordings folder.</summary>
    public string Directory { get; set; } = "";

    /// <summary>The filename, with insert tokens and its extension (register item 30).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>The push URL for a stream (rtmp://, srt://, rtsp://). Unused when recording to file.</summary>
    public string Url { get; set; } = "";

    /// <summary>Arms as soon as the show opens, for a rig that records every performance.</summary>
    public bool ArmWithShow { get; set; }

    /// <summary>
    /// Fills idle time with black and silence instead of collapsing it.
    /// </summary>
    /// <remarks>
    /// The difference between an ARCHIVE and a REEL. A continuous recording runs on wall clock, so the
    /// gap between act one and act two is in the file and a timecode taken off the recording still
    /// matches the show. Content-only collapses those gaps, which is what somebody cutting a montage
    /// wants and what would make an archive useless for finding anything.
    /// <para>
    /// Streams are always continuous whatever this says - an ingest drops a connection that stops
    /// sending, so a stream that went quiet between cues would simply die.
    /// </para>
    /// </remarks>
    public bool Continuous { get; set; }
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

/// <summary>
/// The audition rig: one audio line plus one video surface (register item 15).
/// </summary>
/// <remarks>
/// <para>
/// <b>One rig, not two halves.</b> Auditioning is a single thing an operator does - "let me hear and
/// see this before I fire it" - so the pane that configures it appears in both the Audio and Video
/// views rather than being split across them.
/// </para>
/// <para>
/// <b>The audio side names a project LINE, not a device</b> (D8). The rig is an output like any other:
/// it takes that line's own channel count rather than being hardcoded stereo, it travels with the show,
/// it goes absent on a machine that lacks it, and it relinks on arrival - all for the same reasons
/// register item 14 gives for every other line. Naming a raw device here would make the audition path
/// the one output in the app that behaved differently from the rest.
/// </para>
/// <para>
/// Null <see cref="AudioLineId"/> means the bay's default monitor line, which is the first line that
/// opened. That is a real answer on a one-interface rig and the reason the rig works before anybody
/// configures it.
/// </para>
/// </remarks>
public sealed record AuditionRig
{
    /// <summary>The line to monitor through, or null for the bay's default monitor terminal.</summary>
    public Guid? AudioLineId { get; set; }

    public AuditionSurface Surface { get; set; } = AuditionSurface.None;

    /// <summary>
    /// Monitor trim, applied to the audition path only.
    /// </summary>
    /// <remarks>
    /// Never reaches the program mix: the whole point of auditioning is that it cannot be heard by the
    /// audience, so this gain stage is outside the composition chain the plan documents.
    /// </remarks>
    public double LevelDb { get; set; } = -12;

    /// <summary>Ducks the monitor while the program is sounding - the booth's own ears, not the mix.</summary>
    public bool DuckWhenProgramSounds { get; set; } = true;

    /// <summary>The audition canvas size. Follows the largest composition when left at zero.</summary>
    public int SurfaceWidth { get; set; }

    public int SurfaceHeight { get; set; }
}

public enum AuditionSurface
{
    /// <summary>Audio only. The default: a video surface costs a window and most cues are audio.</summary>
    None,

    /// <summary>A window on the operator's own screen.</summary>
    Window,
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
