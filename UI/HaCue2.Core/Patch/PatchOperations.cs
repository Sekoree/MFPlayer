using HaCue2.Core.Model;

namespace HaCue2.Core.Patch;

/// <summary>
/// One complete path from a cue's source channel to a real device channel.
/// </summary>
/// <remarks>
/// The answer to "why is this silent" and "why is it coming out twice", which otherwise requires
/// mentally multiplying the send matrix by the patch matrix.
/// </remarks>
public sealed record EffectiveRoute(
    int SourceChannel,
    Guid LogicalChannelId,
    string LogicalName,
    Guid LineId,
    string LineName,
    int LineChannel,
    double GainDb,
    bool Muted)
{
    /// <summary>Whether anything actually arrives: not muted, and above the silence floor.</summary>
    public bool IsAudible => !Muted && GainDb > PatchOperations.SilenceFloorDb;
}

/// <summary>A cell a recall could not apply, and why.</summary>
public sealed record BrokenBinding(Guid LogicalChannelId, Guid LineId, int LineChannel, string Reason);

/// <summary>What a recall did.</summary>
public sealed record PatchRecallResult(int CellsApplied, IReadOnlyList<BrokenBinding> Broken)
{
    public bool IsClean => Broken.Count == 0;
}

/// <summary>
/// Pure operations over the two matrices: the N×V sends and the V×R patch.
/// </summary>
/// <remarks>
/// Nothing here is journaled. Recall is an operator action on the LIVE patch, not a document edit -
/// undo means "un-edit my document", never "un-recall my snapshot". Editing a snapshot's stored cells
/// is a document edit and goes through the journal like anything else.
/// </remarks>
public static class PatchOperations
{
    /// <summary>Re-exported from the model so callers need only one namespace.</summary>
    public const double SilenceFloorDb = GainRange.SilenceFloorDb;

