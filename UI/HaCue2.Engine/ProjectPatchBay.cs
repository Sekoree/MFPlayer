using HaCue2.Core.Model;
using HaCue2.Machine;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg.Audio;
using S.Media.NDI;
using S.Media.Routing;

namespace HaCue2.Engine;

/// <summary>
/// The project's V×R patch, as a live audio bay.
/// </summary>
/// <remarks>
/// <para>
/// This is the SECOND of the two matrices. The first - a cue's sends onto logical outputs - travels in
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
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaCue2.ProjectPatchBay");

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

    /// <summary>Lines that could not be opened, and why - what the status bar reports.</summary>
    public IReadOnlyList<string> Failures { get; private init; } = [];

    /// <summary>
    /// Opens every patched line this machine has and wires the project's patch onto them.
    /// </summary>
    /// <remarks>
    /// Returns a bay even when nothing opened. A show with no audio device is still a show that can
    /// be edited and whose video runs; refusing to build would take the whole session down over a
    /// missing interface.
    /// </remarks>
    public static ProjectPatchBay Open(
        HaCueProject project, IAudioBackend? backend, IMediaRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var patch = project.AudioPatch;
        var channels = patch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();
        var channelIds = channels.Select(channel => channel.Id.ToString()).ToList();

        var bay = new AudioPatchBay(
            logicalChannels: Math.Max(1, channels.Count),
            mixSampleRate: patch.MixSampleRate,
            // A line whose device opens at a foreign rate joins through a resampler instead of being
            // refused; a NON-master line additionally gets adaptive-rate drift correction. Both are
            // capabilities the registry carries (FFmpeg), and the bay without them can only serve a
            // rig whose every line happens to match the mix rate exactly.
            resamplerFactory: ResamplingAudioOutput.Wrap,
            adaptiveRateWrapper: registry is { SupportsAdaptiveRateOutput: true }
                ? (router, inner, outputId, maxRateDeltaHz) =>
                {
                    var monitor = new PumpPressurePlaybackHintMonitor(router, outputId);
                    return registry.CreateAdaptiveRateOutput(
                        inner, () => monitor.HintPpmBias, maxRateDeltaHz, monitor) ?? inner;
                }
                : null);

        var failures = new List<string>();

        // Enumerated ONCE, and only to turn a name into whatever this backend calls a device. A hint
        // is a name by design; a backend wants its own id, and PortAudio's is a global index - passing
        // the name straight through made every configured line refuse to open.
        var catalog = Catalog(backend);
        var opened = new List<IAudioOutput>();
        var senders = new List<NDIOutput>();
        var openLines = new List<(Guid, int)>();
        string? monitor = null;
        string? automaticMaster = null;

        foreach (var line in project.AudioLines)
        {
            var cells = patch.Cells.Where(cell => cell.LineId == line.Id).ToList();
            if (cells.Count == 0 || backend is null)
                continue;

            // Record and stream lines are not devices. They are encode sessions, and they open when the
            // operator ARMS them rather than when the show opens - so they join the bay through
            // AttachRecorder, not here. Opening one here would hand a recording's filename pattern to
            // the audio backend as a device name and then report the show's own recorder as a missing
            // interface, which is exactly what happened before recording was implemented.
            if (line.Kind is AudioLineKind.FileRecord or AudioLineKind.Stream)
                continue;

            try
            {
                var format = new AudioFormat(line.SampleRate ?? patch.MixSampleRate, line.Channels);

                // An NDI line is a SENDER, not a device on this machine - asking the audio backend for
                // one by name would look for a sound card called "HACUE-PROG" and report the show's own
                // NDI feed as a missing interface.
                var output = line.Kind == AudioLineKind.Ndi
                    ? OpenNdi(line, format, senders)
                    : backend.CreateOutput(AudioDevices.DeviceIdFor(catalog, line.DeviceHint), format);

                // The clock master paces the whole bay, so it must be a line that natively runs at the
                // project rate - the document says which, and choosing it is a real decision. An NDI
                // sender is never it: it paces on the network's terms, not the rig's.
                //
                // But a document that names NONE is not a decision to run without one: a masterless
                // bay free-runs on the wall clock, and no two crystals agree - the device terminal's
                // ring then drops a burst every couple of seconds, which is an audible pop, for the
                // whole show ("ring full" every ~2 s in the incident log). When the document is
                // silent, the FIRST local line whose device actually opened at the mix rate takes the
                // role - the same line the operator would be told to pick - and the log says so.
                var isMaster = patch.ClockMasterLineId is { } chosen
                    ? chosen == line.Id && line.Kind != AudioLineKind.Ndi
                    : automaticMaster is null
                      && line.Kind == AudioLineKind.LocalAudio
                      && output.Format.SampleRate == patch.MixSampleRate;

                if (isMaster && patch.ClockMasterLineId is null)
                    automaticMaster = line.Name;

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
                // ALSO logged: the status bar is gone when someone reads the log of a silent show,
                // and this was the one failure list that left no trace in it.
                failures.Add($"{line.Name}: {failure.Message}");
                Trace.LogWarning(failure,
                    "audio line '{Line}' (hint '{Hint}') failed to open - the show runs without it",
                    line.Name, line.DeviceHint);
            }
        }

        // One line each way, so the log always answers "did this show have audio at all, and
        // through what": the incident log had NO device activity and no way to tell why.
        if (opened.Count == 0)
            Trace.LogWarning(
                "no audio line opened ({LineCount} defined, {FailureCount} failed) - the program bus will not be consumed and every audio cue is silent",
                project.AudioLines.Count, failures.Count);
        else
            Trace.LogInformation(
                "audio bay open: {OpenedCount} line(s) at {MixRate} Hz, clock master {Master}",
                opened.Count, patch.MixSampleRate,
                patch.ClockMasterLineId is { } masterId
                    ? $"'{project.FindLine(masterId)?.Name ?? masterId.ToString()}'"
                    : automaticMaster is not null
                        ? $"'{automaticMaster}' (automatic - the project names none; set one on the Audio patch to choose)"
                        : "NOT SET and no line is eligible (bay free-runs on the wall clock; A/V genlock is off)");

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

    /// <summary>
    /// The backend's device list, or an empty one when it cannot be asked.
    /// </summary>
    /// <remarks>
    /// A backend that will not enumerate is not a reason to refuse to open anything: every hint then
    /// resolves to null, which is "the default device" - the same answer a show with no hint gets.
    /// </remarks>
    private static IReadOnlyList<AudioDeviceInfo> Catalog(IAudioBackend? backend)
    {
        try
        {
            return backend?.EnumerateOutputDevices() ?? [];
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return [];
        }
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
    /// atomically - changed cells ramp, newly non-zero cells fade in, zeroed cells stop - and the
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
                + $"{LogicalChannelIds.Count} - reopen the show to apply that change",
            ];
        }

        // Order matters as much as count: the bus is addressed by POSITION, so a reordered channel
        // list would silently send Main L down the Sub bus. Ids in bus order are the check.
        if (!channels.Select(channel => channel.Id.ToString()).SequenceEqual(LogicalChannelIds, StringComparer.Ordinal))
            return ["the logical outputs were reordered - reopen the show to apply that change"];

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

    // ── solo to the monitor (register item 13) ────────────────────────────────────────────────

    /// <summary>The logical output the monitor is soloing, or null when it carries its own patch.</summary>
    public Guid? SoloedChannelId
    {
        get
        {
            lock (_solo)
                return _soloed;
        }
    }

    private readonly Lock _solo = new();
    private Guid? _soloed;

    /// <summary>
    /// Sends ONE logical output to the audition monitor, or gives the monitor its own patch back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The monitor's V×R row is rewritten, not tapped.</b> Everything audible arrives from the
    /// program bus through a line's own matrix, so a matrix that is unity on one bus row and zero
    /// everywhere else makes the monitor carry that output alone. That needs no new capability - it is
    /// the same live reconciliation an ordinary patch edit uses, so it fades rather than clicks.
    /// </para>
    /// <para>
    /// <b>A logical output, not a line.</b> "Why can I not hear Lobby" is a question about the bus
    /// channel; a line carries several of them mixed together, and hearing that mix does not answer it.
    /// The output is sent to EVERY monitor channel, because a mono bus heard in one ear reads as a
    /// fault rather than as an answer.
    /// </para>
    /// <para>
    /// It is monitoring: only the monitor line's own row changes, so the program mix is untouched and
    /// nothing appears in the Active list. Clearing it puts that row back from the document.
    /// </para>
    /// </remarks>
    /// <returns>Why it could not, or null on success.</returns>
    public string? Solo(HaCueProject project, Guid? channelId)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (MonitorTerminalId is not { } monitor || !Guid.TryParse(monitor, out var monitorLineId))
            return "this rig has no monitor line to solo to";

        var channels = project.AudioPatch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

        if (channels.Count != LogicalChannelIds.Count)
            return "the logical outputs changed - reopen the show before soloing";

        if (_open.FirstOrDefault(open => open.LineId == monitorLineId) is not { Channels: > 0 } monitorLine)
            return "the monitor line is not open on this machine";

        float[,] matrix;

        if (channelId is { } wanted)
        {
            var row = channels.FindIndex(channel => channel.Id == wanted);

            if (row < 0)
                return "that logical output is no longer in the show";

            matrix = new float[channels.Count, monitorLine.Channels];

            for (var column = 0; column < monitorLine.Channels; column++)
                matrix[row, column] = 1;
        }
        else
        {
            // Cleared: the monitor line's OWN cells, straight from the document, which is where it
            // started and where an operator pressing the button again expects to be.
            matrix = Matrix(
                [.. project.AudioPatch.Cells.Where(cell => cell.LineId == monitorLineId)],
                channels,
                monitorLine.Channels);
        }

        try
        {
            Bay.UpdatePatch(monitor, matrix);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            return $"the monitor line refused the solo - {failure.Message}";
        }

        lock (_solo)
            _soloed = channelId;

        return null;
    }

    /// <summary>
    /// Joins an armed recorder's sink to the bay as that line's terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recorder is patched exactly like an interface - same cells, same matrix, same code - so what
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
            return "the logical outputs changed - reopen the show before arming";

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
    /// after this returns. Detaching first is what makes the trailer complete - a session still
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
    /// Rows are LOGICAL channels in bus order, columns the line's own channels - the shape the bay
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

        // Built ONCE. This was a ToList().FindIndex per cell - a fresh list allocated and scanned for
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
