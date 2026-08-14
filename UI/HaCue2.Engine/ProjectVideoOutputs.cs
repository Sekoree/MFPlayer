using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.NDI.Video;
using S.Media.Present.SDL3;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>One output that opened, and what the session knows it as.</summary>
/// <param name="CompositionId">
/// The canvas it shows, or null when it shows none yet - which a LOCAL SCREEN is allowed to be. Its
/// window still opens, because an operator who has just added a projector needs to see WHERE it landed
/// before they decide what to put on it.
/// </param>
internal sealed record OpenVideoOutput(Guid Id, Guid? CompositionId, IVideoOutput Output)
{
    /// <summary>The id the session addresses this output by, inside its composition.</summary>
    public string OutputId => Id.ToString("N");
}

/// <summary>
/// The project's video outputs, opened.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ProjectPatchBay"/> on the video side, and it follows the same rules:
/// an output this machine cannot open is REPORTED and skipped rather than silently redirected, and one
/// failing output never stops the others. A composition sent to whatever screen happens to answer is
/// the video equivalent of playing the show into the wrong room.
/// </para>
/// <para>
/// <b>Every kind opens.</b> A local screen is a window; an NDI output is a sender's own
/// <c>IVideoOutput</c>; a record or stream output is a <see cref="RecordVideoOutput"/> the compositor
/// renders into from the moment the show loads, holding no file until somebody arms it - the encode
/// session is swapped in behind it, because pressing record must not restart the clips on that
/// composition. Only NDI and the recorders open when this machine has no display: neither is a window,
/// and a booth box running headless is exactly where an unattended send or capture belongs.
/// </para>
/// </remarks>
/// <summary>An open output whose canvas changed: where it was attached, and where it belongs now.</summary>
/// <param name="From">Null when it showed nothing yet; the host has no attachment to release.</param>
/// <param name="To">Null when it now shows nothing; the host paints it black instead.</param>
internal sealed record RetargetedOutput(Guid Id, Guid? From, Guid? To)
{
    public string OutputId => Id.ToString("N");
}

public sealed class ProjectVideoOutputs : IDisposable
{
    private readonly List<OpenVideoOutput> _open = [];
    private readonly Dictionary<Guid, RecordVideoOutput> _recorders = [];
    private readonly List<NDIOutput> _senders = [];

    private ProjectVideoOutputs()
    {
    }

    private readonly List<string> _failures = [];
    private readonly HashSet<Guid> _unopened = [];
    private readonly List<RetargetedOutput> _retargeted = [];
    private bool _headless;

    /// <summary>What could not be opened on the LAST sync, and why - joined into the host's problem
    /// list at start-up. Replaced per pass: appending forever grew one duplicate row per 300 ms
    /// debounced reload for as long as an output kept failing.</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>
    /// The outputs that are not showing anything, by document id.
    /// </summary>
    /// <remarks>
    /// The same events as <see cref="Failures"/>, addressed rather than described: a sentence is what
    /// the Problems list wants and an id is what the Video screen and the status pass want, and
    /// deriving one from the other means parsing prose. An output that names no composition is in here
    /// too - it opened nothing and shows nothing, which is what the row has to say.
    /// </remarks>
    public IReadOnlySet<Guid> Unopened => _unopened;

    internal IReadOnlyList<OpenVideoOutput> Open => _open;

    /// <summary>Outputs whose canvas changed on the last sync, so the host can move their attachment.</summary>
    internal IReadOnlyList<RetargetedOutput> Retargeted => _retargeted;

    /// <summary>
    /// Outputs that are OPEN but show no canvas - a local screen created before any composition.
    /// </summary>
    /// <remarks>
    /// Its window exists and has to be painted, and nothing in the session will do it: a composition is
    /// what submits frames, and this output is on none. The host paints them black itself.
    /// </remarks>
    internal IEnumerable<OpenVideoOutput> Unattached =>
        _open.Where(open => open.CompositionId is null);

    /// <summary>The record and stream outputs, for <see cref="ProjectRecorders"/> to arm.</summary>
    internal IReadOnlyDictionary<Guid, RecordVideoOutput> Recorders => _recorders;

