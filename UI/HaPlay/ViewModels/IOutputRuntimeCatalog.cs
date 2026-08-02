using System.Collections.ObjectModel;
using HaPlay.OutputPreview;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Routing;

namespace HaPlay.ViewModels;

/// <summary>
/// The output engine as its consumers actually use it: acquire a line, release it, wrap its effects, and
/// be told when the topology changed.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="OutputManagementViewModel"/> (1,800+ lines), which six call sites were taking
/// a direct dependency on. Everything here is the RUNTIME half - the transactional acquire/release
/// protocol, the reconfiguration hooks and the health probes. The half that is not here is just as
/// deliberate: the add/edit commands, the dialogs they open and the window ownership they need are the
/// view model's own business, and a second app has no use for them.
/// </para>
/// <para>
/// This is a seam, not yet a boundary. <see cref="Outputs"/> and several methods still traffic in
/// <see cref="OutputLineViewModel"/>, so an alternative implementation would still be sharing HaPlay's line
/// type. Removing that means untangling the line view model's own dependencies (app <c>Strings</c>, the
/// dialogs) and is its own step - stating the consumed surface first is what makes that step visible and
/// finite rather than a guess about a 1,800-line class.
/// </para>
/// </remarks>
internal interface IOutputRuntimeCatalog
{
    /// <summary>Every configured output line.</summary>
    ObservableCollection<OutputLineViewModel> Outputs { get; }

    /// <summary>An immutable snapshot of the persisted definitions.</summary>
    IReadOnlyList<OutputDefinition> DefinitionsSnapshot { get; }

    /// <summary>The clone lines that mirror <paramref name="parentId"/>.</summary>
    IEnumerable<OutputLineViewModel> GetClonesOf(Guid parentId);

    // --- transactional acquire / release ------------------------------------------------------------

    /// <summary>Acquires a line's video output for playback, or null when it cannot be held.</summary>
    IVideoOutput? AcquireVideoOutputForLine(Guid lineId);

    /// <summary>Releases what <see cref="AcquireVideoOutputForLine"/> took. Safe on a line never held.</summary>
    void ReleaseVideoOutputForLine(Guid lineId);

    /// <summary>Acquires a line's audio output for playback, or null when it cannot be held.</summary>
    IAudioOutput? AcquireAudioOutputForLine(Guid lineId);

    /// <summary>Releases what <see cref="AcquireAudioOutputForLine"/> took.</summary>
    void ReleaseAudioOutputForLine(Guid lineId);

    /// <summary>Takes a shared lease on a PortAudio line.</summary>
    SharedAudioOutputLease? TryAcquirePortAudioByLineId(Guid lineId, bool liveMonitoring = false);

    /// <summary>Takes an encode line's audio sink.</summary>
    IAudioOutput? TryAcquireEncodeAudioByLineId(Guid lineId);

    /// <summary>Releases an encode line's audio sink.</summary>
    void ReleaseEncodeAudioByLineId(Guid lineId);

    /// <summary>Takes a local preview window's video output for playback.</summary>
    IVideoOutput? TryAcquireLocalVideoOutputForPlayback(OutputLineViewModel line);

    /// <summary>Releases a local preview window taken for playback.</summary>
    void ReleaseLocalVideoOutputForPlayback(OutputLineViewModel line);

    /// <summary>Takes an NDI carrier, declaring which halves of it are needed.</summary>
    /// <remarks>Video and audio are declared separately because one carrier serves both and a caller that
    /// wants only one must not release the other out from under its partner.</remarks>
    NDIOutput? TryAcquireNDICarrierForPlayback(OutputLineViewModel line, bool needsVideo, bool needsAudio);

    /// <summary>Releases the halves of an NDI carrier this caller took.</summary>
    void ReleaseNDICarrierForPlayback(OutputLineViewModel line, bool releaseVideo = true, bool releaseAudio = true);

    /// <summary>Sets (or clears) the idle logo an NDI carrier shows when nothing is playing.</summary>
    void SetNDICarrierLogo(OutputLineViewModel line, VideoFrame? logoFrame);

    /// <summary>Stops any preview using these lines, so playback can take them.</summary>
    void StopPreviewsForPlayback(IEnumerable<OutputLineViewModel> lines);

    /// <summary>Wraps a line's audio in its configured effect chain.</summary>
    IAudioOutput WrapAudioEffectsForLine(Guid lineId, IAudioOutput inner, bool disposeInner = false);

    // --- change notification ------------------------------------------------------------------------

    /// <summary>Raised when routing topology changes (a line added, removed or re-targeted).</summary>
    event EventHandler? RoutingTopologyChanged;

    /// <summary>Raised when a line's display name changes.</summary>
    event EventHandler? OutputNamingChanged;

    /// <summary>Raised before a line is reconfigured, so holders can let it go.</summary>
    event Func<OutputLineViewModel, Task>? OutputLineReconfiguringAsync;

    /// <summary>Raised after a line is reconfigured, so holders can re-acquire.</summary>
    event Func<OutputLineViewModel, Task>? OutputLineReconfiguredAsync;

    // --- probes the host installs -------------------------------------------------------------------

    /// <summary>Supplies the live decks, for health and "is anything using this line" checks.</summary>
    Func<IReadOnlyList<MediaPlayerViewModel>>? ActivePlayersProbe { get; set; }

    /// <summary>Supplies per-line cue metrics for the health scorer.</summary>
    Func<Guid, Playback.OutputLineHealthEvaluator.LineHealthMetrics?>? CueLineMetricsProbe { get; set; }
}
