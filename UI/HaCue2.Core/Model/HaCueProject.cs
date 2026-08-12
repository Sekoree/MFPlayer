namespace HaCue2.Core.Model;

/// <summary>
/// A HaCue2 show: everything that travels in the <c>.hacue2proj</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutable on purpose.</b> The undo journal holds COMMANDS, not document snapshots
/// (Plans/HaCue-Extraction-And-Project-Audio-Patch-Plan.md, "Editing model"), so undo cost stays
/// proportional to what changed rather than to project size. That only works if a command can mutate
/// the document in place and put it back — an immutable document would force every step to keep a
/// whole clone, which is precisely the design the plan rejects.
/// </para>
/// <para>
/// <b>Every property uses <c>set</c>, never <c>init</c>.</b> Not a style choice: System.Text.Json's
/// source generator emits an object initializer that assigns EVERY init-only property, so one absent
/// from the JSON is written as the CLR default and the C# initializer beside it is silently lost.
/// A <c>bool Enabled { get; init; } = true</c> would deserialize to <c>false</c> on any document
/// written before that field existed. This bit HaPlay's <c>FadeCueNode</c>/<c>JumpCueNode</c>
/// already; here it is a rule.
/// </para>
/// <para>
/// <b>IDs bind, names and positions do not.</b> Reordering a logical channel never retargets a cue,
/// and renaming one never breaks a send.
/// </para>
/// </remarks>
public sealed record HaCueProject
{
    /// <summary>What this build writes. Bumped only for a change a older build could MISREAD.</summary>
    /// <remarks>
    /// Additive, nullable members do NOT bump it — an older build ignoring a field it never knew about
    /// is the correct outcome, and bumping for every addition would refuse documents that are perfectly
    /// readable. Only a change in the meaning of an existing field earns a bump.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The oldest schema this build can still read.</summary>
    public const int MinimumSupportedSchemaVersion = 1;

    /// <summary>Written by the app; checked on load against the tolerant-below, closed-above policy.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Title { get; set; } = "";

    public ProjectSettings Settings { get; set; } = new();

    /// <summary>The project's audio vocabulary and its patch to this machine's lines.</summary>
    public ProjectAudioPatch AudioPatch { get; set; } = new();

    /// <summary>
    /// Machine-side audio lines. Project-owned (register item 14): they travel with the show, go
    /// absent on another machine, and relink on arrival — they are never silently swapped for a
    /// default device.
    /// </summary>
    public List<AudioLineDefinition> AudioLines { get; set; } = [];

    /// <summary>Named partial patch states, recalled by patch cues or by hand.</summary>
    public List<PatchSnapshot> PatchSnapshots { get; set; } = [];

    /// <summary>
    /// The audition rig — where preview is heard and seen (register item 15).
    /// </summary>
    /// <remarks>
    /// Always present rather than nullable: a rig with no line named is a valid, working configuration
    /// (it monitors through the bay's default terminal), so "absent" and "unconfigured" would be the
    /// same state expressed two ways.
    /// </remarks>
    public AuditionRig Audition { get; set; } = new();

    public List<CompositionDefinition> Compositions { get; set; } = [];
    public List<VideoOutputDefinition> VideoOutputs { get; set; } = [];
    public List<ActionEndpoint> ActionEndpoints { get; set; } = [];
    public List<TriggerInputDefinition> TriggerInputs { get; set; } = [];
    public List<CueList> CueLists { get; set; } = [];

    /// <summary>Custom fade curves saved as project presets — the library is the preset row itself.</summary>
    public List<CurvePreset> CurvePresets { get; set; } = [];

    /// <summary>Every cue in the project, in list order, flattened through group children.</summary>
    public IEnumerable<CueNode> AllCues() => CueLists.SelectMany(list => list.Flatten());

    /// <summary>The list a cue belongs to, or null if the id is not in this project.</summary>
    public CueList? ListOf(Guid cueId) =>
        CueLists.FirstOrDefault(list => list.Flatten().Any(cue => cue.Id == cueId));