    /// <summary>
    /// Opens every video output the project defines and this machine can provide.
    /// </summary>
    /// <remarks>
    /// <paramref name="headless"/> is what makes this testable and what makes a booth machine with no
    /// display survive: nothing is opened, every output is reported as skipped, and the rest of the
    /// show still runs. It is not a mode the product exposes - it is what a CI box and a preview are.
    /// </remarks>
    public static ProjectVideoOutputs OpenAll(HaCueProject project, bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        var result = new ProjectVideoOutputs { _headless = headless };
        result.Sync(project);
        return result;
    }

    /// <summary>
    /// Opens whatever the project now defines and is not open yet, and closes what it no longer has.
    /// </summary>
    /// <remarks>
    /// Called on every reload, which is what makes adding an output to a RUNNING show do something.
    /// Before this it opened once at start-up, so a newly added screen stayed dark until the whole show
    /// was restarted - and an operator adding a projector mid-get-in had no way to know that.
    /// <para>
    /// Outputs already open are left ALONE. Re-opening one because an unrelated cue was edited would
    /// close and re-create the operator's projector window on a keystroke.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Sync(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var wanted = project.VideoOutputs.Select(output => output.Id).ToHashSet();

        // Gone from the document: close the window rather than leaving it on a screen with nothing in
        // the show pointing at it.
        foreach (var stale in _open.Where(open => !wanted.Contains(open.Id)).ToList())
        {
            Close(stale);
            _open.Remove(stale);
            _recorders.Remove(stale.Id);
        }

        _unopened.RemoveWhere(id => !wanted.Contains(id));

        var failures = new List<string>();
        var unopened = new HashSet<Guid>();
        var opened = new List<OpenVideoOutput>();
        var recorders = new Dictionary<Guid, RecordVideoOutput>();
        var senders = new List<NDIOutput>();
        var headless = _headless;

        _retargeted.Clear();

        foreach (var output in project.VideoOutputs)
        {
            var canvas = output.CompositionId is { } wantedCanvas
                         && project.Compositions.Any(item => item.Id == wantedCanvas)
                ? wantedCanvas
                : (Guid?)null;

            // Already open. Left alone unless the canvas it shows has CHANGED - assigning a
            // composition to an output that is already on screen has to move it, and before this the
            // open record kept its original canvas forever, so the assignment did nothing at all.
            if (_open.FirstOrDefault(open => open.Id == output.Id) is { } existing)
            {
                ApplyWindowConstraints(existing.Output, output, project);

                if (existing.CompositionId == canvas)
                    continue;

                _retargeted.Add(new RetargetedOutput(output.Id, existing.CompositionId, canvas));
                _open[_open.IndexOf(existing)] = existing with { CompositionId = canvas };
                continue;
            }

            if (canvas is not { } compositionId)
            {
                // A LOCAL SCREEN opens anyway, showing black. An output is a piece of this machine and
                // exists before any canvas is authored against it, so leaving it invisible until one
                // is assigned means the operator's only evidence that they created anything is a table
                // row. The other kinds do not: an NDI sender with nothing to send is a name on the
                // network that carries black, and a recorder with nothing to record is a file of it.
                if (output.Kind != VideoOutputKind.LocalScreen)
                {
                    failures.Add($"“{output.Name}” shows no composition");
                    unopened.Add(output.Id);
                    continue;
                }

                if (headless)
                {
                    // The honest reason, and a different one: on a box with a display this window
                    // would have opened. Reporting "shows no composition" here would describe a
                    // decision this build no longer makes.
                    failures.Add($"“{output.Name}” not opened - no display");
                    unopened.Add(output.Id);
                    continue;
                }

                // 1280×720 rather than the output's own zeros. A fullscreen output carries no window
                // size at all - the add dialog greys that field out - so passing them through opened a
                // 160×90 stub (the floor in OpenWindow) and asked the window manager to promote THAT to
                // fullscreen. A refused or slow promotion left a chip of a window nobody could find,
                // which reads exactly like an output that never opened.
                var (dark, darkProblem) = OpenWindow(output, 1280, 720);
                if (dark is not null)
                {
                    opened.Add(new OpenVideoOutput(output.Id, null, dark));
                    continue;
                }

                failures.Add($"“{output.Name}” could not open a window - {darkProblem}");
                unopened.Add(output.Id);
                continue;
            }

            if (output.Kind is VideoOutputKind.Record or VideoOutputKind.Stream)
            {
                // Opened whether or not this machine has a display: a recording is not a window, and a
                // booth box running headless is exactly where an unattended capture belongs.
                var recorder = new RecordVideoOutput();
                recorders[output.Id] = recorder;
                opened.Add(new OpenVideoOutput(output.Id, compositionId, recorder));
                continue;
            }

            if (output.Kind == VideoOutputKind.Ndi)
            {
                // Opened whether or not this machine has a display, like a recorder: an NDI feed is not
                // a window. It is also NOT armed - an NDI source is a live feed that receivers connect
                // to when they choose, so "armed" would be a switch with nothing behind it.
                try
                {
                    var sender = new NDIOutput(
                        output.TargetHint.Length > 0 ? output.TargetHint : output.Name,
                        // The composition is already the cadence owner. SDK pacing here would add a
                        // second clock and make its worker queue absorb their beat difference.
                        clockVideo: false,
                        videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks);

                    senders.Add(sender);
                    opened.Add(new OpenVideoOutput(output.Id, compositionId, sender.Video));
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    // Reported, not thrown: an NDI runtime this machine lacks must not stop the show.
                    failures.Add($"“{output.Name}”: {failure.Message}");
                    unopened.Add(output.Id);
                }

                continue;
            }

            if (headless)
            {
                failures.Add($"“{output.Name}” not opened - no display");
                unopened.Add(output.Id);
                continue;
            }

            var composition = project.Compositions.First(item => item.Id == compositionId);

            // The window's own size when the document gives one, and the COMPOSITION's otherwise: the
            // operator authored placements against that canvas, so opening at some other aspect would
            // show them a letterboxed version of their own layout on first launch.
            var (screen, screenProblem) = OpenWindow(output, composition.Width, composition.Height);
            if (screen is not null)
            {
                opened.Add(new OpenVideoOutput(output.Id, compositionId, screen));
                continue;
            }

            failures.Add($"“{output.Name}” could not open a window - {screenProblem}");
            unopened.Add(output.Id);
        }

