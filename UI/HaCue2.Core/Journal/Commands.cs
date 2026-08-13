using HaCue2.Core.Model;

namespace HaCue2.Core.Journal;

/// <summary>
/// Sets one property of one document object, and puts the old value back.
/// </summary>
/// <typeparam name="T">The property's type.</typeparam>
/// <remarks>
/// <para>
/// The workhorse: every scalar edit in the app is one of these. It coalesces, so a fader drag or a
/// spinner hold is a single undo step.
/// </para>
/// <para>
/// <b>Invariant this relies on:</b> commands move the SAME object instances in and out of the
/// document, never copies. That is what keeps the accessors here valid across an undo - a command
/// that cloned a cue on removal would leave every closure pointing at an orphan, and the next undo
/// would edit a cue nobody can see.
/// </para>
/// </remarks>
public sealed class SetValueCommand<T> : ICoalescingCommand
{
    private readonly Func<T> _read;
    private readonly Action<T> _write;
    private readonly T _before;
    private T _after;

    public SetValueCommand(
        Guid subject,
        string property,
        string domain,
        Func<T> read,
        Action<T> write,
        T value,
        string? description = null)
    {
        _read = read;
        _write = write;
        _before = read();
        _after = value;
        Key = new CoalesceKey(subject, property);
        Domain = domain;
        Description = description ?? $"set {property} {Format(_before)} → {Format(value)}";
    }

    public CoalesceKey Key { get; }
    public string Domain { get; }
    public string Description { get; private set; }

    public void Apply(HaCueProject project) => _write(_after);

    public void Revert(HaCueProject project) => _write(_before);

    /// <summary>Takes the newer value and keeps the original "before", so the whole drag reverts.</summary>
    public void MergeFrom(ICoalescingCommand newer)
    {
        if (newer is not SetValueCommand<T> other)
            return;

        _after = other._after;
        Description = $"set {Key.Property} {Format(_before)} → {Format(_after)}";
    }

    /// <summary>The current value, for tests and for a UI that wants to show the pending edit.</summary>
    public T Current => _read();

    private static string Format(T value) => value switch
    {
        null => "-",
        bool flag => flag ? "on" : "off",
        double number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };
}

/// <summary>Adds an item to a document list at a known position, and takes it back out.</summary>
/// <remarks>
/// The index is remembered so an undo/redo pair restores ORDER, not just membership. Appending on
/// redo would quietly reorder a cue list, which is a show change dressed up as an undo.
/// </remarks>
public sealed class AddItemCommand<T>(
    IList<T> list,
    T item,
    int index,
    string domain,
    string description) : IProjectCommand
{
    public string Description { get; } = description;
    public string Domain { get; } = domain;

    public void Apply(HaCueProject project) => list.Insert(Math.Clamp(index, 0, list.Count), item);

    public void Revert(HaCueProject project) => list.Remove(item);
}

/// <summary>Removes an item from a document list, remembering where it was.</summary>
public sealed class RemoveItemCommand<T> : IProjectCommand
{
    private readonly IList<T> _list;
    private readonly T _item;
    private readonly int _index;

    public RemoveItemCommand(IList<T> list, T item, string domain, string description)
    {
        _list = list;
        _item = item;
        _index = list.IndexOf(item);
        Domain = domain;
        Description = description;
    }

    public string Description { get; }
    public string Domain { get; }

    public void Apply(HaCueProject project) => _list.Remove(_item);

    public void Revert(HaCueProject project) =>
        _list.Insert(Math.Clamp(_index, 0, _list.Count), _item);
}

/// <summary>
/// Sets, adds or removes one V×R patch cell.
/// </summary>
/// <remarks>
/// Routing and level are one edit here because in the matrix they are one gesture: clicking an empty
/// cell routes it at unity, dragging changes the gain, right-clicking mutes. Splitting them into
/// separate commands would make "click then drag" two undo steps for one motion.
/// <para>
/// <b>Not routed is not muted.</b> Removing the cell means no signal path exists; muting keeps the
/// path and silences it. The distinction is what lets an operator restore a patch they muted, and
/// what keeps an absent device's patch intact rather than deleted.
/// </para>
/// </remarks>
public sealed class SetPatchCellCommand : ICoalescingCommand
{
    private readonly ProjectAudioPatch _patch;
    private readonly Guid _channelId;
    private readonly Guid _lineId;
    private readonly int _lineChannel;
    private readonly CellState _before;
    private CellState _after;

