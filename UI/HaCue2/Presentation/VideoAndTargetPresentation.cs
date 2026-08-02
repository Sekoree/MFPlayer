using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>Turns compositions, outputs and their mappings into what the Video view draws.</summary>
public static class VideoPresentation
{
    /// <summary>
    /// The layers on a composition's canvas: every cue placed there, in layer order.
    /// </summary>
    /// <remarks>
    /// Placements belong to CUES, not to the composition (register item 21) — the visualizer on the
    /// Cyc canvas is simply a visualizer cue's placement. Which is why this reads the cue list rather
    /// than anything on the composition itself.
    /// </remarks>
    public static IReadOnlyList<PlacementBox> Layers(
        HaCueProject project, CompositionDefinition composition, Guid? selectedCueId = null)
    {
        var boxes = new List<PlacementBox>();

        foreach (var cue in project.AllCues())
        {
            var placement = cue switch
            {
                MediaCueNode media => media.Placement,
                VisualizerCueNode visualizer => visualizer.Placement,
                _ => null,
            };

            if (placement is null || placement.CompositionId != composition.Id)
                continue;

            boxes.Add(new PlacementBox
            {
                SubjectId = cue.Id,
                LayerIndex = placement.LayerIndex,
                Label = $"Q{CuePresentation.Number(cue.Number)} {cue.Label} · L{placement.LayerIndex}",
                Left = placement.X,
                Top = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                // Alternating gel by layer parity, so two overlapping placements stay separable at a
                // glance without inventing a per-cue colour nobody chose.
                IsSecondary = placement.LayerIndex % 2 == 1,
                IsSelected = cue.Id == selectedCueId,
            });
        }

        // Drawn in LAYER order, lowest first, so the box painted last is the one actually on top —
        // and so a click, which searches back to front, grabs what the operator sees on top. Cue order
        // would have an L1 covering an L2 whenever the L1 cue happened to come later in the list.
        return [.. boxes.OrderBy(box => box.LayerIndex)];
    }

    public static IReadOnlyList<VideoOutputRow> Outputs(HaCueProject project, ShowRuntime runtime) =>
    [
        .. project.VideoOutputs.Select(output => new VideoOutputRow
        {
            Id = output.Id,
            Name = output.Name,
            Kind = $"{KindLabel(output.Kind)} · {output.TargetHint}"
                 + (output.Kind == VideoOutputKind.LocalScreen && output.Fullscreen ? " · fullscreen" : ""),
            Shows = project.Compositions
                .FirstOrDefault(composition => composition.Id == output.CompositionId)?.Name ?? "—",
            Map = output.IsMapped
                ? $"warp · {output.Mapping.Count} sect"
                : "clean",
            State = runtime.AbsentVideoOutputs.Contains(output.Id)
                ? new Status(output.Required ? "absent · required" : "screen absent", Gel.Red)
                : new Status("live", Gel.Green),
        }),
    ];

    /// <summary>What each mapping section samples from the composition (screen 10, left canvas).</summary>
    public static IReadOnlyList<PlacementBox> MappingSource(VideoOutputDefinition output, int selected) =>
    [
        .. output.Mapping.Select((section, index) => new PlacementBox
        {
            SubjectId = section.Id,
            Label = $"{index + 1} · {section.Name}",
            Left = section.SourceX, Top = section.SourceY,
            Width = section.SourceWidth, Height = section.SourceHeight,
            IsSecondary = index % 2 == 1,
            IsSelected = index == selected,
        }),
    ];

    /// <summary>Where each section lands on the output (screen 10, right canvas).</summary>
    public static IReadOnlyList<PlacementBox> MappingTarget(VideoOutputDefinition output, int selected) =>
    [
        .. output.Mapping.Select((section, index) => new PlacementBox
        {
            SubjectId = section.Id,
            Label = section.WarpGrid > 0
                ? $"{index + 1} · warp {section.WarpGrid}×{section.WarpGrid}"
                : $"{index + 1}",
            Left = section.TargetX, Top = section.TargetY,
            Width = section.TargetWidth, Height = section.TargetHeight,
            IsSecondary = index % 2 == 1,
            IsSelected = index == selected,
        }),
    ];

