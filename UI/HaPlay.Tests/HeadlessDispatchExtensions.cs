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
/// Awaits async bodies dispatched onto the headless UI session - the body itself, not just its
/// scheduling. <see cref="HeadlessUnitTestSession"/> has no <c>Func&lt;Task&gt;</c> overload of
/// <c>Dispatch</c>: an <c>async () =&gt; …</c> lambda binds to <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c>
/// with <c>TResult = Task</c>, which runs the lambda only to its first await and returns the inner
/// task un-awaited. Every assertion after that first await then ran (or failed) after the test had
/// already passed, and exceptions from the body were lost. Always use these helpers for async
/// bodies; plain <c>Dispatch</c> stays fine for synchronous ones.
/// </summary>
internal static class HeadlessDispatchExtensions
{
    public static Task DispatchAsync(
        this HeadlessUnitTestSession session, Func<Task> body, CancellationToken cancellationToken = default)
        // Route through the Func<Task<TResult>> overload: it keeps pumping the session's dispatcher
        // until the inner task completes. A naive `await await session.Dispatch(body, ct)` deadlocks -
        // once the lambda hits its first await the session stops pumping, so the body's continuation
        // (queued back onto that dispatcher) would never run.
        => session.Dispatch<object?>(async () => { await body(); return null; }, cancellationToken);

    public static async Task<TResult> DispatchAsync<TResult>(
        this HeadlessUnitTestSession session, Func<Task<TResult>> body, CancellationToken cancellationToken = default)
        // The Func<Task<TResult>> overload of Dispatch awaits the inner task itself.
        => await session.Dispatch(body, cancellationToken);
}
