using System.Text.Json.Serialization;
using S.Control;

namespace HaPlay.Models;

// Serialized model. Non-CLR-default properties use `set`, not `init`: the source-generated
// serializer assigns EVERY init property through one object initializer, so a field absent from
// the JSON would load as the CLR default instead of the property initializer (see the FadeCueNode
// doc note in CueList.cs). `init` remains only where the initializer IS the CLR default.

/// <summary>
/// Top-level project file (§7 of the UI refactor plan). Captures every persistable piece of a HaPlay
/// session in one file so a show is one save/open away. Cue lists (§5) live under
/// <see cref="CueLists"/>. UI layout state intentionally lives in a
/// per-machine sidecar (not part of this record).
/// </summary>
public sealed record HaPlayProject
{
    /// <summary>Bump on every breaking field change so the loader can migrate (§9.4).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Best-effort app version stamp - informational only.</summary>
    public string? HaPlayVersion { get; init; }

    /// <summary>
    /// Sections this file actually carries (see <see cref="ProjectSections"/>). <c>null</c> = full
    /// project (every pre-existing file). Loading applies only the listed sections, so partial
    /// files double as section exports/imports.
    /// </summary>
    public List<string>? SavedSections { get; init; }

    /// <summary>All output definitions in display order. Identity is <see cref="OutputDefinition.Id"/>.</summary>
    public List<OutputDefinition> Outputs { get; set; } = new();

    /// <summary>Legacy (pre-UI-rewrite-P2): the virtual-audio-channel model was removed in favor of
    /// output aliases + matrix presets. Kept only so old project files deserialize; ignored on load
    /// (a one-time migration toast tells the operator) and written empty on save.</summary>
    public List<VirtualAudioChannelAssignment> VirtualAudioChannels { get; set; } = new();

    /// <summary>Per-player config (§4.5 will split this; Phase A keeps the existing <see cref="MediaPlayerConfig"/> shape).</summary>
    public List<MediaPlayerConfig> Players { get; set; } = new();

    public List<ActionEndpoint> ActionEndpoints { get; set; } = new();

    public List<CueList> CueLists { get; set; } = new();

    /// <summary>Soundboard tabs (touch-friendly sound clip grids).</summary>
    public List<SoundboardConfig> Soundboards { get; set; } = new();

    public List<ControlGraphConfig> ControlGraphs { get; set; } = new();

    public ControlSystemConfig ControlSystem { get; set; } = new();

    /// <summary>
    /// Per-project session-restore setting (default off). When on, HaPlay writes edits through to this
    /// project's file on the crash-recovery cadence (true autosave); when off, only a crash-recovery
    /// duplicate is kept in the cache and the file is written solely by an explicit Save. Additive/optional -
    /// old files (no field) load as <see langword="false"/>, which is why the schema version is not bumped.
    /// </summary>
    public bool AutoSaveEnabled { get; init; }

    /// <summary>
    /// Operator's cue preview (audition) device by PortAudio device name. "" = an explicit "Default
    /// device" pick; null (older files / never picked) = the picker derives its preselect from the
    /// first configured PortAudio cue output line. Document-level like <see cref="AutoSaveEnabled"/> -
    /// partial section imports leave the live choice untouched. Additive/optional, so no schema bump.
    /// </summary>
    public string? CuePreviewAudioDeviceName { get; init; }

    /// <summary>Constant for callers that want to write SchemaVersion explicitly.</summary>
    public const int CurrentSchemaVersion = 3;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HaPlayProject))]