    public SetPatchCellCommand(
        ProjectAudioPatch patch,
        Guid channelId,
        Guid lineId,
        int lineChannel,
        double? gainDb,
        bool? muted,
        string description)
    {
        _patch = patch;
        _channelId = channelId;
        _lineId = lineId;
        _lineChannel = lineChannel;
        _before = Read();
        _after = gainDb is null && muted is null
            ? CellState.Unrouted
            : new CellState(
                true,
                gainDb ?? _before.GainDb,
                muted ?? _before.Muted,
                // A cell that already exists keeps its place; a new one goes on the end.
                _before.Routed ? _before.Index : patch.Cells.Count);

        Key = new CoalesceKey(channelId, $"patch:{lineId}:{lineChannel}");
        Domain = "patch";
        Description = description;
    }

    public CoalesceKey Key { get; }
    public string Domain { get; }
    public string Description { get; }

    public void Apply(HaCueProject project) => Write(_after);

    public void Revert(HaCueProject project) => Write(_before);

    public void MergeFrom(ICoalescingCommand newer)
    {
        if (newer is SetPatchCellCommand other)
            _after = other._after;
    }

    private CellState Read()
    {
        var cell = Find();
        return cell is null
            ? CellState.Unrouted
            : new CellState(true, cell.GainDb, cell.Muted, _patch.Cells.IndexOf(cell));
    }

    private void Write(CellState state)
    {
        var cell = Find();

        if (!state.Routed)
        {
            if (cell is not null)
                _patch.Cells.Remove(cell);
            return;
        }

        if (cell is null)
        {
            var restored = new PatchCell
            {
                LogicalChannelId = _channelId,
                LineId = _lineId,
                LineChannel = _lineChannel,
                GainDb = state.GainDb,
                Muted = state.Muted,
            };

            // Back at its original index, not appended. Order is part of the document: re-adding at
            // the end round-trips the same CONTENT to different bytes, so an undo would leave a file
            // that diffs against the one on disk for no reason the operator can see.
            _patch.Cells.Insert(Math.Clamp(state.Index, 0, _patch.Cells.Count), restored);
            return;
        }

        cell.GainDb = state.GainDb;
        cell.Muted = state.Muted;
    }

    private PatchCell? Find() =>
        _patch.Cells.FirstOrDefault(cell => cell.Matches(_channelId, _lineId, _lineChannel));

    private readonly record struct CellState(bool Routed, double GainDb, bool Muted, int Index)
    {
        public static CellState Unrouted { get; } = new(false, 0, false, -1);
    }
}

