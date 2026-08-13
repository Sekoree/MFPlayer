using Avalonia;
using Avalonia.Headless;

namespace HaCue2.Tests;

/// <summary>
/// The sanctioned way to run work on the headless UI session.
/// </summary>
/// <remarks>
/// <para>
/// Every dispatch in this assembly goes through one of these, because they carry two things a raw
/// <c>HeadlessUnitTestSession.Dispatch</c> does not.
/// </para>
/// <para>
/// <b>1. Async bodies are actually awaited.</b> <see cref="HeadlessUnitTestSession"/> has no
/// <c>Func&lt;Task&gt;</c> overload of <c>Dispatch</c>: an <c>async () =&gt; …</c> lambda binds to
/// <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c> with <c>TResult = Task</c>, which runs the lambda
/// only to its first await and returns the inner task un-awaited. Every assertion after that first await
/// then runs - or fails - after the test has already passed.
/// </para>
/// <para>
/// <b>2. They survive the PerTest app-init race</b> - see <see cref="IsHeadlessAppInitRace"/>.
/// </para>
/// </remarks>
internal static class HeadlessDispatchExtensions
{
    /// <summary>Attempts per dispatch. The race window is microseconds wide and the retry re-runs a
    /// fresh reset, so one extra attempt is already generous.</summary>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>Runs a synchronous body on the UI session.</summary>
    public static Task DispatchGuarded(
        this HeadlessUnitTestSession session, Action body, CancellationToken cancellationToken = default)
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    /// <summary>Runs a synchronous body that produces a value.</summary>
    public static Task<TResult> DispatchGuarded<TResult>(
        this HeadlessUnitTestSession session, Func<TResult> body, CancellationToken cancellationToken = default)
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    /// <summary>Awaits an async body - the body itself, not just its scheduling.</summary>
    public static Task DispatchAsync(
        this HeadlessUnitTestSession session, Func<Task> body, CancellationToken cancellationToken = default)
        // Routed through the Func<Task<TResult>> overload: it keeps pumping the session's dispatcher
        // until the inner task completes. A naive `await await session.Dispatch(body, ct)` deadlocks -
        // once the lambda hits its first await the session stops pumping, so the body's continuation
        // (queued back onto that dispatcher) would never run.
        => RetryAsync(() => session.Dispatch<object?>(
            async () => { await body(); return null; }, cancellationToken));

    /// <summary>Awaits an async body that produces a value.</summary>
    public static Task<TResult> DispatchAsync<TResult>(
        this HeadlessUnitTestSession session, Func<Task<TResult>> body,
        CancellationToken cancellationToken = default)
        => RetryAsync(() => session.Dispatch(body, cancellationToken));

    private static async Task RetryAsync(Func<Task> dispatch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dispatch().ConfigureAwait(false);
                return;
            }
            catch (Exception failure) when (attempt < MaxAttempts && IsHeadlessAppInitRace(failure))
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
    }

    private static async Task<TResult> RetryAsync<TResult>(Func<Task<TResult>> dispatch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dispatch().ConfigureAwait(false);
            }
            catch (Exception failure) when (attempt < MaxAttempts && IsHeadlessAppInitRace(failure))
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True for the ONE failure a retry may be applied to: the session failing to stand its isolated
    /// <see cref="Application"/> up because <c>Dispatcher.UIThread</c> was bound to another thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The race.</b> Avalonia's dispatcher does <c>s_uiThread ??= this</c>, so the first dispatcher
    /// constructed after a reset becomes the process-wide UI thread - whichever thread constructs it.
    /// Isolation is PerTest, so <c>EnsureIsolatedApplication</c> runs on every dispatch: a reset that
    /// nulls the binding, then setup that re-creates it. Between those two calls any thread touching
    /// <c>Dispatcher.UIThread</c> takes the binding, and setup then fails its access check.
    /// </para>
    /// <para>
    /// <b>Why retrying is a fix rather than a paper-over.</b> The corruption self-heals - the next
    /// dispatch's own reset clears the hijacked binding - and the failure happens strictly BEFORE the
    /// dispatched body is invoked, which the stack-frame check is what proves. A retry therefore cannot
    /// run a test body twice.
    /// </para>
    /// </remarks>
    internal static bool IsHeadlessAppInitRace(Exception? failure)
    {
        for (; failure is not null; failure = failure.InnerException)
        {
            if (failure is InvalidOperationException
                && failure.Message.Contains("different thread owns it", StringComparison.Ordinal)
                // The frame distinguishes "the harness could not stand the app up" (body not yet run,
                // safe to retry) from "the test body itself touched the wrong thread" (retrying would
                // hide a real defect behind a flake).
                && (failure.StackTrace?.Contains("EnsureIsolatedApplication", StringComparison.Ordinal) == true
                    || failure.StackTrace?.Contains("SetupUnsafe", StringComparison.Ordinal) == true))
            {
                return true;
            }
        }

        return false;
    }
}
