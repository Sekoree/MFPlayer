using HaCue2.Core.Model;
using S.Media.Core.Video;
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
/// The project's video outputs, as real windows.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ProjectPatchBay"/> on the video side, and it follows the same rules:
/// an output this machine cannot open is REPORTED and skipped rather than silently redirected, and one
/// failing output never stops the others. A composition sent to whatever screen happens to answer is
/// the video equivalent of playing the show into the wrong room.
/// </para>
/// <para>
/// <b>Record and stream outputs open too</b>, as <see cref="RecordVideoOutput"/>s the compositor renders
/// into from the moment the show loads. They hold no file until somebody arms them — the encode session
/// is swapped in behind the output — because pressing record must not restart the clips on that
/// composition. NDI remains named in the document and reported as not-yet-openable rather than quietly
/// dropped: a video view that lists four outputs while one does nothing is worse than one that says so.
/// </para>
/// </remarks>
public sealed class ProjectVideoOutputs : IDisposable
{
    private readonly List<OpenVideoOutput> _open = [];
    private readonly Dictionary<Guid, RecordVideoOutput> _recorders = [];

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

            if (output.Kind != VideoOutputKind.LocalScreen)
            {
                failures.Add($"“{output.Name}” is a {Describe(output.Kind)} output — not implemented yet");
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

    private static string Describe(VideoOutputKind kind) => kind switch
    {
        VideoOutputKind.Ndi => "NDI",
        VideoOutputKind.Record => "recording",
        VideoOutputKind.Stream => "streaming",
        _ => "screen",
    };

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
    }
}