/// <summary>Builders for the edits that are several changes the operator thinks of as one.</summary>
public static class ProjectEdits
{
    /// <summary>
    /// Deletes a logical output and everything that pointed at it, as ONE undoable edit.
    /// </summary>
    /// <remarks>
    /// Register item 11 - this supersedes the earlier "refuse, or ask the operator to choose
    /// cancel/remove/rebind" design. Asking is worse than it sounds: the operator has to answer before
    /// they can see what the answer would do, and the reference list is exactly what they need to see.
    /// Cleaning up automatically and making the whole thing one ⌘Z is the version that can be
    /// explored safely.
    /// <para>
    /// Everything that can reference a channel is cleaned here: patch cells, cue sends, snapshot
    /// cells, output-group membership, patch-cue level changes, fade-cue targets, and the clock
    /// master. Missing one would leave a dangling reference the validator then reports against a
    /// channel that no longer exists.
    /// </para>
    /// </remarks>
    public static void DeleteLogicalChannel(ProjectJournal journal, Guid channelId)
    {
        var project = journal.Project;
        var channel = project.FindChannel(channelId);
        if (channel is null)
            return;

        using (journal.Composite($"delete logical output “{channel.Name}”", "outputs"))
        {
            foreach (var cell in project.AudioPatch.Cells
                         .Where(cell => cell.LogicalChannelId == channelId).ToList())
                journal.Do(new RemoveItemCommand<PatchCell>(
                    project.AudioPatch.Cells, cell, "patch", "remove patch cell"));

            foreach (var snapshot in project.PatchSnapshots)
                foreach (var cell in snapshot.Cells
                             .Where(cell => cell.LogicalChannelId == channelId).ToList())
                    journal.Do(new RemoveItemCommand<PatchCell>(
                        snapshot.Cells, cell, "patch", $"remove cell from snapshot “{snapshot.Name}”"));

            foreach (var cue in project.AllCues())
            {
                switch (cue)
                {
                    case MediaCueNode media:
                        foreach (var send in media.Sends
                                     .Where(send => send.LogicalChannelId == channelId).ToList())
                            journal.Do(new RemoveItemCommand<CueAudioSend>(
                                media.Sends, send, "cues", $"remove send from Q{cue.Number}"));
                        break;

                    case PatchCueNode patchCue:
                        foreach (var change in patchCue.Levels
                                     .Where(change => change.LogicalChannelId == channelId).ToList())
                            journal.Do(new RemoveItemCommand<PatchLevelChange>(
                                patchCue.Levels, change, "cues", $"remove level change from Q{cue.Number}"));
                        break;

                    case FadeCueNode fade when fade.TargetChannelIds.Contains(channelId):
                        journal.Do(new RemoveItemCommand<Guid>(
                            fade.TargetChannelIds, channelId, "cues", $"remove fade target from Q{cue.Number}"));
                        break;
                }
            }

            foreach (var group in project.AudioPatch.Groups.Where(g => g.MemberIds.Contains(channelId)))
                journal.Do(new RemoveItemCommand<Guid>(
                    group.MemberIds, channelId, "outputs", $"remove from group “{group.Name}”"));

            journal.Do(new RemoveItemCommand<LogicalAudioChannel>(
                project.AudioPatch.LogicalChannels, channel, "outputs",
                $"delete logical output “{channel.Name}”"));
        }
    }

    /// <summary>
    /// Deletes an audio line and everything that pointed at it, as ONE undoable step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line is referenced from four places, and every one of them fails differently if it is left
    /// behind: a patch cell on a missing line silently routes nothing, a snapshot recalls a cell that
    /// cannot land, a patch cue changes a level on hardware that is gone, and the clock master or the
    /// audition rig pointing at nothing takes the rig with it. So they go together or not at all.
    /// </para>
    /// <para>
    /// Snapshot cells are removed too rather than left as history. A snapshot is a state the show can
    /// RECALL, so one holding a cell on a deleted line is a recall that quietly does less than it says.
    /// </para>
    /// </remarks>
    public static void DeleteAudioLine(ProjectJournal journal, Guid lineId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;
        var line = project.FindLine(lineId);

        if (line is null)
            return;

        using (journal.Composite($"delete audio line “{line.Name}”", "audio"))
        {
            foreach (var cell in project.AudioPatch.Cells.Where(cell => cell.LineId == lineId).ToList())
                journal.Do(new RemoveItemCommand<PatchCell>(
                    project.AudioPatch.Cells, cell, "patch", "remove patch cell"));

            foreach (var snapshot in project.PatchSnapshots)
                foreach (var cell in snapshot.Cells.Where(cell => cell.LineId == lineId).ToList())
                    journal.Do(new RemoveItemCommand<PatchCell>(
                        snapshot.Cells, cell, "patch", $"remove cell from snapshot “{snapshot.Name}”"));

            foreach (var cue in project.AllCues().OfType<PatchCueNode>())
                foreach (var level in cue.Levels.Where(level => level.LineId == lineId).ToList())
                    journal.Do(new RemoveItemCommand<PatchLevelChange>(
                        cue.Levels, level, "cues", $"remove level change from Q{cue.Number}"));

            if (project.AudioPatch.ClockMasterLineId == lineId)
            {
                var patch = project.AudioPatch;
                journal.Do(new SetValueCommand<Guid?>(
                    lineId, "clockMaster", "audio",
                    () => patch.ClockMasterLineId, value => patch.ClockMasterLineId = value, null,
                    "clear the clock master"));
            }

            if (project.Audition.AudioLineId == lineId)
            {
                var rig = project.Audition;
                journal.Do(new SetValueCommand<Guid?>(
                    lineId, "auditionLine", "audio",
                    () => rig.AudioLineId, value => rig.AudioLineId = value, null,
                    "clear the audition rig's line"));
            }

            journal.Do(new RemoveItemCommand<AudioLineDefinition>(
                project.AudioLines, line, "audio", $"delete audio line “{line.Name}”"));
        }
    }