    public static IReadOnlyList<string> SectionLabels(VideoOutputDefinition output) =>
    [
        .. output.Mapping.Select((section, index) =>
            $"▸ {index + 1} · {section.Name}"
            + (section.WarpGrid > 0 ? $" · warp {section.WarpGrid}×{section.WarpGrid}" : "")),
    ];

    private static string KindLabel(VideoOutputKind kind) => kind switch
    {
        VideoOutputKind.LocalScreen => "local",
        VideoOutputKind.Ndi => "NDI · video+audio",
        VideoOutputKind.Record => "record",
        _ => "stream",
    };
}

/// <summary>Turns endpoints and trigger inputs into the rows the Targets view binds to.</summary>
public static class TargetPresentation
{
    public static IReadOnlyList<TriggerSourceRow> Endpoints(HaCueProject project, ShowRuntime runtime)
    {
        var rows = new List<TriggerSourceRow>();

        foreach (var endpoint in project.ActionEndpoints)
        {
            var users = project.AllCues().Count(cue =>
                cue is ActionCueNode action && action.EndpointId == endpoint.Id);

            rows.Add(new TriggerSourceRow
            {
                Id = endpoint.Id,
                Name = endpoint.Name,
                Kind = endpoint.Kind == EndpointKind.OscOut
                    ? $"OSC out · {endpoint.Host}:{endpoint.Port}"
                    : "MIDI out",
                Bindings = $"{users} cue{(users == 1 ? "" : "s")}",
                LastSeen = runtime.LastSent.GetValueOrDefault(endpoint.Id, "—"),
                // An endpoint no enabled cue uses is worth saying: it is either a leftover or a cue
                // somebody forgot to point at it.
                State = users == 0
                    ? new Status("unused", Gel.Amber)
                    : new Status("reachable", Gel.Green),
            });
        }

        return rows;
    }

    public static IReadOnlyList<TriggerSourceRow> Sources(HaCueProject project, ShowRuntime runtime) =>
    [
        .. project.TriggerInputs.Select(input => new TriggerSourceRow
        {
            Id = input.Id,
            Name = input.Name,
            Kind = input.Kind switch
            {
                TriggerInputKind.MidiIn => "MIDI in",
                TriggerInputKind.OscIn => $"OSC in · :{input.Port}",
                _ => "keyboard",
            },
            Bindings = $"{input.Bindings.Count} cue{(input.Bindings.Count == 1 ? "" : "s")}",
            LastSeen = runtime.LastSeen.GetValueOrDefault(input.Id, "—"),
            State = input.Kind == TriggerInputKind.Keyboard
                ? new Status("always")
                : new Status(input.Kind == TriggerInputKind.OscIn ? "listening" : "open", Gel.Green),
        }),
    ];

    public static IReadOnlyList<BindingRow> Bindings(HaCueProject project, TriggerInputDefinition input) =>
    [
        .. input.Bindings.Select(binding => new BindingRow(
            binding.Input,
            Describe(project, binding),
            Filter(binding))),
    ];

    private static string Describe(HaCueProject project, TriggerBinding binding) => binding.Target switch
    {
        TriggerTarget.Parameter => $"{binding.ParameterId} (ride)",
        TriggerTarget.Transport => "GO",
        _ => binding.TargetCueId is { } id && project.FindCue(id) is { } cue
            ? $"Q{CuePresentation.Number(cue.Number)} {cue.Label}"
            // A binding whose cue is gone says so rather than showing a blank: it is exactly the case
            // the reverse-reference query exists to make visible.
            : "— cue no longer in the show —",
    };

    private static string Filter(TriggerBinding binding)
    {
        var parts = new List<string>();

        if (binding.Target == TriggerTarget.Parameter)
            parts.Add($"0–127 → {CuePresentation.Db(binding.RangeMin)}..{CuePresentation.Db(binding.RangeMax)}");

        if (binding.NoRepeatMs > 0)
            parts.Add($"no-repeat {binding.NoRepeatMs} ms");

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
