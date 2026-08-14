namespace HaCue2.Core.Model;

/// <summary>
/// The project's gain convention: what "silence" is, and what the editors offer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silence is <see cref="SilenceFloorDb"/>, not negative infinity.</b> Infinity is the
/// mathematically obvious value for "fade to nothing" and it is the wrong one to put in a document:
/// it cannot be written as JSON at all (System.Text.Json refuses it without opting into named
/// literals), it has no meaningful UI representation, and it makes "gains must be finite" - a rule
/// the validator enforces everywhere else - untrue in one place. A floor is also what an operator
/// actually means: below −60 dB nothing is audible, so the extra range buys nothing and costs a
/// special case in every arithmetic path.
/// </para>
/// <para>
/// The ceiling matches the existing cue controls. It is a WARNING to exceed it, not a refusal - the
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

    // ── the ONE statement of the dB↔linear convention (2026-08-14 second review) ─────────────────
    // The silence-floor rule ("at or below the floor the factor is EXACTLY 0, never a very quiet
    // signal") was restated inline at least six times across ShowHost/ShowCompiler/DuckMath/
    // ProjectPatchBay - each a chance to drift. It lives here, next to the floor it interprets.

    /// <summary>dB → linear gain factor, double precision (curve math). Floor → exactly 0.</summary>
    public static double LinearFactor(double gainDb) =>
        gainDb <= SilenceFloorDb ? 0 : Math.Pow(10, gainDb / 20);

    /// <summary>dB → the float gain the engine multiplies by. Floor → exactly 0; no ceiling (a
    /// validated document value above <see cref="MaximumDb"/> is a warning, not a refusal).</summary>
    public static float Linear(double gainDb) => (float)LinearFactor(gainDb);

    /// <summary>Like <see cref="Linear"/> but hard-capped at <see cref="MaximumDb"/> - for the
    /// paths where a wild document value must not overdrive a live envelope.</summary>
    public static float LinearClamped(double gainDb) =>
        gainDb <= SilenceFloorDb ? 0f : (float)Math.Pow(10, Math.Clamp(gainDb, SilenceFloorDb, MaximumDb) / 20);

    /// <summary>Linear gain → dB for reporting a runtime value back in authoring units: at or
    /// below zero it is the silence floor, and the result is clamped to the editors' range.</summary>
    public static double Db(float linear) => linear <= 0f
        ? SilenceFloorDb
        : Math.Clamp(20 * Math.Log10(linear), SilenceFloorDb, MaximumDb);
}
