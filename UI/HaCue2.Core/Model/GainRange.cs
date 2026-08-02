namespace HaCue2.Core.Model;

/// <summary>
/// The project's gain convention: what "silence" is, and what the editors offer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silence is <see cref="SilenceFloorDb"/>, not negative infinity.</b> Infinity is the
/// mathematically obvious value for "fade to nothing" and it is the wrong one to put in a document:
/// it cannot be written as JSON at all (System.Text.Json refuses it without opting into named
/// literals), it has no meaningful UI representation, and it makes "gains must be finite" — a rule
/// the validator enforces everywhere else — untrue in one place. A floor is also what an operator
/// actually means: below −60 dB nothing is audible, so the extra range buys nothing and costs a
/// special case in every arithmetic path.
/// </para>
/// <para>
/// The ceiling matches the existing cue controls. It is a WARNING to exceed it, not a refusal — the
/// runtime is safe for any validated value, and refusing to open a show because somebody typed +14 dB
/// would be worse than telling them about it.
/// </para>
/// </remarks>
public static class GainRange
{
    /// <summary>At or below this, a route is silence rather than a very quiet signal.</summary>
    public const double SilenceFloorDb = -60;

    /// <summary>The top of the range the editors offer.</summary>
    public const double MaximumDb = 12;

    /// <summary>True when this gain is a value the document may hold.</summary>
    public static bool IsStorable(double gainDb) => double.IsFinite(gainDb);

    /// <summary>True when the gain is inside the range the editors offer.</summary>
    public static bool IsUsual(double gainDb) =>
        IsStorable(gainDb) && gainDb >= SilenceFloorDb && gainDb <= MaximumDb;
}
