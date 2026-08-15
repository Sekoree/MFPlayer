using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;

namespace HaCue2.ViewModels;

/// <summary>
/// The GROUP pane: fire mode, playlist/shuffle behavior, pass counts, crossfade and at-end
/// policy of a group cue. A per-kind editor over the shared <see cref="CueEditPlumbing"/>
/// (review F-11).
/// </summary>
public sealed partial class GroupPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private GroupCueNode? Group => context.Cue as GroupCueNode;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(FireModeIndex));
        OnPropertyChanged(nameof(IsTimelineGroup));
        OnPropertyChanged(nameof(IsPlaylistGroup));
        OnPropertyChanged(nameof(IsSequencedGroup));
        OnPropertyChanged(nameof(ChildCount));
        OnPropertyChanged(nameof(ShuffleValue));
        OnPropertyChanged(nameof(ReshuffleValue));
        OnPropertyChanged(nameof(AvoidRepeatValue));
        OnPropertyChanged(nameof(LoopCountValue));
        OnPropertyChanged(nameof(PlayCountValue));
        OnPropertyChanged(nameof(CrossfadeValue));
        OnPropertyChanged(nameof(AtEndIndex));
        OnPropertyChanged(nameof(AtEndEnabled));
        OnPropertyChanged(nameof(AtEndHint));
    }

    public IReadOnlyList<string> FireModes { get; } =
        ["all together", "playlist", "timeline", "first cue only", "armed list · one per GO"];

    /// <summary>
    /// How this group fires. Was a hard-coded index, so a timeline group read "playlist".
    /// </summary>
    public int FireModeIndex
    {
        get => Group is { } group ? (int)group.FireMode : -1;
        set
        {
            if (Group is not { } group || value < 0 || (GroupFireMode)value == group.FireMode)
                return;

            plumbing.EditEach(group, "fireMode", "cues",
                cue => cue.FireMode, (cue, mode) => cue.FireMode = mode, (GroupFireMode)value,
                $"fire {FireModes[value]}");
        }
    }

    public bool IsTimelineGroup => Group is { FireMode: GroupFireMode.Timeline };

    /// <summary>Playlist-only options; a timeline group has no "next item" to cross into.</summary>
    public bool IsPlaylistGroup => Group is { FireMode: GroupFireMode.Playlist };
    public bool IsSequencedGroup => Group is
        { FireMode: GroupFireMode.Playlist or GroupFireMode.ArmedList };

    public string ChildCount => Group is { } group
        ? $"{group.Children.Count} cue{(group.Children.Count == 1 ? "" : "s")}"
        : "-";

    public bool ShuffleValue
    {
        get => Group is { Shuffle: true };
        set
        {
            if (Group is { } group && value != group.Shuffle)
                plumbing.EditEach(group, "shuffle", "cues",
                cue => cue.Shuffle, (cue, on) => cue.Shuffle = on, value,
                    value ? "shuffle" : "play in order");
        }
    }

    public bool ReshuffleValue
    {
        get => Group is { ReshuffleEachPass: true };
        set
        {
            if (Group is { } group && value != group.ReshuffleEachPass)
                plumbing.EditEach(group, "reshuffle", "cues",
                cue => cue.ReshuffleEachPass, (cue, on) => cue.ReshuffleEachPass = on, value,
                    "reshuffle each pass");
        }
    }

    /// <summary>
    /// Never open a pass with the item that closed the previous one.
    /// </summary>
    /// <remarks>
    /// Only meaningful while shuffling - an in-order playlist repeats by construction, and hiding the
    /// checkbox is clearer than offering one that does nothing.
    /// </remarks>
    public bool AvoidRepeatValue
    {
        get => Group is not { AvoidImmediateRepeat: false };
        set
        {
            if (Group is { } group && value != group.AvoidImmediateRepeat)
                plumbing.EditEach(group, "avoidRepeat", "cues",
                cue => cue.AvoidImmediateRepeat, (cue, on) => cue.AvoidImmediateRepeat = on, value,
                    value ? "avoid immediate repeats" : "allow immediate repeats");
        }
    }

    /// <summary>Passes through the list. Zero is forever, which is what the field's zero means.</summary>
    public int LoopCountValue
    {
        get => Group?.LoopCount ?? 1;
        set
        {
            if (Group is { } group && value >= 0 && value != group.LoopCount)
                plumbing.EditEach(group, "loopCount", "cues",
                cue => cue.LoopCount, (cue, count) => cue.LoopCount = count, Math.Clamp(value, 0, 999),
                    value == 0 ? "loop forever" : $"play {value} pass(es)");
        }
    }

    /// <summary>Blank means every enabled child; otherwise a subset per pass.</summary>
    public decimal? PlayCountValue
    {
        get => Group?.PlayCount;
        set
        {
            if (Group is not { } group)
                return;
            var count = value is null ? (int?)null : Math.Max(1, (int)value.Value);
            if (count == group.PlayCount)
                return;
            plumbing.EditEach(group, "playCount", "cues",
                cue => cue.PlayCount, (cue, set) => cue.PlayCount = set, count, count is null ? "play every item per pass" : $"play {count} item(s) per pass");
        }
    }

    public string CrossfadeValue
    {
        get => Group is { } group ? $"{group.CrossfadeMs / 1000d:0.##} s" : "-";
        set
        {
            if (Group is not { } group
                || !double.TryParse(
                    new string([.. value.Where(c => char.IsAsciiDigit(c) || c is '.' or ',')]),
                    NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds))
                return;

            plumbing.EditEach(group, "crossfade", "cues",
                cue => cue.CrossfadeMs, (cue, ms) => cue.CrossfadeMs = ms, (int)Math.Clamp(seconds * 1000, 0, 60_000), "set crossfade");
        }
    }

    /// <summary>
    /// "loop" is deliberately NOT offered here: looping is the pass count's whole job (0 =
    /// forever), and a group-level "loop" at-end did nothing - two controls claiming one behaviour,
    /// one of them a lie. A legacy document carrying it displays (and behaves) as "hold last".
    /// </summary>
    public IReadOnlyList<string> AtEndOptions { get; } = ["hold last", "next list"];

    public int AtEndIndex
    {
        get => Group?.AtEnd switch
        {
            AtListEnd.NextList => 1,
            null => -1,
            _ => 0, // Hold, and legacy Loop which always behaved as Hold at group level
        };
        set
        {
            if (Group is not { } group || value < 0)
                return;

            var wanted = value == 1 ? AtListEnd.NextList : AtListEnd.Hold;
            if (wanted == group.AtEnd)
                return;

            plumbing.EditEach(group, "atEnd", "cues",
                cue => cue.AtEnd, (cue, at) => cue.AtEnd = at, wanted,
                $"at end: {AtEndOptions[value]}");
        }
    }

    /// <summary>A forever-looping playlist has no end for a policy to run at; the field greys
    /// rather than offering a choice that can never happen.</summary>
    public bool AtEndEnabled => Group is { LoopCount: not 0 };

    public string AtEndHint => Group is { LoopCount: 0 }
        ? "looping forever - the end never comes; set a pass count to choose an ending"
        : "";
}