    public CueNode? FindCue(Guid cueId) => AllCues().FirstOrDefault(cue => cue.Id == cueId);

    public LogicalAudioChannel? FindChannel(Guid id) =>
        AudioPatch.LogicalChannels.FirstOrDefault(channel => channel.Id == id);

    public AudioLineDefinition? FindLine(Guid id) =>
        AudioLines.FirstOrDefault(line => line.Id == id);
}

/// <summary>
/// Show behaviour and authoring defaults — the project half of the settings split (screen 13).
/// </summary>
/// <remarks>
/// These are journaled and travel in the file. Machine preferences (theme, density, cache paths) are
/// the OTHER half and live in <c>app-settings.json</c>, outside this model entirely: a show that
/// carried the operator's font size to the next venue would be carrying the wrong thing.
/// </remarks>
public sealed record ProjectSettings
{
    /// <summary>
    /// A show may opt to boot locked, but editing is the default everywhere (register item 2).
    /// </summary>
    public bool OpenLocked { get; set; }

    public bool RunStatusChecksOnOpen { get; set; } = true;

    public AtListEnd AtListEnd { get; set; } = AtListEnd.Hold;

    public int StopFadeMs { get; set; } = 750;
    public CurveSpec StopFadeCurve { get; set; } = new();

    /// <summary>
    /// A project override of the machine's panic fade, or null to inherit it.
    /// </summary>
    /// <remarks>
    /// Nullable is what makes it an OVERRIDE rather than a copy (register item 26). A plain int with a
    /// default cannot express "this project does not care", so every project would silently pin the
    /// value it happened to be created with and the ledger would have nothing to show.
    /// </remarks>
    public int? PanicFadeMs { get; set; }

    /// <summary>The machine default when a project does not override it.</summary>
    public const int DefaultPanicFadeMs = 250;

    /// <summary>The panic fade this project actually uses, given the machine's own default.</summary>
    public int EffectivePanicFadeMs(int machineDefault) => PanicFadeMs ?? machineDefault;

    /// <summary>
    /// Register item 3: external input is off when a project opens. A show that starts answering MIDI
    /// the instant it loads fires cues during a get-in.
    /// </summary>
    public bool ExternalInputOffOnOpen { get; set; } = true;

    /// <summary>
    /// Register item 6, default off: a single click view-selects, and standby moves only on a
    /// double-click or an explicit Stby command. Shows that want QLab-style click-to-target flip it.
    /// </summary>
    public bool ClickMovesStandby { get; set; }

    /// <summary>
    /// D6: what an auto-follow chain does when it reaches a disabled cue. Skipping onward is what an
    /// operator usually means by disabling one cue for one performance; stopping is what the
    /// framework does today. The setting exists because both are defensible and the wrong one is
    /// only discovered mid-show.
    /// </summary>
    public DisabledCueFollow DisabledCueFollow { get; set; } = DisabledCueFollow.SkipOnward;

    /// <summary>
    /// How many upcoming cues have their media opened before anybody presses GO.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The difference between a GO that plays and a GO that opens a file first. Two is enough for the
    /// ordinary case — the next cue and the one after it — and the cost is decoders held open, which is
    /// why it is a number rather than a switch: a booth machine with little memory and 4 K media can
    /// turn it down, and zero turns it off.
    /// </para>
    /// <para>
    /// A PROJECT setting rather than a machine one, because how much a show pre-rolls is a property of
    /// the show's media, and a show carried to another booth should behave the same way there.
    /// </para>
    /// </remarks>
    public int PreRollCount { get; set; } = 2;

    public CueTrigger NewCueTrigger { get; set; } = CueTrigger.Manual;

    /// <summary>
    /// What a new cue's fades start at. ZERO — a cue fades because somebody asked it to.
    /// </summary>
    /// <remarks>
    /// It used to be 100 ms in and 2 s out, which is a reasonable house style and the wrong default: a
    /// cue authored to hit a frame arrives 100 ms late, and one meant to butt-cut into the next
    /// dissolves under it for two seconds. Neither is visible in the cue list, so the operator finds
    /// out by listening. A show that wants a house fade sets it here once, deliberately.
    /// </remarks>
    public int DefaultFadeInMs { get; set; }

