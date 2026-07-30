using System.Reflection;
using Avalonia.Headless;
using Xunit.Sdk;

[assembly: HaPlay.Tests.DispatcherOwnershipGuard]

namespace HaPlay.Tests;

/// <summary>
/// Blames the test that BREAKS the shared headless dispatcher, instead of the innocent one that runs next.
/// <para>The whole assembly shares one <see cref="HeadlessUnitTestSession"/> (see
/// <see cref="HeadlessSessionFramework"/>), and Avalonia binds <c>Dispatcher.UIThread</c> to one thread
/// process-wide. A test that returns while some background work is still alive - a REST listener handler, a
/// timer, a fire-and-forget task - can have that work touch the dispatcher afterwards, from a thread-pool
/// thread. The session is then wedged or rebound, and the NEXT test dies in application init with "The calling
/// thread cannot access this object because a different thread owns it". Whichever test happened to run next
/// took the blame, and since xunit's order shifts whenever tests are added or renamed, the victim moved around
/// (2026-07-04 hang; a 2026-07-30 <c>RemoteApiDispatcherTests</c> failure that would not reproduce in isolation
/// - 8 class runs, 6 assembly runs and 3 full-solution runs all clean - because the cause was in another file).
/// A leak like that is only diagnosable if the culprit is what fails.</para>
/// <para>So after EVERY test this probes the shared session: one no-op dispatch, which must complete and must
/// land on the same thread as always. A dispatch that throws, hangs, or arrives on a different thread means the
/// test that just finished left something behind.</para>
/// <para>It reports ONCE and then stands down: a corrupted session cannot be repaired here, and without the
/// latch every remaining test would fail too, burying the one name that matters under hundreds of casualties.
/// The first failure is the culprit - read that one.</para>
/// <para><strong>OPT-IN, and that is not timidity - the probe is not passive.</strong> Isolation is PerTest, so
/// every <c>Dispatch</c> builds and tears down an isolated <c>Application</c>, and each teardown leaves
/// <c>Dispatcher.UIThread</c> momentarily unowned. Probing after all ~970 tests therefore multiplies the exact
/// window a stray thread needs, and measurably so: on this tree the assembly ran 6/6 clean without the guard,
/// 4-of-8 failing with it, and the victims spread from one file to four. That makes it a fine INSTRUMENT and an
/// unacceptable permanent assertion - left on, it would be the flakiest thing in the suite. Set
/// <c>HAPLAY_DISPATCHER_GUARD=1</c> when hunting a "different thread owns it" failure; leave it unset otherwise.</para>
/// <para><strong>What it found, 2026-07-30 - read this before hunting again.</strong> Enabled, it reproduced the
/// corruption on demand with one signature every time: <c>EnsureIsolatedApplication</c> →
/// <c>Compositor..ctor</c> → <c>DefaultRenderLoop.Add</c> → <c>ThrowVerifyAccess</c>. So <c>UIThread</c> really
/// is being taken over by a non-session thread. Two things it also settled:</para>
/// <para>(1) <em>It cannot name the culprit by adjacency.</em> The blamed test differed on every run
/// (<c>CuesGo…</c>, <c>Go_UsesANewSelectionOnce…</c>, <c>CueByReferenceGo…</c>), which means the offending
/// access is ASYNCHRONOUS - it lands whenever some background tick happens to fall in an unbind window, not at
/// the end of the test that armed it. "Blame whoever just finished" is the wrong model for that, so treat a
/// report as "corruption occurred around here", never as a verdict.</para>
/// <para>(2) <em>Listener handlers are not the whole story.</em> Two of the three listener tests never drained
/// (the earlier round added <see cref="DrainListenerHandlers"/> only to the test that motivated it); fixing
/// both was correct hygiene but did NOT reduce failures under the guard. The remaining suspect is the one the
/// framework already point-fixes with <c>HAPLAY_DISABLE_RECOVERY_TIMER</c>: tests construct view models and
/// never tear them down, and there are ~47 <c>DispatcherTimer</c> sites across them
/// (<c>CuePlayerViewModel.Transport</c>, <c>MediaPlayerViewModel</c>, <c>OutputManagementViewModel</c>,
/// <c>SoundboardWorkspaceViewModel</c> - all built per test by <c>RemoteApiDispatcherTests.CreateDispatcher</c>).
/// Dozens accumulate over a run, each tick touching the dispatcher. The structural fix is view-model teardown
/// in tests, not another env-var point-fix; that is a bigger job than one guard and has not been done.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DispatcherOwnershipGuardAttribute : BeforeAfterTestAttribute
{
    /// <summary>Generous on purpose: this must fire on a WEDGED dispatcher, not on a slow one. A test that is
    /// merely heavy has to stay green, or the guard becomes the flake it exists to retire.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Off unless explicitly asked for - see the type remarks for why probing costs what it measures.</summary>
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("HAPLAY_DISPATCHER_GUARD") == "1";

    private static int _dispatcherThreadId;
    private static int _reported;

    public override void After(MethodInfo methodUnderTest)
    {
        if (!Enabled || Volatile.Read(ref _reported) != 0)
            return; // disabled, or already blamed the culprit; everything after this is a casualty, not a cause

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(DispatcherOwnershipGuardAttribute).Assembly);

        var observedThreadId = 0;
        Task probe;
        try
        {
            probe = session.Dispatch(
                () => observedThreadId = Environment.CurrentManagedThreadId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw Blame(methodUnderTest, $"the shared headless session refused a dispatch ({ex.Message})");
        }

        if (!probe.Wait(ProbeTimeout))
            throw Blame(
                methodUnderTest,
                $"the shared headless dispatcher did not answer a no-op dispatch within {ProbeTimeout.TotalSeconds:F0} s, "
                + "so it is wedged");

        if (probe.IsFaulted)
            throw Blame(
                methodUnderTest,
                $"a no-op dispatch on the shared headless session threw ({probe.Exception?.GetBaseException().Message})");

        // The first test to finish establishes what the dispatcher thread IS; every later one must agree.
        var expected = Interlocked.CompareExchange(ref _dispatcherThreadId, observedThreadId, 0);
        if (expected != 0 && expected != observedThreadId)
            throw Blame(
                methodUnderTest,
                $"Dispatcher.UIThread moved from thread {expected} to thread {observedThreadId}");
    }

    /// <summary>Builds the failure AND trips the latch, so the blame is raised exactly once - every call site
    /// here is a report, and none of them can leave the latch unset.</summary>
    private static XunitException Blame(MethodInfo test, string what)
    {
        Volatile.Write(ref _reported, 1);
        return new XunitException(
            $"{test.DeclaringType?.Name}.{test.Name} left the shared headless dispatcher broken: {what}. "
            + "Something this test started was still running when it returned and then touched the dispatcher "
            + "from another thread - await it, or drain it, before the test ends. (Reported by "
            + $"{nameof(DispatcherOwnershipGuardAttribute)}; later tests in this run are casualties, not causes.)");
    }
}