    /// <summary>
    /// Applies one gain delta across a whole Output Group.
    /// </summary>
    /// <remarks>
    /// Register item 9: changing one member's cell applies the SAME DELTA to the other members'
    /// corresponding cells - the corresponding cell being the one on the same line at the member's own
    /// offset within the group. Setting them all to the same absolute value would flatten a deliberate
    /// L/R trim the first time anyone nudged a stereo pair.
    /// <para>
    /// Grouping is an editing and display convenience only; the mix math stays strictly per channel,
    /// so this produces ordinary per-cell edits and nothing downstream knows a group was involved.
    /// </para>
    /// </remarks>
    public static void NudgeGroupGain(
        ProjectJournal journal,
        Guid channelId,
        Guid lineId,
        int lineChannel,
        double deltaDb)
    {
        var project = journal.Project;
        var patch = project.AudioPatch;
        var group = patch.GroupOf(channelId);

        if (group is null)
        {
            journal.Do(GainCommand(patch, channelId, lineId, lineChannel, deltaDb));
            return;
        }

        var index = group.MemberIds.IndexOf(channelId);
        using (journal.Composite($"trim group “{group.Name}” {deltaDb:+0.0;-0.0} dB", "patch"))
        {
            for (var member = 0; member < group.MemberIds.Count; member++)
            {
                // The other members' corresponding cell sits at the same offset from the edited one,
                // which is what makes a stereo pair on Out 1/2 move together.
                var memberChannel = lineChannel + (member - index);
                journal.Do(GainCommand(patch, group.MemberIds[member], lineId, memberChannel, deltaDb));
            }
        }
    }

    private static SetPatchCellCommand GainCommand(
        ProjectAudioPatch patch, Guid channelId, Guid lineId, int lineChannel, double deltaDb)
    {
        var existing = patch.Cells.FirstOrDefault(cell => cell.Matches(channelId, lineId, lineChannel));
        var gain = (existing?.GainDb ?? 0) + deltaDb;
        return new SetPatchCellCommand(
            patch, channelId, lineId, lineChannel, gain, existing?.Muted ?? false,
            $"set cell gain {gain:0.0} dB");
    }
}

/// <summary>
/// Sets, adds or removes one N×V send: a cue's source channel into a logical output.
/// </summary>
/// <remarks>
/// The twin of <see cref="SetPatchCellCommand"/>, and deliberately shaped the same way - the two
/// matrices are the same grid read at two levels, and an operator who learns "click routes, drag
/// adjusts, right-click mutes" on one must find it true on the other.
/// </remarks>
public sealed class SetCueSendCommand : ICoalescingCommand
{
    private readonly MediaCueNode _cue;
    private readonly int _sourceChannel;
    private readonly Guid _channelId;
    private readonly SendState _before;
    private SendState _after;