    public int DefaultFadeOutMs { get; set; }

    /// <summary>Seeded on from the application-scope New project defaults (register item 20).</summary>
    public bool AutoRenumberOnInsert { get; set; } = true;

    public string MediaRoot { get; set; } = "";

    /// <summary>
    /// Media outside the root is ALLOWED (register item 26). Adding such a file warns and offers
    /// move/copy; this picks the default answer.
    /// </summary>
    public OutsideMediaPolicy OutsideMedia { get; set; } = OutsideMediaPolicy.KeepInPlace;

    public int AutosaveSeconds { get; set; } = 30;
    public int RecoveryCopies { get; set; } = 5;

    /// <summary>
    /// Venue A/V trim in milliseconds: positive shows every picture EARLIER (compensating a
    /// projector/TV chain that displays frames late), negative delays it. Zero for a rig whose
    /// display adds no meaningful latency.
    /// </summary>
    /// <remarks>
    /// A property of the VENUE's display chain, not of any cue — the pipeline keeps picture and
    /// sound aligned at the moment frames leave the machine, but a projector adds one to three
    /// frames of scaling/processing after that, and no software can measure glass from here. One
    /// number, set once at the venue by eye (a clap test), carried in the project because the
    /// project travels to the venue.
    /// </remarks>
    public int AvOffsetMs { get; set; }

    /// <summary>
    /// How long before a Follow cue's out-point its successor is opened, in milliseconds. Zero (the
    /// default) keeps the historical behaviour: the successor is opened only once the current cue has
    /// ended, so its decoder-open time lands AFTER the out-point and pushes the chain late.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A follow chain used to accumulate error per hop, because each hop paid for its own media open
    /// after the edge rather than before it — a cold 4K file is hundreds of milliseconds, and nothing
    /// compensated. With a lead, the successor opens, commits, pre-rolls and presents its
    /// synchronization frame DURING the outgoing clip's last <c>FollowLeadMs</c>, and is then released
    /// on the out-point itself. The instant it starts is unchanged; what changes is that the open no
    /// longer happens inside it.
    /// </para>
    /// <para>
    /// The cost is real and is why this is opt-in: during the lead window the chain holds two decoders
    /// and two producer leases instead of one, so the lead multiplies against the voice budget. Set it
    /// to comfortably exceed the slowest open in the show (2000 is a sane starting point for local
    /// files) rather than to the largest number that fits.
    /// </para>
    /// <para>
    /// Ignored where there is no fixed edge to schedule against or the successor is not a playable
    /// cue — see <c>CueExecutor.TryBeginPreparedFollow</c> for the exact opt-outs.
    /// </para>
    /// </remarks>
    public int FollowLeadMs { get; set; }

    /// <summary>Off by default: a GO is a performance action and a disk write on it is a stall.</summary>
    public bool SaveOnGo { get; set; }

    /// <summary>
    /// A project override of the machine's Remote API default, or null to inherit it. The overridable
    /// set is frozen at panic fade, remote API and hotkeys (register item 26), and an override always
    /// wins AND is always visible in both scopes.
    /// </summary>
    public RemoteApiOverride? RemoteApi { get; set; }
}

public enum AtListEnd
{
    Hold,
    Loop,
    NextList,
}

public enum DisabledCueFollow
{
    /// <summary>Skip the disabled cue and continue the chain.</summary>
    SkipOnward,

    /// <summary>Stop the chain at the disabled cue — today's framework behaviour.</summary>
    StopTheChain,
}

public enum OutsideMediaPolicy
{
    KeepInPlace,
    MoveToRoot,
    CopyToRoot,
}

public sealed record RemoteApiOverride
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8420;
    public bool LanAllowed { get; set; }
}
