using HaCue2.Core.Model;
using S.Media.Core.Audio;
using S.Media.NDI;
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

    /// <summary>NDI senders this bay opened, disposed after the terminals that submit to them.</summary>
    private readonly List<NDIOutput> _senders = [];

    /// <summary>The lines that actually opened, and how wide each one turned out to be.</summary>
    /// <remarks>
    /// Kept so <see cref="Apply"/> can rebuild exactly the matrices that are live. The width comes from
    /// the LINE rather than from the document because a device may have opened at a different channel
    /// count than the document asked for, and a matrix sized to the document would be rejected.
    /// </remarks>
    private readonly List<(Guid LineId, int Channels)> _open = [];

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
        var senders = new List<NDIOutput>();
        var openLines = new List<(Guid, int)>();
        string? monitor = null;

        foreach (var line in project.AudioLines)
        {
            var cells = patch.Cells.Where(cell => cell.LineId == line.Id).ToList();
            if (cells.Count == 0 || backend is null)
                continue;

            // Record and stream lines are not devices. They are encode sessions, and they open when the
            // operator ARMS them rather than when the show opens — so they join the bay through
            // AttachRecorder, not here. Opening one here would hand a recording's filename pattern to
            // the audio backend as a device name and then report the show's own recorder as a missing
            // interface, which is exactly what happened before recording was implemented.
            if (line.Kind is AudioLineKind.FileRecord or AudioLineKind.Stream)
                continue;

            try
            {
                var format = new AudioFormat(line.SampleRate ?? patch.MixSampleRate, line.Channels);

                // An NDI line is a SENDER, not a device on this machine — asking the audio backend for
                // one by name would look for a sound card called "HACUE-PROG" and report the show's own
                // NDI feed as a missing interface.
                var output = line.Kind == AudioLineKind.Ndi
                    ? OpenNdi(line, format, senders)
                    : backend.CreateOutput(line.DeviceHint.Length > 0 ? line.DeviceHint : null, format);

                // The clock master paces the whole bay, so it must be a line that natively runs at the
                // project rate — the document says which, and it is a real decision, not a default.
                // An NDI sender is never it: it paces on the network's terms, not the rig's.
                var isMaster = patch.ClockMasterLineId == line.Id && line.Kind != AudioLineKind.Ndi;

                bay.AddTerminal(
                    line.Id.ToString(),
                    output,
                    Matrix(cells, channels, line.Channels),
                    isMaster);

                opened.Add(output);
                openLines.Add((line.Id, output.Format.Channels));
                monitor ??= line.Id.ToString();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Reported, not thrown: one absent interface must not stop the rest of the rig.
                failures.Add($"{line.Name}: {failure.Message}");
            }
        }

        if (opened.Count > 0)
        {
            // Metering is switched on for the life of the show rather than when a meter becomes
            // visible: the Output info drawer is summoned exactly when something sounds wrong, and a
            // meter that starts measuring at that moment has no history to show for the seconds that
            // prompted it. The cost is a peak/RMS pass over a buffer the bus already has in cache.
            bay.EnableProgramMetering();
            bay.Play();
        }

        var result = new ProjectPatchBay(bay, channelIds, monitor) { Failures = failures };
        result._outputs.AddRange(opened);
        result._open.AddRange(openLines);
        result._senders.AddRange(senders);
        return result;
    }

    /// <summary>Opens an NDI sender's audio side as an ordinary bay terminal.</summary>
    private static IAudioOutput OpenNdi(
        AudioLineDefinition line, AudioFormat format, List<NDIOutput> senders)
    {
        var sender = new NDIOutput(line.DeviceHint.Length > 0 ? line.DeviceHint : line.Name);

        try
        {
            var audio = sender.EnableAudio(format);
            senders.Add(sender);
            return audio;
        }
        catch
        {
            sender.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pushes the project's current V×R patch onto the live bay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half that makes "changing the real-output patch does not rebuild active cue
    /// transport" true. <see cref="AudioPatchBay.UpdatePatch"/> reconciles one terminal's matrix
    /// atomically — changed cells ramp, newly non-zero cells fade in, zeroed cells stop — and the
    /// producers behind it are never touched, so a cell can be re-patched under a sounding cue.
    /// </para>
    /// <para>
    /// <b>Adding or removing a LOGICAL output is refused while live</b> and reported rather than
    /// applied. The bus width is fixed when the bay is built, so a matrix with a different row count
    /// would be rejected by the terminal anyway; saying so is better than a patch that silently stops
    /// tracking the document. Re-opening the show adopts the new width.
    /// </para>
    /// </remarks>
    /// <returns>What could not be applied, for the operator. Empty when the patch is now live.</returns>
    public IReadOnlyList<string> Apply(HaCueProject project) =>
        Apply(project, project?.AudioPatch.Cells ?? []);

    /// <summary>
    /// Pushes an explicit set of cells onto the live bay, using the project only for its channel order.
    /// </summary>
    /// <remarks>
    /// The overload a RAMP needs: a patch cue's fade pushes intermediate states that deliberately do
    /// not exist in the document, and the document is written once with where the fade lands.
    /// </remarks>
    public IReadOnlyList<string> Apply(HaCueProject project, IReadOnlyList<PatchCell> cells)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(cells);

        var channels = project.AudioPatch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

        if (channels.Count != LogicalChannelIds.Count)
        {
            return
            [
                $"the project now has {channels.Count} logical output(s) and the running bay has "
                + $"{LogicalChannelIds.Count} — reopen the show to apply that change",
            ];
        }

        // Order matters as much as count: the bus is addressed by POSITION, so a reordered channel
        // list would silently send Main L down the Sub bus. Ids in bus order are the check.
        if (!channels.Select(channel => channel.Id.ToString()).SequenceEqual(LogicalChannelIds, StringComparer.Ordinal))
            return ["the logical outputs were reordered — reopen the show to apply that change"];

        var failures = new List<string>();

        foreach (var (lineId, lineChannels) in _open)
        {
            var forLine = cells.Where(cell => cell.LineId == lineId).ToList();

            try
            {
                Bay.UpdatePatch(lineId.ToString(), Matrix(forLine, channels, lineChannels));
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
            {
                // One terminal refusing its matrix must not stop the others being updated: a partly
                // applied patch the operator is told about beats a wholly stale one they are not.
                failures.Add($"{project.FindLine(lineId)?.Name ?? lineId.ToString()}: {failure.Message}");
            }
        }

        return failures;
    }

    /// <summary>
    /// Joins an armed recorder's sink to the bay as that line's terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recorder is patched exactly like an interface — same cells, same matrix, same code — so what
    /// gets recorded is what the operator patched to it, and "record the foldback mix" needs no feature
    /// of its own. The bay takes a terminal while running and fades it in, so arming mid-show neither
    /// interrupts the program nor starts the file with a click.
    /// </para>
    /// <para>
    /// The line is registered as open so <see cref="Apply"/> keeps its matrix current: a patch change
    /// made while recording has to reach the recording, or the file stops matching the show halfway
    /// through.
    /// </para>
    /// </remarks>
    /// <returns>Why it could not be attached, or null on success.</returns>
    public string? AttachRecorder(HaCueProject project, Guid lineId, IAudioOutput sink)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sink);

        if (_open.Any(item => item.LineId == lineId))
            return "that line is already attached";

        var channels = project.AudioPatch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

        if (channels.Count != LogicalChannelIds.Count)
            return "the logical outputs changed — reopen the show before arming";

        var cells = project.AudioPatch.Cells.Where(cell => cell.LineId == lineId).ToList();

        try
        {
            Bay.AddTerminal(lineId.ToString(), sink, Matrix(cells, channels, sink.Format.Channels));
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            return failure.Message;
        }

        _open.Add((lineId, sink.Format.Channels));
        return null;
    }

    /// <summary>
    /// Removes an armed recorder's terminal, on disarm.
    /// </summary>
    /// <remarks>
    /// The sink is BORROWED, as every terminal is: the recorder owns the encode session and flushes it
    /// after this returns. Detaching first is what makes the trailer complete — a session still
    /// attached to a running bay would be written to while it was being finalized.
    /// </remarks>
    public void DetachRecorder(Guid lineId)
    {
        Bay.RemoveTerminal(lineId.ToString());
        _open.RemoveAll(item => item.LineId == lineId);
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

        // Built ONCE. This was a ToList().FindIndex per cell — a fresh list allocated and scanned for
        // every cell of every line, on every reload, and a reload happens every 300 ms while somebody
        // drags a patch cell.
        var rows = new Dictionary<Guid, int>(channels.Count);

        for (var index = 0; index < channels.Count; index++)
            rows[channels[index].Id] = index;

        foreach (var cell in cells)
        {
            var row = rows.GetValueOrDefault(cell.LogicalChannelId, -1);

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

        // After the terminals, which are the senders' own audio sides.
        foreach (var sender in _senders)
        {
            try
            {
                sender.Dispose();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // One sender that will not close must not stop the others, or the show's teardown.
            }
        }

        _senders.Clear();
    }
}