        _open.AddRange(opened);
        _senders.AddRange(senders);

        foreach (var (id, recorder) in recorders)
            _recorders[id] = recorder;

        foreach (var id in unopened)
            _unopened.Add(id);

        // Only THIS pass's failures are kept and returned, so the host reports a newly broken output
        // once rather than re-reporting every old one on every edit - and the member list stays the
        // last sync's answer instead of an ever-growing history of duplicates.
        _failures.Clear();
        _failures.AddRange(failures);
        return failures;
    }

    /// <summary>
    /// Opens one local screen's window, placed where the document says. A null window carries the
    /// reason instead, and the CALLER records it - exactly once. This used to write its own row into
    /// the failure list while the caller wrote a second, vaguer one, so every refused window was
    /// reported twice.
    /// </summary>
    /// <remarks>
    /// The window's size falls back to <paramref name="fallbackWidth"/>×<paramref name="fallbackHeight"/>
    /// - the composition's, when it shows one, and a plain 1280×720 when it does not yet. Reported
    /// rather than thrown: one screen that will not open must not take the show down.
    /// </remarks>
    private static (SDL3GLVideoOutput? Window, string? Problem) OpenWindow(
        VideoOutputDefinition output, int fallbackWidth, int fallbackHeight)
    {
        try
        {
            var window = new SDL3GLVideoOutput(
                title: $"HaCue2 · {output.Name}",
                initialWidth: output.WindowWidth > 0 ? output.WindowWidth : Math.Max(160, fallbackWidth),
                initialHeight: output.WindowHeight > 0 ? output.WindowHeight : Math.Max(90, fallbackHeight));

            var width = output.WindowWidth > 0 ? output.WindowWidth : Math.Max(160, fallbackWidth);
            var height = output.WindowHeight > 0 ? output.WindowHeight : Math.Max(90, fallbackHeight);
            window.SetWindowConstraints(
                !output.Fullscreen && output.WindowAspectLocked,
                !output.Fullscreen && output.WindowResolutionLocked,
                width / (float)height);

            // The document says which screen and whether to fill it, so a show carried to a venue puts
            // its projector feed on the projector rather than on whichever display SDL opened first. An
            // unparseable or absent hint leaves the window where it is rather than guessing - moving a
            // feed to the wrong screen is worse than not moving it.
            if (ScreenNumber(output.TargetHint) is { } display)
                window.ApplyWindowPlacement(display - 1, output.Fullscreen, null, null);
            else if (output.Fullscreen)
                window.ApplyWindowPlacement(0, fullscreen: true, null, null);

            return (window, null);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return (null, failure.Message);
        }
    }

    private static void ApplyWindowConstraints(
        IVideoOutput opened,
        VideoOutputDefinition definition,
        HaCueProject project)
    {
        if (opened is not SDL3GLVideoOutput window)
            return;

        var composition = definition.CompositionId is { } id
            ? project.Compositions.FirstOrDefault(item => item.Id == id)
            : null;
        var width = definition.WindowWidth > 0
            ? definition.WindowWidth
            : Math.Max(160, composition?.Width ?? 1280);
        var height = definition.WindowHeight > 0
            ? definition.WindowHeight
            : Math.Max(90, composition?.Height ?? 720);

        window.SetWindowConstraints(
            !definition.Fullscreen && definition.WindowAspectLocked,
            !definition.Fullscreen && definition.WindowResolutionLocked,
            width / (float)height);
    }

    /// <summary>
    /// Which screen a local output's hint names, one-based, or null for "wherever it opens".
    /// </summary>
    /// <remarks>
    /// The hint is a NUMBER, and the leading number of a picker label is the same number: the add-output
    /// dialog used to store the whole label ("2 · 1920×1080"), so every output authored through it
    /// silently opened on whichever display SDL answered with. Reading the leading digits rescues those
    /// documents without a migration pass, and rejects anything else rather than guessing - moving a
    /// feed to the wrong screen is worse than not moving it.
    /// </remarks>
    public static int? ScreenNumber(string? hint)
    {
        var text = (hint ?? "").TrimStart();
        var digits = new string([.. text.TakeWhile(char.IsAsciiDigit)]);

        return int.TryParse(digits, out var display) && display > 0 ? display : null;
    }

    /// <summary>Closes one output's own resources. A window that will not close must not stop the rest.</summary>
    private void Close(OpenVideoOutput open)
    {
        try
        {
            (open.Output as IDisposable)?.Dispose();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Teardown must not throw out of an edit.
        }
    }

    /// <summary>The leases the session attaches, with each output's mapping resolved to its size.</summary>
    internal IEnumerable<(string CompositionId, ClipCompositionOutputLease Lease)> Leases(
        HaCueProject project)
    {
        foreach (var open in _open)
        {
            var definition = project.VideoOutputs.FirstOrDefault(item => item.Id == open.Id);

            if (definition is null || open.CompositionId is not { } canvasId)
                continue;

            var composition = project.Compositions.First(item => item.Id == canvasId);

            yield return (
                canvasId.ToString(),
                new ClipCompositionOutputLease(
                    open.OutputId,
                    definition.Name,
                    open.Output,
                    // The HOST owns these windows, so the session must not dispose them: it reloads
                    // the document on every edit, and an output disposed by a reload would close the
                    // operator's projector window on a keystroke.
                    DisposeOutputOnRuntimeDispose: false,
                    Mapping: OutputMapping.Spec(definition, composition.Width, composition.Height),
                    // A show's outputs are on for the evening, not for the duration of a cue. Without
                    // this the composition only starts pumping when something plays on it, so a
                    // freshly added projector shows nothing - and never opens its window at all.
                    PresentWhenIdle: true));
        }
    }

    public void Dispose()
    {
        foreach (var open in _open.Select(item => item.Output).OfType<IDisposable>())
        {
            try
            {
                open.Dispose();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A window that will not close cleanly must not stop the others closing, and must not
                // throw out of the show's own teardown.
            }
        }

        _open.Clear();

        // After the windows: a sender's own IVideoOutput is in the list above, so it must not be
        // disposed until nothing is still submitting to it.
        foreach (var sender in _senders)
        {
            try
            {
                sender.Dispose();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // One sender that will not close cleanly must not stop the others, and must not throw
                // out of the show's own teardown.
            }
        }

        _senders.Clear();
    }
}