    public SetCueSendCommand(
        MediaCueNode cue,
        int sourceChannel,
        Guid channelId,
        double? gainDb,
        bool? muted,
        string description)
    {
        _cue = cue;
        _sourceChannel = sourceChannel;
        _channelId = channelId;
        _before = Read();
        _after = gainDb is null && muted is null
            ? SendState.Unrouted
            : new SendState(
                true,
                gainDb ?? _before.GainDb,
                muted ?? _before.Muted,
                _before.Routed ? _before.Index : cue.Sends.Count);

        Key = new CoalesceKey(cue.Id, $"send:{sourceChannel}:{channelId}");
        Domain = "cues";
        Description = description;
    }

    public CoalesceKey Key { get; }
    public string Domain { get; }
    public string Description { get; }

    public void Apply(HaCueProject project) => Write(_after);

    public void Revert(HaCueProject project) => Write(_before);

    public void MergeFrom(ICoalescingCommand newer)
    {
        if (newer is SetCueSendCommand other)
            _after = other._after;
    }

    private SendState Read()
    {
        var send = Find();
        return send is null
            ? SendState.Unrouted
            : new SendState(true, send.GainDb, send.Muted, _cue.Sends.IndexOf(send));
    }

    private void Write(SendState state)
    {
        var send = Find();

        if (!state.Routed)
        {
            if (send is not null)
                _cue.Sends.Remove(send);
            return;
        }

        if (send is null)
        {
            // Back at its original index for the same reason a patch cell is: order is document state,
            // and an undo that reordered the sends would produce a file that diffs for no visible reason.
            _cue.Sends.Insert(
                Math.Clamp(state.Index, 0, _cue.Sends.Count),
                new CueAudioSend
                {
                    SourceChannel = _sourceChannel,
                    LogicalChannelId = _channelId,
                    GainDb = state.GainDb,
                    Muted = state.Muted,
                });
            return;
        }

        send.GainDb = state.GainDb;
        send.Muted = state.Muted;
    }

    private CueAudioSend? Find() =>
        _cue.Sends.FirstOrDefault(send =>
            send.SourceChannel == _sourceChannel && send.LogicalChannelId == _channelId);

    private readonly record struct SendState(bool Routed, double GainDb, bool Muted, int Index)
    {
        public static SendState Unrouted { get; } = new(false, 0, false, -1);
    }
}

/// <summary>A rectangle in fractions of whatever contains it - a composition, or an output.</summary>
/// <remarks>
/// Fractions rather than pixels so a placement survives a composition being resized, and so the
/// document never has to know what a canvas is currently drawn at.
/// </remarks>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// Keeps a box on the canvas and big enough to grab again.
    /// </summary>
    /// <remarks>
    /// The minimum is the important half: a drag that could take a placement to zero size would let
    /// somebody lose a layer with one slip and have nothing left to click on to get it back.
    /// </remarks>
    public NormalizedRect Clamped(double minimum = 0.02)
    {
        var width = Math.Clamp(Width, minimum, 1);
        var height = Math.Clamp(Height, minimum, 1);

        return new NormalizedRect(
            Math.Clamp(X, 0, 1 - width),
            Math.Clamp(Y, 0, 1 - height),
            width,
            height);
    }

    /// <summary>How far outside the frame a free rectangle may be dragged, as a multiple of it.</summary>
    public const double FreeReach = 2;

    /// <summary>
    /// Kept a sane SIZE but free to leave the frame, which is what a layer placement needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A picture pushed off the edge of the canvas is an ordinary thing to author - a caption that
    /// bleeds off the bottom, a wall of screens where each clip sits past its neighbour, a reveal that
    /// slides in from outside. <see cref="Clamped"/> is the wrong rule for it, and did more than refuse
    /// the odd case: it pins X to <c>1 − width</c>, so a placement filling the canvas could only ever
    /// have X = 0 and could not be dragged AT ALL. That is the commonest placement in any show.
    /// </para>
    /// <para>
    /// Still bounded. Free is not infinite: a slip that threw a layer a thousand canvases away would
    /// leave nothing on screen to drag back, which is the same trap the minimum size exists to avoid.
    /// </para>
    /// </remarks>
    public NormalizedRect Free(double minimum = 0.02, double reach = FreeReach)
    {
        var width = Math.Clamp(Width, minimum, 1 + (reach * 2));
        var height = Math.Clamp(Height, minimum, 1 + (reach * 2));

        return new NormalizedRect(
            Math.Clamp(X, -reach, 1 + reach),
            Math.Clamp(Y, -reach, 1 + reach),
            width,
            height);
    }
}

