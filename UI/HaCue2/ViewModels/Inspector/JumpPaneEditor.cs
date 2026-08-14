using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// The JUMP pane: destination, condition, count and arrival behavior of a jump cue - a per-kind
/// editor over the shared <see cref="CueEditPlumbing"/> (review F-11).
/// </summary>
public sealed partial class JumpPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private JumpCueNode? Jump => context.Cue as JumpCueNode;
    private HaCueProject Project => context.Project;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(JumpTargets));
        OnPropertyChanged(nameof(JumpTargetIndex));
        OnPropertyChanged(nameof(JumpConditionIndex));
        OnPropertyChanged(nameof(JumpCountValue));
        OnPropertyChanged(nameof(IsCountedJump));
        OnPropertyChanged(nameof(JumpPickAtRandomValue));
        OnPropertyChanged(nameof(JumpFiresOnArrivalValue));
        OnPropertyChanged(nameof(JumpHint));
    }

    /// <summary>Every cue in the show, as jump destinations. A jump may legitimately cross lists.</summary>
    public IReadOnlyList<string> JumpTargets =>
        Jump is null
            ? []
            : ["- none -", .. Project.AllCues()
                .Where(cue => cue.Id != Jump.Id)
                .Select(cue => $"Q{CuePresentation.Number(cue.Number)} · {cue.Label}")];

    public int JumpTargetIndex
    {
        get
        {
            if (Jump is not { TargetCueIds.Count: > 0 } jump)
                return 0;

            var candidates = Candidates(jump);
            var at = candidates.FindIndex(cue => cue.Id == jump.TargetCueIds[0]);

            // A target that has been deleted reads as "none" rather than pointing at whatever now
            // occupies that position - Project status reports the dangling reference separately.
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Jump is not { } jump || value < 0)
                return;

            var candidates = Candidates(jump);
            var chosen = value == 0 || value > candidates.Count
                ? new List<Guid>()
                : [candidates[value - 1].Id];

            if (chosen.SequenceEqual(jump.TargetCueIds))
                return;

            // A fresh list per cue: one shared instance would alias every selected jump onto the same
            // object, so editing one afterwards would silently edit the others.
            plumbing.EditEach(jump, "jumpTarget", "cues",
                cue => cue.TargetCueIds, (cue, ids) => cue.TargetCueIds = ids,
                _ => new List<Guid>(chosen),
                chosen.Count == 0 ? "clear jump target" : "set jump target");
        }
    }

    private List<CueNode> Candidates(JumpCueNode jump) =>
        [.. Project.AllCues().Where(cue => cue.Id != jump.Id)];

    public IReadOnlyList<string> JumpConditions { get; } =
        ["always", "while the trigger is held", "n times, then continue"];

    public int JumpConditionIndex
    {
        get => Jump is { } jump ? (int)jump.Condition : -1;
        set
        {
            if (Jump is not { } jump || value < 0 || (JumpCondition)value == jump.Condition)
                return;

            var target = jump;
            plumbing.EditEach(target, "jumpCondition", "cues",
                cue => cue.Condition, (cue, condition) => cue.Condition = condition, (JumpCondition)value,
                $"jump {JumpConditions[value]}");
            OnPropertyChanged(nameof(IsCountedJump));
        }
    }

    public bool IsCountedJump => Jump?.Condition == JumpCondition.CountThenContinue;

    public string JumpCountValue
    {
        get => Jump?.JumpCount.ToString(CultureInfo.CurrentCulture) ?? "1";
        set
        {
            if (Jump is not { } jump
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count))
                return;

            count = Math.Clamp(count, 1, 10_000);
            plumbing.EditEach(jump, "jumpCount", "cues",
                cue => cue.JumpCount, (cue, number) => cue.JumpCount = number, count,
                $"jump {count} times, then continue");
        }
    }

    public bool JumpPickAtRandomValue
    {
        get => Jump is { PickAtRandom: true };
        set
        {
            if (Jump is not { } jump || value == jump.PickAtRandom)
                return;

            var target = jump;
            plumbing.EditEach(target, "jumpRandom", "cues",
                cue => cue.PickAtRandom, (cue, on) => cue.PickAtRandom = on, value,
                value ? "pick at random" : "always the first target");
        }
    }

    public bool JumpFiresOnArrivalValue
    {
        get => Jump is not { FireOnArrival: false };
        set
        {
            if (Jump is not { } jump || value == jump.FireOnArrival)
                return;

            var target = jump;
            plumbing.EditEach(target, "jumpFires", "cues",
                cue => cue.FireOnArrival, (cue, on) => cue.FireOnArrival = on, value,
                value ? "fire on arrival" : "move standby only");
        }
    }

    /// <summary>Said in the editor rather than left for Project status to find at a get-in.</summary>
    public string JumpHint => Jump is not { } jump
        ? ""
        : jump.TargetCueIds.Count == 0
            ? "no target - a jump with nowhere to go is an error on Project status, not a silent no-op"
            : jump.TargetCueIds.Any(id => Project.FindCue(id) is null)
                ? "a target no longer exists in this show"
                : "";
}
