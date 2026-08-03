using HaCue2.Core.Model;
using S.Media.Core.Audio;
using S.Media.Routing;

namespace HaCue2.Engine;

/// <summary>
/// The project's V×R patch, as a live audio bay.
/// </summary>
/// <remarks>
/// <para>
/// This is the SECOND of the two matrices. The first — a cue's sends onto logical outputs — travels in
/// the document as <c>ShowClipLogicalSend</c> and is applied by the session. This one, logical outputs
/// onto real device channels, is a property of the RIG rather than of the show, which is why it is
/// built here from the project's lines and never compiled into the document.
/// </para>
/// <para>
/// One bay TERMINAL per audio line, each with its own logical→device matrix. A line that is not on
/// this machine is skipped rather than opened against the default device: sending Main L/R to
/// whatever happens to answer is how a show ends up in the wrong room.
/// </para>
/// </remarks>
public sealed class ProjectPatchBay : IDisposable
{
    private readonly List<IAudioOutput> _outputs = [];

    private ProjectPatchBay(AudioPatchBay bay, IReadOnlyList<string> channelIds, string? monitor)
    {
        Bay = bay;
        LogicalChannelIds = channelIds;
        MonitorTerminalId = monitor;
    }

    public AudioPatchBay Bay { get; }

    /// <summary>The bus order the session addresses logical channels by. Ids, never names.</summary>
    public IReadOnlyList<string> LogicalChannelIds { get; }

    /// <summary>Where audition monitors, or null when no line offered itself.</summary>
    public string? MonitorTerminalId { get; }

    /// <summary>Lines that could not be opened, and why — what the status bar reports.</summary>
    public IReadOnlyList<string> Failures { get; private init; } = [];

    /// <summary>
    /// Opens every patched line this machine has and wires the project's patch onto them.
    /// </summary>
    /// <remarks>
    /// Returns a bay even when nothing opened. A show with no audio device is still a show that can
    /// be edited and whose video runs; refusing to build would take the whole session down over a
    /// missing interface.
    /// </remarks>
    public static ProjectPatchBay Open(HaCueProject project, IAudioBackend? backend)
    {
        ArgumentNullException.ThrowIfNull(project);

        var patch = project.AudioPatch;
        var channels = patch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();
        var channelIds = channels.Select(channel => channel.Id.ToString()).ToList();

        var bay = new AudioPatchBay(
            logicalChannels: Math.Max(1, channels.Count),
            mixSampleRate: patch.MixSampleRate);

        var failures = new List<string>();
        var opened = new List<IAudioOutput>();
        string? monitor = null;

        foreach (var line in project.AudioLines)
        {
            var cells = patch.Cells.Where(cell => cell.LineId == line.Id).ToList();
            if (cells.Count == 0 || backend is null)
                continue;

            try
            {
                var format = new AudioFormat(line.SampleRate ?? patch.MixSampleRate, line.Channels);
                var output = backend.CreateOutput(line.DeviceHint.Length > 0 ? line.DeviceHint : null, format);

                // The clock master paces the whole bay, so it must be a line that natively runs at the
                // project rate — the document says which, and it is a real decision, not a default.
                var isMaster = patch.ClockMasterLineId == line.Id;

                bay.AddTerminal(
                    line.Id.ToString(),
                    output,
                    Matrix(cells, channels, line.Channels),
                    isMaster);

                opened.Add(output);
                monitor ??= line.Id.ToString();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Reported, not thrown: one absent interface must not stop the rest of the rig.
                failures.Add($"{line.Name}: {failure.Message}");
            }
        }

        if (opened.Count > 0)
            bay.Play();

        var result = new ProjectPatchBay(bay, channelIds, monitor) { Failures = failures };
        result._outputs.AddRange(opened);
        return result;
    }

    /// <summary>
    /// The logical→device gain matrix for one line.
    /// </summary>
    /// <remarks>
    /// Rows are LOGICAL channels in bus order, columns the line's own channels — the shape the bay
    /// wants. Gains are linear here because a matrix is multiplied, not added; the document's decibels
    /// are converted once, at the boundary. A muted cell is a zero rather than an absent one, so
    /// unmuting is a value change instead of a re-patch.
    /// </remarks>
    private static float[,] Matrix(
        IReadOnlyList<PatchCell> cells,
        IReadOnlyList<LogicalAudioChannel> channels,
        int lineChannels)
    {
        var matrix = new float[Math.Max(1, channels.Count), Math.Max(1, lineChannels)];

        foreach (var cell in cells)
        {
            var row = channels.ToList().FindIndex(channel => channel.Id == cell.LogicalChannelId);

            if (row < 0 || cell.LineChannel < 0 || cell.LineChannel >= lineChannels)
                continue;

            matrix[row, cell.LineChannel] = cell.Muted
                ? 0f
                : (float)(cell.GainDb <= GainRange.SilenceFloorDb ? 0 : Math.Pow(10, cell.GainDb / 20));
        }

        return matrix;
    }

    public void Dispose()
    {
        Bay.Dispose();

        // The bay owns the terminals it was given; an output that also happens to be disposable is
        // released here, and one that is not simply has nothing to release.
        foreach (var output in _outputs.OfType<IDisposable>())
            output.Dispose();

        _outputs.Clear();
    }
}
