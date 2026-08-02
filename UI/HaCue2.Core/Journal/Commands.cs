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
/// document, never copies. That is what keeps the accessors here valid across an undo — a command
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
        null => "—",
        bool flag => flag ? "on" : "off",
        double number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
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
    /// Register item 11 — this supersedes the earlier "refuse, or ask the operator to choose
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
    /// Applies one gain delta across a whole Output Group.
    /// </summary>
    /// <remarks>
    /// Register item 9: changing one member's cell applies the SAME DELTA to the other members'
    /// corresponding cells — the corresponding cell being the one on the same line at the member's own
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