    /// <summary>
    /// Multiplies a cue's sends by the project patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gains ADD because they are decibels - the dB sum is the linear product of the two stages, which
    /// is what composing two gain stages means.
    /// </para>
    /// <para>
    /// No normalization is applied where several routes land on the same device channel. Summing is
    /// the operator's decision and meters make the result visible; silently inserting a limiter would
    /// change what the show sounds like without saying so.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<EffectiveRoute> EffectiveRoutes(
        HaCueProject project, MediaCueNode cue, int? sourceChannel = null)
    {
        var routes = new List<EffectiveRoute>();

        foreach (var send in cue.Sends)
        {
            if (sourceChannel is { } only && send.SourceChannel != only)
                continue;

            var channel = project.FindChannel(send.LogicalChannelId);
            if (channel is null)
                continue;

            foreach (var cell in project.AudioPatch.Cells
                         .Where(cell => cell.LogicalChannelId == send.LogicalChannelId))
            {
                var line = project.FindLine(cell.LineId);
                routes.Add(new EffectiveRoute(
                    send.SourceChannel,
                    channel.Id,
                    channel.Name,
                    cell.LineId,
                    line?.Name ?? "(absent)",
                    cell.LineChannel,
                    send.GainDb + cell.GainDb,
                    send.Muted || cell.Muted));
            }
        }

        return routes;
    }

    /// <summary>Logical outputs a cue feeds, whether or not they reach hardware.</summary>
    public static IReadOnlyList<LogicalAudioChannel> DestinationsOf(HaCueProject project, MediaCueNode cue) =>
    [
        .. cue.Sends
            .Select(send => project.FindChannel(send.LogicalChannelId))
            .OfType<LogicalAudioChannel>()
            .DistinctBy(channel => channel.Id),
    ];

    /// <summary>
    /// Recalls a snapshot onto the live patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Partial:</b> only the cells the snapshot stores are written. Everything else keeps its
    /// current value, so two patch cues can own disjoint parts of the house without undoing each
    /// other. A whole-console reset would make the second recall wipe the first.
    /// </para>
    /// <para>
    /// <b>Idempotent and re-firable:</b> recalling twice lands on the same state, because each cell is
    /// SET to the stored value rather than nudged toward it.
    /// </para>
    /// <para>
    /// <b>A broken cell is skipped and reported, never slid onto a neighbour.</b> The rest of the
    /// snapshot still applies: refusing a whole recall mid-show because one channel was renamed would
    /// leave the operator with neither the old state nor the new one.
    /// </para>
    /// </remarks>
    public static PatchRecallResult Recall(HaCueProject project, Guid snapshotId)
    {
        var snapshot = project.PatchSnapshots.FirstOrDefault(snapshot => snapshot.Id == snapshotId);
        if (snapshot is null)
            return new PatchRecallResult(0, [new BrokenBinding(Guid.Empty, Guid.Empty, 0, "the snapshot no longer exists")]);

        var broken = new List<BrokenBinding>();
        var applied = 0;

        foreach (var stored in snapshot.Cells)
        {
            if (Reject(project, stored.LogicalChannelId, stored.LineId, stored.LineChannel) is { } reason)
            {
                broken.Add(new BrokenBinding(
                    stored.LogicalChannelId, stored.LineId, stored.LineChannel, reason));
                continue;
            }

            Write(project.AudioPatch, stored.LogicalChannelId, stored.LineId, stored.LineChannel,
                stored.GainDb, stored.Muted);
            applied++;
        }

        return new PatchRecallResult(applied, broken);
    }

    /// <summary>
    /// Applies a patch cue's inline level changes to the live patch.
    /// </summary>
    /// <remarks>
    /// A change with no line means "every cell fed by this logical output"; a line with no channel
    /// means "every cell on that line". That is how "Fold L/R up 6 dB" stays one entry when the
    /// foldback is patched to four places - and why a widened change that currently matches nothing is
    /// reported rather than treated as success.
    /// </remarks>
    public static PatchRecallResult ApplyLevels(
        HaCueProject project, IReadOnlyList<PatchLevelChange> changes)
    {
        var broken = new List<BrokenBinding>();
        var applied = 0;

        foreach (var change in changes)
        {
            if (project.FindChannel(change.LogicalChannelId) is null)
            {
                broken.Add(new BrokenBinding(change.LogicalChannelId, change.LineId ?? Guid.Empty,
                    change.LineChannel ?? 0, "the logical output no longer exists"));
                continue;
            }

            if (change.LineId is { } lineId && project.FindLine(lineId) is null)
            {
                broken.Add(new BrokenBinding(change.LogicalChannelId, lineId, change.LineChannel ?? 0,
                    "the audio line no longer exists"));
                continue;
            }

            var targets = project.AudioPatch.Cells
                .Where(cell => cell.LogicalChannelId == change.LogicalChannelId)
                .Where(cell => change.LineId is null || cell.LineId == change.LineId)
                .Where(cell => change.LineChannel is null || cell.LineChannel == change.LineChannel)
                .ToList();

            if (targets.Count == 0)
            {
                broken.Add(new BrokenBinding(change.LogicalChannelId, change.LineId ?? Guid.Empty,
                    change.LineChannel ?? 0, "no patch cell matches this change"));
                continue;
            }

            foreach (var cell in targets)
            {
                cell.GainDb = change.GainDb;
                cell.Muted = change.Muted;
                applied++;
            }
        }

        return new PatchRecallResult(applied, broken);
    }

    /// <summary>Captures the current patch as snapshot cells - all of it, or a chosen subset.</summary>
    public static List<PatchCell> Capture(HaCueProject project, IReadOnlyCollection<Guid>? channelIds = null) =>
    [
        .. project.AudioPatch.Cells
            .Where(cell => channelIds is null || channelIds.Contains(cell.LogicalChannelId))
            // Copies, not the live cells: a snapshot that aliased the patch would follow every later
            // edit and stop being a snapshot.
            .Select(cell => cell with { }),
    ];

    /// <summary>The stereo-identity preset: channel n of the pair to channel n of the line.</summary>
    public static List<PatchCell> StereoIdentity(
        IReadOnlyList<Guid> channelIds, Guid lineId, int firstLineChannel = 0) =>
    [
        .. channelIds.Select((channelId, index) => new PatchCell
        {
            LogicalChannelId = channelId,
            LineId = lineId,
            LineChannel = firstLineChannel + index,
        }),
    ];

    /// <summary>The fan-out preset: one logical output onto several lines at unity.</summary>
    public static List<PatchCell> FanOut(Guid channelId, IReadOnlyList<(Guid LineId, int Channel)> targets) =>
    [
        .. targets.Select(target => new PatchCell
        {
            LogicalChannelId = channelId,
            LineId = target.LineId,
            LineChannel = target.Channel,
        }),
    ];

    private static string? Reject(HaCueProject project, Guid channelId, Guid lineId, int lineChannel)
    {
        if (project.FindChannel(channelId) is null)
            return "the logical output no longer exists";

        var line = project.FindLine(lineId);
        if (line is null)
            return "the audio line no longer exists";

        return lineChannel < 0 || lineChannel >= line.Channels
            ? $"the line has only {line.Channels} channel{(line.Channels == 1 ? "" : "s")}"
            : null;
    }

    private static void Write(
        ProjectAudioPatch patch, Guid channelId, Guid lineId, int lineChannel, double gainDb, bool muted)
    {
        var cell = patch.Cells.FirstOrDefault(cell => cell.Matches(channelId, lineId, lineChannel));

        if (cell is null)
        {
            patch.Cells.Add(new PatchCell
            {
                LogicalChannelId = channelId,
                LineId = lineId,
                LineChannel = lineChannel,
                GainDb = gainDb,
                Muted = muted,
            });
            return;
        }

        cell.GainDb = gainDb;
        cell.Muted = muted;
    }
}
