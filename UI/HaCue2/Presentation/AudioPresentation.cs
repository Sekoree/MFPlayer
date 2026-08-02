using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Core.Validation;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>Turns the project's audio patch into the rows and matrices the Audio view binds to.</summary>
/// <remarks>
/// The two failure states screen 06 exists to catch are computed here from the document, not painted
/// in: a logical output fed by cues but patched to nothing is red, one patched to hardware that no cue
/// feeds is amber. Neither can drift out of agreement with the show, because neither is written down.
/// </remarks>
public static class AudioPresentation
{
    public static IReadOnlyList<LogicalOutputRow> LogicalOutputs(
        HaCueProject project, ShowRuntime runtime)
    {
        var rows = new List<LogicalOutputRow>();

        foreach (var channel in project.AudioPatch.LogicalChannels.OrderBy(c => c.SortOrder))
        {
            var feeding = ProjectReferences.CuesFeeding(project, channel.Id);
            var cells = project.AudioPatch.Cells.Where(cell => cell.LogicalChannelId == channel.Id).ToList();
            var group = project.AudioPatch.GroupOf(channel.Id);

            var unfed = feeding == 0 && cells.Count > 0;
            var unpatched = feeding > 0 && cells.Count == 0;

            rows.Add(new LogicalOutputRow
            {
                Id = channel.Id,
                Name = channel.Name,
                Group = group?.Name ?? "—",
                FedBy = new Status(
                    $"{feeding} cue{(feeding == 1 ? "" : "s")}",
                    unfed ? Gel.Amber : Gel.Neutral),
                PatchedTo = cells.Count == 0
                    ? new Status("nothing — silent", unpatched ? Gel.Red : Gel.Neutral)
                    : new Status(string.Join(" + ", cells.Select(cell => CellLabel(project, cell)))),
                // The meter is a RUNTIME fact. No telemetry means no bars — never an invented level.
                MeterBars = runtime.Levels.TryGetValue(channel.Id, out var level) ? level.Bars : 0,
                MeterGel = runtime.Levels.TryGetValue(channel.Id, out var hot) && hot.IsHot
                    ? Gel.Red
                    : Gel.Green,
                NameGel = unpatched ? Gel.Red : unfed ? Gel.Amber : Gel.Neutral,
            });
        }

        return rows;
    }

    /// <summary>The cues feeding one logical output, as the detail pane lists them.</summary>
    public static IReadOnlyList<string> Senders(HaCueProject project, Guid channelId) =>
    [
        .. project.AllCues().OfType<MediaCueNode>()
            .Where(cue => cue.Sends.Any(send => send.LogicalChannelId == channelId))
            .Select(cue =>
            {
                var sends = cue.Sends.Where(send => send.LogicalChannelId == channelId).ToList();
                var routing = string.Join(" ", sends.Select(send => $"{Source(send.SourceChannel)}→"));
                return $"Q{CuePresentation.Number(cue.Number)} {cue.Label} · {routing} "
                     + CuePresentation.Db(sends[0].GainDb);
            }),
    ];

    // ── the V×R patch (screen 07) ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<MatrixColumn> PatchColumns(HaCueProject project) =>
    [
        .. project.AudioPatch.LogicalChannels
            .OrderBy(channel => channel.SortOrder)
            .Select(channel => new MatrixColumn(
                Abbreviate(channel.Name),
                // A grouped column is marked because linked-delta editing applies to it: the operator
                // has to know before they drag a cell, not after.
                project.AudioPatch.GroupOf(channel.Id) is not null,
                channel.Id)),
    ];

    public static IReadOnlyList<MatrixRow> PatchRows(HaCueProject project, ShowRuntime runtime)
    {
        var channels = project.AudioPatch.LogicalChannels.OrderBy(c => c.SortOrder).ToList();
        var rows = new List<MatrixRow>();

        foreach (var line in project.AudioLines)
        {
            // Only lines that carry audio out get patch rows; a recorder with no channels would be an
            // empty band across the matrix.
            for (var channel = 0; channel < line.Channels; channel++)
            {
                var index = rows.Count;
                var cells = channels
                    .Select((logical, column) => Cell(project, logical.Id, line.Id, channel, index, column))
                    .ToList();

                // Every channel of every line gets a row, unused ones included. The patch sheet is
                // about the DEVICE: hiding spare outputs is how somebody patches into one twice, and
                // a row that vanished when its last cell was un-routed could never be clicked again.
                rows.Add(new MatrixRow(
                    $"{line.Name} · Out {channel + 1}",
                    cells,
                    runtime.AbsentLines.Contains(line.Id),
                    line.Id,
                    channel));
            }
        }

        return rows;
    }

    private static MatrixCell Cell(
        HaCueProject project, Guid channelId, Guid lineId, int lineChannel, int row, int column)
    {
        var cell = project.AudioPatch.Cells
            .FirstOrDefault(item => item.Matches(channelId, lineId, lineChannel));

        if (cell is null)
            return MatrixCell.Empty(row, column);

        if (cell.Muted)
            return MatrixCell.Mute(row, column);

        return cell.GainDb == 0
            ? MatrixCell.Unity(row, column)
            : MatrixCell.Gain(row, column, CuePresentation.Db(cell.GainDb));
    }

    // ── a cue's N×V sends (the inspector's Audio tab) ─────────────────────────────────────────

