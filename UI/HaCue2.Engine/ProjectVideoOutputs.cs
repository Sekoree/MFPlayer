using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Present.SDL3;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>One output that opened, and what the session knows it as.</summary>
internal sealed record OpenVideoOutput(Guid Id, Guid CompositionId, IVideoOutput Output)
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
/// renders into from the moment the show loads, holding no file until somebody arms it — the encode
/// session is swapped in behind it, because pressing record must not restart the clips on that
/// composition. Only NDI and the recorders open when this machine has no display: neither is a window,
/// and a booth box running headless is exactly where an unattended send or capture belongs.
/// </para>
/// </remarks>
public sealed class ProjectVideoOutputs : IDisposable
{
    private readonly List<OpenVideoOutput> _open = [];
    private readonly Dictionary<Guid, RecordVideoOutput> _recorders = [];
    private readonly List<NDIOutput> _senders = [];

    private ProjectVideoOutputs()
    {
    }

    /// <summary>What could not be opened, and why — joined into the host's problem list.</summary>
    public IReadOnlyList<string> Failures { get; private init; } = [];

    internal IReadOnlyList<OpenVideoOutput> Open => _open;

    /// <summary>The record and stream outputs, for <see cref="ProjectRecorders"/> to arm.</summary>
    internal IReadOnlyDictionary<Guid, RecordVideoOutput> Recorders => _recorders;

    /// <summary>
    /// Opens every video output the project defines and this machine can provide.
    /// </summary>
    /// <remarks>
    /// <paramref name="headless"/> is what makes this testable and what makes a booth machine with no
    /// display survive: nothing is opened, every output is reported as skipped, and the rest of the
    /// show still runs. It is not a mode the product exposes — it is what a CI box and a preview are.
    /// </remarks>
    public static ProjectVideoOutputs OpenAll(HaCueProject project, bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        var failures = new List<string>();
        var opened = new List<OpenVideoOutput>();
        var recorders = new Dictionary<Guid, RecordVideoOutput>();
        var senders = new List<NDIOutput>();

        foreach (var output in project.VideoOutputs)
        {
            if (output.CompositionId is not { } compositionId
                || project.Compositions.All(item => item.Id != compositionId))
            {
                failures.Add($"“{output.Name}” shows no composition");
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
                // a window. It is also NOT armed — an NDI source is a live feed that receivers connect
                // to when they choose, so "armed" would be a switch with nothing behind it.
                try
                {
                    var sender = new NDIOutput(
                        output.TargetHint.Length > 0 ? output.TargetHint : output.Name);

                    senders.Add(sender);
                    opened.Add(new OpenVideoOutput(output.Id, compositionId, sender.Video));
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    // Reported, not thrown: an NDI runtime this machine lacks must not stop the show.
                    failures.Add($"“{output.Name}”: {failure.Message}");
                }

                continue;
            }

            if (headless)
            {
                failures.Add($"“{output.Name}” not opened — no display");
                continue;
            }

            var composition = project.Compositions.First(item => item.Id == compositionId);

            try
            {
                // Sized to the COMPOSITION, not to a nominal window size: the operator authored
                // placements against that canvas, and opening at some other aspect would show them a
                // letterboxed version of their own layout on first launch.
                var window = new SDL3GLVideoOutput(
                    title: $"HaCue2 · {output.Name}",
                    initialWidth: composition.Width,
                    initialHeight: composition.Height);

                // The document says which screen and whether to fill it, so a show carried to a venue
                // puts its projector feed on the projector rather than on whichever display SDL opened
                // first. An unparseable or absent hint leaves the window where it is rather than
                // guessing — moving a feed to the wrong screen is worse than not moving it.
                if (int.TryParse(output.TargetHint, out var display) && display > 0)
                    window.ApplyWindowPlacement(display - 1, output.Fullscreen, null, null);
                else if (output.Fullscreen)
                    window.ApplyWindowPlacement(0, fullscreen: true, null, null);

                opened.Add(new OpenVideoOutput(output.Id, compositionId, window));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Reported, not thrown: one screen that will not open must not take the show down.
                failures.Add($"“{output.Name}”: {failure.Message}");
            }
        }

        var result = new ProjectVideoOutputs { Failures = failures };
        result._open.AddRange(opened);

        foreach (var (id, recorder) in recorders)
            result._recorders[id] = recorder;

        result._senders.AddRange(senders);

        return result;
    }

    /// <summary>The leases the session attaches, with each output's mapping resolved to its size.</summary>
    internal IEnumerable<(string CompositionId, ClipCompositionOutputLease Lease)> Leases(
        HaCueProject project)
    {
        foreach (var open in _open)
        {
            var definition = project.VideoOutputs.FirstOrDefault(item => item.Id == open.Id);

            if (definition is null)
                continue;

            var composition = project.Compositions.First(item => item.Id == open.CompositionId);

            yield return (
                open.CompositionId.ToString(),
                new ClipCompositionOutputLease(
                    open.OutputId,
                    definition.Name,
                    open.Output,
                    // The HOST owns these windows, so the session must not dispose them: it reloads
                    // the document on every edit, and an output disposed by a reload would close the
                    // operator's projector window on a keystroke.
                    DisposeOutputOnRuntimeDispose: false,
                    Mapping: OutputMapping.Spec(definition, composition.Width, composition.Height)));
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
