using HaCue2.Core.Model;

namespace HaCue2.Core.Journal;

/// <summary>
/// One reversible edit to a <see cref="HaCueProject"/>.
/// </summary>
/// <remarks>
/// <para>
/// Commands act on the DOCUMENT, never on a view-model. View-models rebuild or refresh from the
/// result. Retrofitting undo onto mutable view-model graphs is how this class of app ends up with a
/// permanently half-working undo that operators learn not to trust.
/// </para>
/// <para>
/// <b>What is not a command:</b> firing cues, transport, arming, and patch-cue recalls. Undo means
/// "un-edit my document", never "un-play my show". The distinction has to be structural - if a GO
/// could reach this interface, the first stressed operator would press ⌘Z on one.
/// </para>
/// </remarks>
public interface IProjectCommand
{
    /// <summary>
    /// What the operator did, in their words: "set fade 2.0 → 3.0 on Q13.1". Shown on the undo toast
    /// and in the edit log ("what changed since I last saved" - the question people ask after a
    /// rehearsal).
    /// </summary>
    string Description { get; }

    /// <summary>Which surface this edit belongs to - "cues", "patch", "outputs", "settings".</summary>
    /// <remarks>
    /// The undo toast names it ("undid: patch - Fold L → Out 3 gain") because with one journal across
    /// every view, an undo can otherwise change a screen the operator is not looking at.
    /// </remarks>
    string Domain { get; }

    void Apply(HaCueProject project);

    void Revert(HaCueProject project);
}

/// <summary>
/// A command the UI emits in streams - a slider drag, a spinner, a timeline move, a matrix scrub.
/// </summary>
/// <remarks>
/// Consecutive commands sharing a <see cref="CoalesceKey"/> collapse into ONE undo step, keyed by
/// (subject, property), with an idle or blur boundary closing the group. Without this a fader drag
/// leaves a hundred entries and undo becomes useless exactly when it is needed.
/// </remarks>
public interface ICoalescingCommand : IProjectCommand
{
    CoalesceKey Key { get; }

    /// <summary>
    /// Absorbs a newer command in the same group: take its "after" value, keep this one's "before".
    /// That is what makes the whole drag revert to where it started rather than to its last frame.
    /// </summary>
    void MergeFrom(ICoalescingCommand newer);
}

/// <summary>Identifies an edit stream: one property of one subject.</summary>
public readonly record struct CoalesceKey(Guid Subject, string Property);