/// <summary>
/// Moves or resizes one rectangle - a layer placement, or a mapping section's source or target.
/// </summary>
/// <remarks>
/// One command for all four numbers, with one coalesce key, because a drag changes them together and
/// splitting them would make a single gesture four undo steps that can be walked back into a shape the
/// operator never saw.
/// </remarks>
public sealed class SetRectCommand : ICoalescingCommand
{
    private readonly Func<NormalizedRect> _read;
    private readonly Action<NormalizedRect> _write;
    private readonly NormalizedRect _before;
    private NormalizedRect _after;

    public SetRectCommand(
        Guid subject,
        string property,
        string domain,
        Func<NormalizedRect> read,
        Action<NormalizedRect> write,
        NormalizedRect value,
        string description,
        bool allowOutsideFrame = false)
    {
        _read = read;
        _write = write;
        _before = read();
        // Most mapping edits are regions OF a frame and use the safe clamped default. Editors with an
        // explicit offstage work area opt into Free as well: an overhanging source becomes black and
        // an overhanging destination is clipped by the output. Layer placements always use that route.
        _after = allowOutsideFrame ? value.Free() : value.Clamped();
        Key = new CoalesceKey(subject, property);
        Domain = domain;
        Description = description;
    }

    public CoalesceKey Key { get; }
    public string Domain { get; }
    public string Description { get; }

    public NormalizedRect Current => _read();

    public void Apply(HaCueProject project) => _write(_after);

    public void Revert(HaCueProject project) => _write(_before);

    public void MergeFrom(ICoalescingCommand newer)
    {
        if (newer is SetRectCommand other)
            _after = other._after;
    }
}

/// <summary>
/// The three rectangles a canvas drag can be editing, each as one command.
/// </summary>
/// <remarks>
/// Kept together because the three read almost identically and the differences are the interesting
/// part: a layer placement is a property of the CUE (register item 21), so its coalesce subject is the
/// cue - undoing a move undoes it wherever that cue is looked at. A mapping section's source and target
/// are two rectangles on one object, so they take the same subject and different property names, and a
/// drag on the left canvas never merges into a drag on the right.
/// </remarks>
public static class RectEdits
{
    public static SetRectCommand Placement(CueNode cue, LayerPlacement placement, NormalizedRect rect) =>
        new(cue.Id, "placement", "video",
            () => new NormalizedRect(placement.X, placement.Y, placement.Width, placement.Height),
            value =>
            {
                placement.X = value.X;
                placement.Y = value.Y;
                placement.Width = value.Width;
                placement.Height = value.Height;
            },
            rect, "move layer", allowOutsideFrame: true);

    public static SetRectCommand MappingSource(
        MappingSection section, NormalizedRect rect, bool allowOutsideFrame = false) =>
        new(section.Id, "source", "mapping",
            () => new NormalizedRect(
                section.SourceX, section.SourceY, section.SourceWidth, section.SourceHeight),
            value =>
            {
                section.SourceX = value.X;
                section.SourceY = value.Y;
                section.SourceWidth = value.Width;
                section.SourceHeight = value.Height;
            },
            rect, "move source region", allowOutsideFrame);

    public static SetRectCommand MappingTarget(
        MappingSection section, NormalizedRect rect, bool allowOutsideFrame = false) =>
        new(section.Id, "target", "mapping",
            () => new NormalizedRect(
                section.TargetX, section.TargetY, section.TargetWidth, section.TargetHeight),
            value =>
            {
                section.TargetX = value.X;
                section.TargetY = value.Y;
                section.TargetWidth = value.Width;
                section.TargetHeight = value.Height;
            },
            rect, "move output region", allowOutsideFrame);
}