    public static IReadOnlyList<MatrixColumn> SendColumns(HaCueProject project) => PatchColumns(project);

    public static IReadOnlyList<MatrixRow> SendRows(HaCueProject project, MediaCueNode cue, int? picked)
    {
        var channels = project.AudioPatch.LogicalChannels.OrderBy(c => c.SortOrder).ToList();
        var sourceCount = Math.Max(2, cue.Sends.Count == 0 ? 2 : cue.Sends.Max(s => s.SourceChannel) + 1);
        var rows = new List<MatrixRow>();

        for (var source = 0; source < sourceCount; source++)
        {
            var row = source;
            var cells = channels.Select((channel, column) =>
            {
                var send = cue.Sends.FirstOrDefault(item =>
                    item.SourceChannel == row && item.LogicalChannelId == channel.Id);

                if (send is null)
                    return MatrixCell.Empty(row, column);
                if (send.Muted)
                    return MatrixCell.Mute(row, column);

                return send.GainDb == 0
                    ? MatrixCell.Unity(row, column)
                    : MatrixCell.Gain(row, column, CuePresentation.Db(send.GainDb), picked == row);
            }).ToList();

            rows.Add(new MatrixRow(Source(source), cells, LineChannel: source));
        }

        return rows;
    }

    /// <summary>
    /// The effective route for one source channel: source → logical → device, gains composed.
    /// </summary>
    /// <remarks>
    /// Read from the middle, which is the only reading that answers "why is this silent" without
    /// mentally multiplying two matrices.
    /// </remarks>
    /// <summary>
    /// The effective route for one source channel, as the chain the inspector draws.
    /// </summary>
    /// <remarks>
    /// Structured rather than pre-formatted strings, and VARIABLE length: a source channel can reach
    /// no outputs, one, or six, and the view was previously indexing four fixed slots with the gains
    /// typed in beside them — so it bound past the end of the list on every cue that did not happen to
    /// have exactly four routes, and showed invented decibels on the ones that did.
    /// </remarks>
    public static IReadOnlyList<RouteHop> RouteChain(
        HaCueProject project, MediaCueNode cue, int sourceChannel)
    {
        var routes = PatchOperations.EffectiveRoutes(project, cue, sourceChannel);

        return
        [
            .. routes.Select(route => new RouteHop(
                Source(route.SourceChannel),
                route.LogicalName,
                $"{route.LineName} · {route.LineChannel + 1}",
                CuePresentation.Db(route.GainDb),
                route.Muted)),
        ];
    }

    // ── devices (screen 08) ───────────────────────────────────────────────────────────────────

    public static IReadOnlyList<AudioLineRow> Lines(HaCueProject project, ShowRuntime runtime)
    {
        var rows = new List<AudioLineRow>();

        foreach (var line in project.AudioLines)
        {
            var absent = runtime.AbsentLines.Contains(line.Id);
            var isMaster = project.AudioPatch.ClockMasterLineId == line.Id;
            var carries = project.AudioPatch.Cells
                .Where(cell => cell.LineId == line.Id)
                .Select(cell => cell.LogicalChannelId)
                .Distinct()
                .Count();

            var resampled = line.SampleRate is { } rate && rate != project.AudioPatch.MixSampleRate;

            rows.Add(new AudioLineRow
            {
                Id = line.Id,
                Name = line.Name,
                Kind = $"{KindLabel(line.Kind)} · {line.DeviceHint}",
                Channels = line.Channels.ToString(),
                Rate = line.SampleRate is { } declared
                    ? new Status(
                        $"{declared:N0} {(resampled ? "· resampled" : "native")}",
                        resampled ? Gel.Amber : Gel.Neutral)
                    : new Status("—"),
                State = absent
                    ? new Status("absent on this machine", Gel.Red)
                    : new Status(isMaster ? "open · clock master" : "open", Gel.Green),
                Carries = carries == 0
                    ? "—"
                    : $"{carries} logical out{(carries == 1 ? "" : "s")}",
                NameGel = absent ? Gel.Red : Gel.Neutral,
            });
        }

        return rows;
    }

    private static string KindLabel(AudioLineKind kind) => kind switch
    {
        AudioLineKind.PortAudio => "PortAudio",
        AudioLineKind.Ndi => "NDI audio",
        AudioLineKind.FileRecord => "File",
        _ => "Live stream",
    };

    private static string CellLabel(HaCueProject project, PatchCell cell) =>
        $"{project.FindLine(cell.LineId)?.Name ?? "absent"}·{cell.LineChannel + 1}";

    /// <summary>"Src L" / "Src R" / "Src 3" — the first two channels have names people use.</summary>
    private static string Source(int channel) => channel switch
    {
        0 => "Src L",
        1 => "Src R",
        _ => $"Src {channel + 1}",
    };

    /// <summary>
    /// Matrix column heads have 46 px. "Foldback L" does not fit; "Fold L" does and still reads.
    /// </summary>
    private static string Abbreviate(string name) => name
        .Replace("Foldback", "Fold", StringComparison.Ordinal)
        .Replace("Stage cue", "Stg", StringComparison.Ordinal)
        .Replace("Orchestra", "Orch", StringComparison.Ordinal)
        .Replace("FX return", "FX", StringComparison.Ordinal)
        .Replace("Lobby", "Lby", StringComparison.Ordinal);
}
