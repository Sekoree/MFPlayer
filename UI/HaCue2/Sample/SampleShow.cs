using HaCue2.ViewModels;

namespace HaCue2.Sample;

/// <summary>
/// What is still invented.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is a progress bar.</b> Everything left in it stands in for a subsystem that does not
/// exist yet, and each member is deleted by the change that lands its real source — not before, because
/// removing a placeholder without its replacement turns "visibly a mockup" into "visibly broken".
/// </para>
/// <para>
/// Already retired: the launcher's recents, recovery notice and machine checks (now
/// <c>AppSettings</c>, <c>RecoveryStore</c> and <c>MachineFacts</c>), and the audio bay's rows (now
/// <c>BayPresentation</c> over the bay's own counters). Moved out because they were never sample data
/// at all: the fade-curve library, the settings navigation and the record-pattern help.
/// </para>
/// <para>
/// Remaining, with what each is waiting for: <see cref="Overrides"/> needs the project override
/// ledger, and the log tail is now a live read of the MEL pipeline.
/// </para>
/// </remarks>
public static class SampleShow
{
    // ── screen 13 · the project override ledger ───────────────────────────────────────────────

    public static IReadOnlyList<OverrideRow> Overrides { get; } =
    [
        new("Panic fade", "0.15 s", "0.25 s"),
        new("Remote API", "off", "on · port 8420"),
    ];
}
