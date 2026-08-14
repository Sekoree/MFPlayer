using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Compile;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;
using HaCue2.Session;
using S.Media.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// What a per-pane editor may see of the inspector (review F-11's facade seam): the live selection,
/// the running show for hot edits, and the ONE refresh entry point. Editors write only through the
/// journal they are constructed with - the inspector's undo guarantee is not theirs to weaken.
/// </summary>
public interface IInspectorEditorContext
{
    HaCueProject Project { get; }

    /// <summary>The lead cue - the one whose values single-selection fields show.</summary>
    CueNode? Cue { get; }

    IReadOnlyList<CueNode> Selected { get; }

    /// <summary>The running show, used only for hot edits while a cue is sounding.</summary>
    ShowHost? Host { get; }

    /// <summary>The inspector-wide refresh (tab set, titles, every pane's projections).</summary>
    void Reload();
}

/// <summary>
/// The Audio pane: the cue-send matrix, its presets, the effective-route readout, and the live
/// pushes that keep a sounding cue's mix following the edit. Extracted from the 3.9k-line
/// <c>InspectorViewModel</c> as review F-11's exemplar - one feature, one owner, testable against
/// a fake <see cref="IInspectorEditorContext"/>, with the journal as the only write path.
/// </summary>
public sealed partial class AudioPaneEditor(ProjectJournal journal, IInspectorEditorContext context)
    : ObservableObject
{
    /// <summary>Open for the duration of one gain drag, so the gesture is a single undo step.
    /// The pane owns its own drag scope - a pointer can only drag one thing at a time.</summary>
    private IDisposable? _drag;

    private HaCueProject Project => context.Project;
    private CueNode? Cue => context.Cue;
    private IReadOnlyList<CueNode> Selected => context.Selected;

    public IReadOnlyList<MatrixColumn> SendColumns => AudioPresentation.SendColumns(Project);

    public IReadOnlyList<MatrixRow> SendRows => Cue is MediaCueNode media
        ? AudioPresentation.SendRows(Project, media, picked: 1)
        : [];

    /// <summary>
    /// The effective route for the picked source channel, read from the middle.
    /// </summary>
    /// <remarks>
    /// This is the answer to "why is this silent" and to "why is it coming out twice", and it is
    /// computed from the two matrices rather than described - so it cannot disagree with them.
    /// </remarks>
    public IReadOnlyList<RouteHop> RouteChain => Cue is MediaCueNode media
        ? AudioPresentation.RouteChain(Project, media, sourceChannel: 1)
        : [];

    public bool HasRoute => RouteChain.Count > 0;

    /// <summary>Raises every projection off the current selection - the inspector's
    /// <c>Reload</c> calls this instead of naming the pane's properties itself.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(SendColumns));
        OnPropertyChanged(nameof(SendRows));
        OnPropertyChanged(nameof(SendPresetTarget));
        OnPropertyChanged(nameof(HasSendPresetTarget));
        OnPropertyChanged(nameof(RouteChain));
        OnPropertyChanged(nameof(HasRoute));
    }

    /// <summary>
    /// Applies one pointer gesture to this cue's sends - the same click/drag/right-click as the patch.
    /// </summary>
    /// <remarks>
    /// No group linking here. An Output Group links the PATCH, where a stereo pair's two cells are the
    /// same trim on the same speaker system; a cue's sends are where the operator decides what goes
    /// where, and mirroring one into its partner would undo that decision.
    /// </remarks>
    public void ApplySendGesture(MatrixGesture gesture)
    {
        // EVERY selected media cue, not just the lead. Routing a stem group to a different logical
        // output is the archetypal multi-selection edit, and this pane silently applied it to the first
        // row only - leaving the other ten cues on the old send with nothing on screen saying so.
        //
        // The lead still decides WHAT the gesture means (which cell, whether it is being added or
        // removed, which direction a mute toggles), because that is what the operator clicked on. The
        // others follow it, so the whole selection ends up in the state the lead's cell now shows
        // rather than each cue toggling its own way and the matrix reading "mixed" afterwards.
        if (Cue is not MediaCueNode lead
            || gesture.Row >= SendRows.Count
            || gesture.Column >= SendColumns.Count)
            return;

        var source = SendRows[gesture.Row].LineChannel;
        var channelId = SendColumns[gesture.Column].ChannelId;
        var targets = Selected.OfType<MediaCueNode>().ToList();

        CueAudioSend? SendOn(MediaCueNode cue) => cue.Sends.FirstOrDefault(
            send => send.SourceChannel == source && send.LogicalChannelId == channelId);

        var existing = SendOn(lead);

        switch (gesture.Kind)
        {
            case MatrixGestureKind.Toggle:
            {
                var routing = existing is null;
                Each(routing ? "route send at unity" : "remove send", (cue, current) =>
                    new SetCueSendCommand(
                        cue, source, channelId,
                        routing ? 0 : null,
                        routing ? false : null,
                        routing ? "route send at unity" : "remove send"));
                journal.CloseGroup();
                break;
            }

            case MatrixGestureKind.Adjust when existing is not null:
                // A drag emits one command per pointer sample. Quiet, so the shell reacts once on
                // release instead of re-probing and re-refreshing the whole project per pixel.
                _drag ??= journal.Composite("adjust send gain", "cues", quiet: true);
                Each("set send gain", (cue, current) => current is null
                    ? null
                    : new SetCueSendCommand(
                        cue, source, channelId, current.GainDb + gesture.DeltaDb, current.Muted,
                        "set send gain"));
                PushLiveSends();
                OnPropertyChanged(nameof(SendRows));
                OnPropertyChanged(nameof(RouteChain));
                return;

            case MatrixGestureKind.Mute when existing is not null:
            {
                var muting = !existing.Muted;
                Each(muting ? "mute send" : "unmute send", (cue, current) => current is null
                    ? null
                    : new SetCueSendCommand(
                        cue, source, channelId, current.GainDb, muting,
                        muting ? "mute send" : "unmute send"));
                journal.CloseGroup();
                break;
            }
        }

        PushLiveSends();
        context.Reload();
        return;

        void Each(string description, Func<MediaCueNode, CueAudioSend?, SetCueSendCommand?> build)
        {
            if (targets.Count > 1)
            {
                using (journal.Composite($"{description} on {targets.Count} cues", "cues"))
                    foreach (var cue in targets)
                    {
                        if (build(cue, SendOn(cue)) is { } command)
                            journal.Do(command);
                    }

                return;
            }

            if (build(lead, existing) is { } single)
                journal.Do(single);
        }
    }

    // ── send presets ──────────────────────────────────────────────────────────────────────────
    //
    // Screen 05 has always shown a PRESETS strip reading "stereo → Main · mono from L · swap · clear".
    // It was a caption: a Border with a TextBlock in it and nothing behind either, so the four things
    // it names could not be clicked and never happened. HaPlay's cue player had the feature; this is it.
    //
    // They exist because the matrix is the slow way to do the four routings almost every cue wants.
    // Eleven stems into Main is twenty-two clicks placed exactly right, and the commonest authoring
    // mistake in a cue player is a stem that is quietly mono into one side because one of them missed.

    /// <summary>The pair of logical channels a stereo preset targets, in sort order.</summary>
    /// <remarks>
    /// The first Output GROUP with at least two members, because that is what a stereo pair IS in this
    /// document (register item 9) - falling back to the first two logical channels for a project that
    /// never grouped anything. Named rather than assumed so the button can say where it is sending.
    /// </remarks>
    private IReadOnlyList<LogicalAudioChannel> StereoTarget
    {
        get
        {
            var patch = Project.AudioPatch;
            var ordered = patch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

            var pair = patch.Groups
                .Select(group => group.MemberIds
                    .Select(id => ordered.FirstOrDefault(channel => channel.Id == id))
                    .OfType<LogicalAudioChannel>()
                    .OrderBy(channel => channel.SortOrder)
                    .ToList())
                .FirstOrDefault(members => members.Count >= 2);

            return pair ?? [.. ordered.Take(2)];
        }
    }

    /// <summary>Where the stereo presets would send - on the buttons, so nobody has to guess.</summary>
    public string SendPresetTarget => StereoTarget switch
    {
        { Count: >= 2 } pair => $"{pair[0].Name} · {pair[1].Name}",
        { Count: 1 } single => single[0].Name,
        _ => "no logical outputs",
    };

    /// <summary>False on a project with nothing to send TO - the buttons say so rather than no-op.</summary>
    public bool HasSendPresetTarget => StereoTarget.Count >= 2;

    /// <summary>
    /// Applies one send preset to every selected media cue.
    /// </summary>
    /// <remarks>
    /// Written as "replace this cue's sends with exactly these", not as a series of toggles: a preset
    /// whose result depended on what was already routed would give two cues in one selection different
    /// answers, which is the whole failure the presets exist to avoid.
    /// </remarks>
    public void ApplySendPreset(string preset)
    {
        var targets = Selected.OfType<MediaCueNode>().ToList();
        if (targets.Count == 0)
            return;

        var pair = StereoTarget;
        if (preset != "clear" && pair.Count < 2)
            return;

        // (source channel, logical channel) at unity. Empty means "route nothing".
        IReadOnlyList<(int Source, Guid Channel)> wanted = preset switch
        {
            "stereo" => [(0, pair[0].Id), (1, pair[1].Id)],
            // One channel to both sides, so a mono stem sits in the middle instead of hard left.
            "monoL" => [(0, pair[0].Id), (0, pair[1].Id)],
            "swap" => [(1, pair[0].Id), (0, pair[1].Id)],
            "clear" => [],
            _ => [],
        };

        if (preset is not ("stereo" or "monoL" or "swap" or "clear"))
            return;

        var description = preset switch
        {
            "stereo" => $"stereo → {SendPresetTarget}",
            "monoL" => $"mono from L → {SendPresetTarget}",
            "swap" => "swap L/R sends",
            _ => "clear sends",
        };

        using (journal.Composite(
                   targets.Count > 1 ? $"{description} on {targets.Count} cues" : description, "cues"))
        {
            foreach (var cue in targets)
            {
                // Remove what the preset does not want FIRST, so a swap cannot momentarily double up
                // on a channel and so "clear" is simply the empty case of the same operation.
                foreach (var send in cue.Sends.ToList())
                {
                    if (wanted.Any(want =>
                            want.Source == send.SourceChannel && want.Channel == send.LogicalChannelId))
                        continue;

                    journal.Do(new SetCueSendCommand(
                        cue, send.SourceChannel, send.LogicalChannelId, null, null, description));
                }

                foreach (var (source, channel) in wanted)
                    journal.Do(new SetCueSendCommand(cue, source, channel, 0, false, description));
            }
        }

        journal.CloseGroup();
        PushLiveSends();
        context.Reload();
    }

    /// <summary>Closes the send-gain drag's undo step, on pointer release.</summary>
    public void EndSendGesture()
    {
        _drag?.Dispose();
        _drag = null;
        journal.CloseGroup();
        context.Reload();
    }

    /// <summary>
    /// Re-applies the selection's sends to whatever is currently sounding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send or level edit changes the cue's clip binding, so the engine will not adopt it while that
    /// cue is playing - a reload would restart the cue, which on a group of stems is a pop and eleven
    /// re-opened files for one fader move. The running voice gets the new matrix directly instead, and
    /// the document catches up when the show is idle.
    /// </para>
    /// <para>
    /// Best-effort on every count. An idle cue simply has no active voice and the session says so; the
    /// authored value stands either way.
    /// </para>
    /// </remarks>
    /// <summary>Public because the General pane's level field pushes the same mix snapshot - cue
    /// volume and per-send trim are two gain stages of ONE live update.</summary>
    public void PushLiveSends()
    {
        if (context.Host is not { } host)
            return;

        foreach (var media in Selected.OfType<MediaCueNode>())
            _liveSends.Offer(
                new LiveSendKey(host, media.Id),
                new LiveCueMix(ShowCompiler.LogicalSends(media), media.LevelDb));
    }

    /// <summary>
    /// Serializes live send pushes per cue, keeping only the newest.
    /// </summary>
    /// <remarks>
    /// A gain drag emits one of these per pointer sample and each crosses the session dispatcher, which
    /// is the same thread playback runs its commands on. Queueing every sample makes the fader lag the
    /// mouse and starves the show; the publisher lets one finish and replaces the rest.
    /// </remarks>
    private readonly LatestOnlyPublisher<LiveSendKey, LiveCueMix> _liveSends =
        new(
            static async (key, mix) =>
            {
                await key.Host.ApplyActiveSendsAsync(key.CueId, mix.Sends).ConfigureAwait(false);
                await key.Host.ApplyActiveVolumeAsync(key.CueId, mix.LevelDb).ConfigureAwait(false);
            },
            TimeSpan.FromMilliseconds(33),
            static failure => System.Diagnostics.Trace.TraceWarning(
                $"Live send update failed: {failure.GetType().Name}: {failure.Message}"));

    private readonly record struct LiveSendKey(ShowHost Host, Guid CueId);
    private readonly record struct LiveCueMix(IReadOnlyList<ShowClipLogicalSend> Sends, double LevelDb);
}
