using Avalonia;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;

namespace HaPlay.Tests;

/// <summary>
/// The headless <c>TestApp</c> ships no <c>Application.Styles</c>, while the real app declares the
/// <c>ClassicThemeBundle</c> in slot 0 of <c>App.axaml</c> and <see cref="AppearanceController"/> swaps it at
/// startup. Most controls tolerate the difference (Avalonia falls back to a bare ContentPresenter template),
/// but a few - <see cref="Avalonia.Controls.ToggleSwitch"/> is the one HaPlay hosts, in
/// <c>CuePlayerView</c> - declare a REQUIRED template part (<c>PART_MovingKnobs</c>) and throw
/// <see cref="KeyNotFoundException"/> out of <c>OnApplyTemplate</c> when the fallback template doesn't supply
/// it. That crash surfaces during <c>Window.Show()</c>; it was invisible for as long as the dispatch helpers
/// discarded their Task. Tests that realise a real HaPlay view must therefore stand up the same theme the app
/// does - call this first, inside the dispatched body (test isolation is PerTest, so every Dispatch gets a
/// fresh <see cref="Application"/> and the theme never leaks between tests).
/// </summary>
internal static class HeadlessAppTheme
{
    /// <summary>The app's startup default - <c>App.axaml</c> puts the Classic bundle in <c>Styles[0]</c>.</summary>
    public static void ApplyProductionBaseTheme() => ApplyBaseTheme(AppBaseTheme.Classic);

    /// <summary>
    /// Applies one of the shipped base themes. Any of the three now works for tests that need a real
    /// <see cref="Avalonia.Controls.Primitives.OverlayLayer"/> - i.e. anything opening a Flyout/Popup:
    /// the headless platform has no <c>IPopupImpl</c>, so a popup can only fall back to the top level's
    /// overlay layer, and <c>TopLevel</c> only enables that layer when it finds a
    /// <c>VisualLayerManager</c> NAMED <c>PART_VisualLayerManager</c> in the template.
    /// <para>The in-repo Classic theme used to leave that name off every one of its TopLevel templates,
    /// so under Classic - the app's STARTUP DEFAULT - there was no overlay layer at all and
    /// <c>FlyoutBase.ShowAt</c> threw "Unable to create IPopupImpl and no overlay layer is found";
    /// tests worked around it by asking for Simple. Fixed 2026-07-29 in
    /// <c>External/Classic.Avalonia/.../Styles/{Window,PopupRoot,EmbeddableControlRoot}.axaml</c> (each
    /// carries a LOCAL PATCH comment), and pinned by <c>ThemeOverlayLayerTests</c>.</para>
    /// </summary>
    public static void ApplyBaseTheme(AppBaseTheme baseTheme)
    {
        AppearanceController.ApplyBaseTheme(baseTheme);
        AppearanceController.ApplyTheme(AppThemeMode.Light);
    }
}

/// <summary>
/// The sanctioned way to run work on the headless UI session. Every dispatch in this assembly goes through
/// one of these (<see cref="HeadlessDispatchLintTests"/> keeps them honest), because they carry two things a
/// raw <c>HeadlessUnitTestSession.Dispatch</c> does not.
///
/// <para><b>1. Async bodies are actually awaited.</b> <see cref="HeadlessUnitTestSession"/> has no
/// <c>Func&lt;Task&gt;</c> overload of <c>Dispatch</c>: an <c>async () =&gt; …</c> lambda binds to
/// <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c> with <c>TResult = Task</c>, which runs the lambda only
/// to its first await and returns the inner task un-awaited. Every assertion after that first await then ran
/// (or failed) after the test had already passed. Use <see cref="DispatchAsync(HeadlessUnitTestSession,
/// Func{Task}, CancellationToken)"/> for async bodies; <see cref="DispatchGuarded(HeadlessUnitTestSession,
/// Action, CancellationToken)"/> for synchronous ones.</para>
///
/// <para><b>2. They survive the PerTest app-init race</b> - see
/// <see cref="IsHeadlessAppInitRace"/> for the mechanism and why retrying is correct rather than a
/// papering-over.</para>
/// </summary>
internal static class HeadlessDispatchExtensions
{
    /// <summary>Attempts per dispatch. The race window is microseconds wide and the retry re-runs a fresh
    /// reset, so one extra attempt is already generous; three costs nothing on the happy path (the loop
    /// only ever re-enters after the specific failure below) and covers a doubly unlucky run.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Breathing room between attempts. Not needed to clear the corruption - the next dispatch's
    /// own reset does that - but a background tick that is mid-burst gets a moment to pass, so the retry
    /// is not spent on the same instant that lost the first race.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(5);

    private static int _retries;

    /// <summary>How many dispatches this run has had to retry - i.e. how often the race below actually
    /// fired and was absorbed. Zero on a healthy run.</summary>
    public static int Retries => Volatile.Read(ref _retries);

    /// <summary>
    /// Optional file to append a line to on every retry. Opt-in (the <c>HAPLAY_DISPATCHER_GUARD</c>
    /// precedent) because there is no channel here that a test run would actually surface: xunit does not
    /// capture <see cref="Console"/> from a static helper, and <c>ITestOutputHelper</c> is per-test and
    /// unreachable from one - verified, after a first attempt at <c>Console.WriteLine</c> reported "0
    /// retries" for eighteen runs simply because nothing was listening. Set
    /// <c>HAPLAY_DISPATCH_RETRY_LOG=/path/to/file</c> to measure how often the race really fires.
    /// </summary>
    private static readonly string? RetryLogPath =
        Environment.GetEnvironmentVariable("HAPLAY_DISPATCH_RETRY_LOG");

