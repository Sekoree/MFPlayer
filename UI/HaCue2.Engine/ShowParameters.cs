using HaCue2.Core.Model;
using S.Control;

namespace HaCue2.Engine;

/// <summary>
/// The values a control surface may ride (register item 24).
/// </summary>
/// <remarks>
/// <para>
/// Continuous-controller bindings to PARAMETERS are v1, not just note→cue — a fader on the master trim
/// is a first-class binding rather than something bolted on later. The framework supplies the registry
/// and the soft-takeover arithmetic; what belongs here is the ANSWER to "which values does a cue
/// player offer", which only the app can give.
/// </para>
/// <para>
/// <b>Deliberately few.</b> Every parameter here is something an operator would put a physical fader
/// on during a show. Exposing every number in the document would produce a list nobody could search
/// and would invite binding a control surface to values that are really authoring decisions.
/// </para>
/// </remarks>
public static class ShowParameters
{
    /// <summary>The program master trim, in decibels.</summary>
    public const string MasterTrim = "master.trim";

    /// <summary>The audition monitor's own level. Never heard by the audience.</summary>
    public const string AuditionLevel = "audition.level";

    /// <summary>
    /// Registers what this show offers, over the accessors that can actually move them.
    /// </summary>
    /// <remarks>
    /// The registry holds delegates rather than values, so a parameter always reads what is true now —
    /// which is what soft takeover compares a fader's position against. A cached number would make the
    /// control latch against a value the show had already moved past.
    /// </remarks>
    public static ParameterRegistry Build(
        Func<double> readMasterTrim,
        Action<double> writeMasterTrim,
        Func<HaCueProject> project,
        Action<double> writeAuditionLevel)
    {
        ArgumentNullException.ThrowIfNull(readMasterTrim);
        ArgumentNullException.ThrowIfNull(writeMasterTrim);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(writeAuditionLevel);

        var registry = new ParameterRegistry();

        // −60..+12 dB: the same range the app's own level fields accept, so a fader and a typed value
        // cannot disagree about what the ends mean.
        registry.Register(
            new ParameterTarget(MasterTrim, "Master trim", GainRange.SilenceFloorDb, 12, "dB"),
            readMasterTrim,
            writeMasterTrim);

        registry.Register(
            new ParameterTarget(AuditionLevel, "Audition level", GainRange.SilenceFloorDb, 12, "dB"),
            () => project().Audition.LevelDb,
            writeAuditionLevel);

        return registry;
    }

    /// <summary>What a binding editor offers, in the order it should list them.</summary>
    public static IReadOnlyList<ParameterTarget> Describe(ParameterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return [.. registry.Targets.OrderBy(target => target.DisplayName, StringComparer.Ordinal)];
    }
}