[JsonSerializable(typeof(OutputDefinition))]
[JsonSerializable(typeof(PortAudioOutputDefinition))]
[JsonSerializable(typeof(LocalVideoOutputDefinition))]
[JsonSerializable(typeof(NDIOutputDefinition))]
[JsonSerializable(typeof(VirtualAudioChannelAssignment))]
[JsonSerializable(typeof(MediaPlayerConfig))]
[JsonSerializable(typeof(PlaylistConfig))]
[JsonSerializable(typeof(OutputGainConfig))]
[JsonSerializable(typeof(InputChannelTrimConfig))]
[JsonSerializable(typeof(ActionEndpoint))]
[JsonSerializable(typeof(OSCActionEndpoint))]
[JsonSerializable(typeof(MIDIActionEndpoint))]
[JsonSerializable(typeof(SoundboardConfig))]
[JsonSerializable(typeof(SoundboardTileConfig))]
[JsonSerializable(typeof(SoundboardsCollectionDocument))]
[JsonSerializable(typeof(CueList))]
[JsonSerializable(typeof(CueNode))]
[JsonSerializable(typeof(CueGroupNode))]
[JsonSerializable(typeof(CuePlaylistOptions))]
[JsonSerializable(typeof(MediaCueNode))]
[JsonSerializable(typeof(CueSubtitleSelection))]
[JsonSerializable(typeof(ActionCueNode))]
[JsonSerializable(typeof(CommentCueNode))]
[JsonSerializable(typeof(JumpCueNode))]
[JsonSerializable(typeof(VisualizerCueNode))]
[JsonSerializable(typeof(FadeCueNode))]
[JsonSerializable(typeof(CueComposition))]
[JsonSerializable(typeof(CueVideoOutputBinding))]
[JsonSerializable(typeof(CueAudioRoute))]
[JsonSerializable(typeof(CueVideoPlacement))]
[JsonSerializable(typeof(CueChromaKey))]
[JsonSerializable(typeof(CueColorAdjust))]
[JsonSerializable(typeof(CueSchedule))]
[JsonSerializable(typeof(CueTriggerBinding))]
[JsonSerializable(typeof(CueAutomationPoint))]
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(FilePlaylistItem))]
[JsonSerializable(typeof(NDIInputPlaylistItem))]
[JsonSerializable(typeof(PortAudioInputPlaylistItem))]
[JsonSerializable(typeof(ImagePlaylistItem))]
[JsonSerializable(typeof(SubtitlePlaylistItem))]
[JsonSerializable(typeof(TextPlaylistItem))]
[JsonSerializable(typeof(YouTubePlaylistItem))]
[JsonSerializable(typeof(MMDPlaylistItem))]
[JsonSerializable(typeof(ControlGraphConfig))]
[JsonSerializable(typeof(ControlNodeConfig))]
[JsonSerializable(typeof(ControlConnectionConfig))]
[JsonSerializable(typeof(ControlNodeSettings))]
[JsonSerializable(typeof(PassthroughControlNodeSettings))]
[JsonSerializable(typeof(MIDIInputControlNodeSettings))]
[JsonSerializable(typeof(OSCInputControlNodeSettings))]
[JsonSerializable(typeof(MapRangeControlNodeSettings))]
[JsonSerializable(typeof(ScriptTransformControlNodeSettings))]
[JsonSerializable(typeof(OSCOutputControlNodeSettings))]
[JsonSerializable(typeof(MIDIOutputControlNodeSettings))]
[JsonSerializable(typeof(X32ChannelFaderControlNodeSettings))]
[JsonSerializable(typeof(X32CustomLayerConfig))]
[JsonSerializable(typeof(X32CustomLayerSlotConfig))]
[JsonSerializable(typeof(ControlSystemConfig))]
[JsonSerializable(typeof(ControlDeviceProfile))]
[JsonSerializable(typeof(ControlDevicePortProfile))]
[JsonSerializable(typeof(ControlControlProfile))]
[JsonSerializable(typeof(ControlCommandProfile))]
[JsonSerializable(typeof(ControlLayerProfile))]
[JsonSerializable(typeof(ControlDeviceTaskProfile))]
[JsonSerializable(typeof(ControlDeviceProfileBehaviors))]
[JsonSerializable(typeof(ControlOSCListenerConfig))]
[JsonSerializable(typeof(ControlMonitorOptions))]
[JsonSerializable(typeof(ControlDeviceInstanceConfig))]
[JsonSerializable(typeof(ControlDeviceBindingConfig))]
[JsonSerializable(typeof(ControlPeriodicOSCSendConfig))]
[JsonSerializable(typeof(ControlOSCArgumentConfig))]
[JsonSerializable(typeof(ControlLayerConfig))]
[JsonSerializable(typeof(ControlScriptConfig))]
[JsonSerializable(typeof(ControlScriptFailurePolicy))]
[JsonSerializable(typeof(ControlScriptTriggerConfig))]
internal partial class HaPlayProjectJsonContext : JsonSerializerContext;