    /// <summary>Runs a synchronous body on the UI session. Drop-in for <c>session.Dispatch(body, ct)</c> -
    /// same returned Task, same overload resolution, same blocking idiom at the call site.</summary>
    public static Task DispatchGuarded(
        this HeadlessUnitTestSession session, Action body, CancellationToken cancellationToken = default)
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    /// <summary>Runs a synchronous body that produces a value. Drop-in for <c>session.Dispatch(body, ct)</c>.</summary>
    public static Task<TResult> DispatchGuarded<TResult>(
        this HeadlessUnitTestSession session, Func<TResult> body, CancellationToken cancellationToken = default)
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    /// <summary>Awaits an async body - the body itself, not just its scheduling.</summary>
    public static Task DispatchAsync(
        this HeadlessUnitTestSession session, Func<Task> body, CancellationToken cancellationToken = default)
        // Route through the Func<Task<TResult>> overload: it keeps pumping the session's dispatcher
        // until the inner task completes. A naive `await await session.Dispatch(body, ct)` deadlocks -
        // once the lambda hits its first await the session stops pumping, so the body's continuation
        // (queued back onto that dispatcher) would never run.
        => RetryAsync(() => session.Dispatch<object?>(async () => { await body(); return null; }, cancellationToken));

    /// <summary>Awaits an async body that produces a value.</summary>
    public static Task<TResult> DispatchAsync<TResult>(
        this HeadlessUnitTestSession session, Func<Task<TResult>> body, CancellationToken cancellationToken = default)
        // The Func<Task<TResult>> overload of Dispatch awaits the inner task itself.
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    internal static async Task RetryAsync(Func<Task> dispatch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dispatch().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsHeadlessAppInitRace(ex))
            {
                await Retried(attempt, ex).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<TResult> RetryAsync<TResult>(Func<Task<TResult>> dispatch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dispatch().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsHeadlessAppInitRace(ex))
            {
                await Retried(attempt, ex).ConfigureAwait(false);
            }
        }
    }

    private static async Task Retried(int attempt, Exception ex)
    {
        var total = Interlocked.Increment(ref _retries);
        if (RetryLogPath is { Length: > 0 } path)
        {
            try
            {
                File.AppendAllText(
                    path,
                    $"retry #{total} (attempt {attempt}) lost the PerTest app-init race: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // Instrumentation must never be the thing that fails a run.
            }
        }

        await Task.Delay(RetryDelay).ConfigureAwait(false);
    }

    /// <summary>
    /// True for the ONE failure a retry may be applied to: the headless session failing to stand its
    /// isolated <see cref="Application"/> up because <c>Dispatcher.UIThread</c> was bound to some other
    /// thread. Everything else propagates on the first attempt.
    ///
    /// <para><b>The race.</b> Avalonia's <c>Dispatcher</c> constructor does <c>s_uiThread ??= this</c>, so
    /// the first dispatcher constructed after a reset becomes the process-wide UI thread - <i>whichever
    /// thread constructs it</i> - and <c>Dispatcher.UIThread</c> is <c>s_uiThread ?? CurrentDispatcher</c>,
    /// where <c>CurrentDispatcher</c> constructs one on the calling thread. Isolation here is PerTest, so
    /// <c>EnsureIsolatedApplication</c> runs on EVERY dispatch: <c>Dispatcher.ResetBeforeUnitTests()</c>
    /// (which nulls <c>s_uiThread</c>) and then <c>AppBuilder.SetupUnsafe()</c>. Between those two calls any
    /// thread that touches <c>Dispatcher.UIThread</c> takes the binding, and <c>SetupUnsafe</c> then fails
    /// <c>VerifyAccess</c> inside <c>DefaultRenderLoop.Add</c>.</para>
    ///
    /// <para><b>Why a retry is a real fix.</b> The corruption self-heals: the next dispatch runs its own
    /// <c>ResetBeforeUnitTests()</c>, which clears the hijacked binding before re-creating it on the session
    /// thread. And the failure happens strictly BEFORE the dispatched body is invoked (app init precedes
    /// <c>action()</c> in <c>DispatchCore</c>) - which the stack-frame check below is what proves - so a
    /// retry cannot run a test body twice. The alternative is tearing down every view model in every test to
    /// silence ~47 <c>DispatcherTimer</c> sites; HaPlay has 128 <c>Dispatcher.UIThread</c> call sites and
    /// many are legitimately reached from background threads, so there is no single culprit to fix.</para>
    ///
    /// <para><b>Anti-finding, do not retry it:</b> switching to
    /// <c>AvaloniaTestIsolationLevel.PerAssembly</c> removes the window (no reset, so no gap) and HANGS the
    /// suite - these tests depend on the per-test fresh <see cref="Application"/>, which is exactly what
    /// <see cref="HeadlessAppTheme"/> documents.</para>
    /// </summary>
    internal static bool IsHeadlessAppInitRace(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is InvalidOperationException
                && ex.Message.Contains("different thread owns it", StringComparison.Ordinal)
                // The frame is what distinguishes "the harness could not stand the app up" (body not yet
                // run, safe to retry) from "the test body itself touched the wrong thread" (retrying would
                // hide a real defect behind a flake). Both Avalonia frames are checked so a rename of
                // either one downgrades this to "no retry", never to "retry the wrong thing".
                && (ex.StackTrace?.Contains("EnsureIsolatedApplication", StringComparison.Ordinal) == true
                    || ex.StackTrace?.Contains("SetupUnsafe", StringComparison.Ordinal) == true))
            {
                return true;
            }
        }

        return false;
    }
}
