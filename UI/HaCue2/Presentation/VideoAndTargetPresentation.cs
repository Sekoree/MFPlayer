using HaCue2.Core.Journal;
using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using S.Media.Source.Text;

namespace HaCue2.Presentation;

/// <summary>Turns compositions, outputs and their mappings into what the Video view draws.</summary>
public static class VideoPresentation
{
    /// <summary>
    /// The layers on a composition's canvas: every cue placed there, in layer order.
    /// </summary>
    /// <remarks>
    /// Placements belong to CUES, not to the composition (register item 21) - the visualizer on the
    /// Cyc canvas is simply a visualizer cue's placement. Which is why this reads the cue list rather
    /// than anything on the composition itself.
    /// </remarks>
    public static IReadOnlyList<PlacementBox> Layers(
        HaCueProject project,
        CompositionDefinition composition,
        Guid? selectedCueId = null,
        Func<MediaCueNode, MediaFacts?>? mediaFacts = null)
    {
        var boxes = new List<PlacementBox>();

        foreach (var cue in project.AllCues())
        foreach (var placement in CuePlacements.Of(cue))
        {
            if (placement.CompositionId != composition.Id)
                continue;

            var authored = new NormalizedRect(
                placement.X, placement.Y, placement.Width, placement.Height);
            var rendered = RenderedBounds(cue, placement, composition, mediaFacts) ?? authored;

            boxes.Add(new PlacementBox
            {
                SubjectId = cue.Id,
                LayerIndex = placement.LayerIndex,
                Label = $"Q{CuePresentation.Number(cue.Number)} {cue.Label} · L{placement.LayerIndex}",
                Left = rendered.X,
                Top = rendered.Y,
                Width = rendered.Width,
                Height = rendered.Height,
                AuthoredRect = rendered == authored ? null : authored,
                // Alternating gel by layer parity, so two overlapping placements stay separable at a
                // glance without inventing a per-cue colour nobody chose.
                IsSecondary = placement.LayerIndex % 2 == 1,
                IsSelected = cue.Id == selectedCueId,
            });
        }

        // Drawn in LAYER order, lowest first, so the box painted last is the one actually on top -
        // and so a click, which searches back to front, grabs what the operator sees on top. Cue order
        // would have an L1 covering an L2 whenever the L1 cue happened to come later in the list.
        return [.. boxes.OrderBy(box => box.LayerIndex)];
    }

    /// <summary>The part of a placement that the compositor actually paints.</summary>
    /// <remarks>
    /// A destination is a fitting area, not necessarily the visible picture. A square cover contained
    /// by a wide destination is still square, and a transparent text card paints only its glyphs. This
    /// mirrors the compositor's fit/crop calculation but deliberately does not clip to the composition:
    /// the editor's surrounding work area must keep an overhanging layer visible and recoverable.
    /// </remarks>
    private static NormalizedRect? RenderedBounds(
        CueNode cue,
        LayerPlacement placement,
        CompositionDefinition composition,
        Func<MediaCueNode, MediaFacts?>? mediaFacts)
    {
        if (composition.Width <= 0 || composition.Height <= 0 || placement.VideoFxEnabled && placement.VideoFx.Count > 0)
            return null;

        var sourceWidth = composition.Width;
        var sourceHeight = composition.Height;
        var content = new NormalizedRect(0, 0, 1, 1);

        switch (cue)
        {
            case MediaCueNode media:
            {
                if (media.VideoTrackIndex == -1 || mediaFacts?.Invoke(media) is not { } facts)
                    return null;

                var track = MediaFacts.Resolve(
                                facts.VideoTracks,
                                media.VideoTrackIndex,
                                media.VideoTrackSignature)
                            ?? facts.PlaceableVideoTrack;
                if (track is not { Width: > 0, Height: > 0 })
                    return null;

                sourceWidth = track.Value.Width;
                sourceHeight = track.Value.Height;
                break;
            }

            case TextCueNode text:
            {
                var spec = ShowCompiler.TextSource(text);
                sourceWidth = spec.CanvasWidth;
                sourceHeight = spec.CanvasHeight;

                // An opaque card background really does occupy the frame. A transparent card is only
                // as large as the ink (plus its outline), which is what a text placement editor needs.
                if (string.IsNullOrWhiteSpace(text.Background))
                {
                    if (TextFrameRenderer.MeasureNormalizedBounds(spec) is not { } measured
                        || measured.W <= 0 || measured.H <= 0)
                        return null;

                    var outlineX = spec.OutlineWidthPx / Math.Max(1d, sourceWidth);
                    var outlineY = spec.OutlineWidthPx / Math.Max(1d, sourceHeight);
                    var left = Math.Max(0, measured.X - outlineX);
                    var top = Math.Max(0, measured.Y - outlineY);
                    var right = Math.Min(1, measured.X + measured.W + outlineX);
                    var bottom = Math.Min(1, measured.Y + measured.H + outlineY);
                    content = new NormalizedRect(left, top, right - left, bottom - top);
                }

                break;
            }

            // A visualizer's source surface is the composition itself. The full-source result below
            // therefore collapses to the normal placement geometry while still respecting its fit.
            case VisualizerCueNode:
                break;

            default:
                return null;
        }

        return FitBounds(sourceWidth, sourceHeight, content, placement, composition);
    }

    private static NormalizedRect? FitBounds(
        int sourceWidth,
        int sourceHeight,
        NormalizedRect content,
        LayerPlacement placement,
        CompositionDefinition composition)
    {
        var sx0 = Math.Clamp(placement.CropLeft, 0, .99) * sourceWidth;
        var sy0 = Math.Clamp(placement.CropTop, 0, .99) * sourceHeight;
        var sx1 = (1 - Math.Clamp(placement.CropRight, 0, .99)) * sourceWidth;
        var sy1 = (1 - Math.Clamp(placement.CropBottom, 0, .99)) * sourceHeight;
        if (sx1 <= sx0) sx1 = Math.Min(sourceWidth, sx0 + 1);
        if (sy1 <= sy0) sy1 = Math.Min(sourceHeight, sy0 + 1);

        var dx = placement.X * composition.Width;
        var dy = placement.Y * composition.Height;
        var dw = Math.Max(1, placement.Width * composition.Width);
        var dh = Math.Max(1, placement.Height * composition.Height);
        var cw = sx1 - sx0;
        var ch = sy1 - sy0;

        var (scaleX, scaleY) = placement.Fit switch
        {
            LayerFit.Stretch => (dw / cw, dh / ch),
            LayerFit.Cover => Uniform(Math.Max(dw / cw, dh / ch)),
            LayerFit.FillWidth => Uniform(dw / cw),
            LayerFit.FillHeight => Uniform(dh / ch),
            // Center currently reaches the compositor as Contain; the editor follows the rendered
            // result rather than presenting a 1:1 promise the output does not keep.
            _ => Uniform(Math.Min(dw / cw, dh / ch)),
        };

        var imageWidth = cw * scaleX;
        var imageHeight = ch * scaleY;
        if (imageWidth > dw + .5)
        {
            var trim = (imageWidth - dw) / scaleX;
            sx0 += trim / 2;
            sx1 -= trim / 2;
            imageWidth = (sx1 - sx0) * scaleX;
        }

        if (imageHeight > dh + .5)
        {
            var trim = (imageHeight - dh) / scaleY;
            sy0 += trim / 2;
            sy1 -= trim / 2;
            imageHeight = (sy1 - sy0) * scaleY;
        }

        var ox = dx + ((dw - imageWidth) / 2);
        var oy = dy + ((dh - imageHeight) / 2);
        var contentLeft = Math.Max(sx0, content.X * sourceWidth);
        var contentTop = Math.Max(sy0, content.Y * sourceHeight);
        var contentRight = Math.Min(sx1, (content.X + content.Width) * sourceWidth);
        var contentBottom = Math.Min(sy1, (content.Y + content.Height) * sourceHeight);
        if (contentRight <= contentLeft || contentBottom <= contentTop)
            return null;

        var left = ox + ((contentLeft - sx0) * scaleX);
        var top = oy + ((contentTop - sy0) * scaleY);
        var right = ox + ((contentRight - sx0) * scaleX);
        var bottom = oy + ((contentBottom - sy0) * scaleY);

        if (Math.Abs(placement.RotationDegrees) > .000001)
        {
            var radians = placement.RotationDegrees * Math.PI / 180;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            var centerX = (placement.X + (placement.Width / 2)) * composition.Width;
            var centerY = (placement.Y + (placement.Height / 2)) * composition.Height;
            var corners = new[]
            {
                Rotate(left, top, centerX, centerY, cosine, sine),
                Rotate(right, top, centerX, centerY, cosine, sine),
                Rotate(left, bottom, centerX, centerY, cosine, sine),
                Rotate(right, bottom, centerX, centerY, cosine, sine),
            };
            left = corners.Min(point => point.X);
            top = corners.Min(point => point.Y);
            right = corners.Max(point => point.X);
            bottom = corners.Max(point => point.Y);
        }

        return new NormalizedRect(
            left / composition.Width,
            top / composition.Height,
            Math.Max(1, right - left) / composition.Width,
            Math.Max(1, bottom - top) / composition.Height).Free();
    }

    private static (double X, double Y) Uniform(double scale) => (scale, scale);

    private static (double X, double Y) Rotate(
        double x, double y, double centerX, double centerY, double cosine, double sine)
    {
        var translatedX = x - centerX;
        var translatedY = y - centerY;
        return (
            centerX + (translatedX * cosine) - (translatedY * sine),
            centerY + (translatedX * sine) + (translatedY * cosine));
    }

    /// <summary>
    /// Where the screens showing a canvas divide it, on one axis, as snap guides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What makes an output layout worth more than a picture: with the screen boundaries as snap
    /// targets, a cue can be dropped exactly onto one projector of a wall without anybody working out
    /// what fraction that is. Empty on a canvas nobody has divided - which still snaps to its own edges
    /// and centre, as every canvas does.
    /// </para>
    /// <para>
    /// Here rather than on the Video view-model because it has TWO readers that must agree: the
    /// composition's own layout, and the inspector's cue-placement canvas. A seam an operator lines a
    /// picture up against and a seam the show actually renders at have to be the same number.
    /// </para>
    /// <para>
    /// Local screens only, for the same reason they are the only ones drawn as boxes: a sender and a
    /// recorder take the whole canvas, so their "edges" are the canvas edges and offering them as
    /// guides would add nothing but duplicates.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double> SliceGuides(
        HaCueProject project, Guid compositionId, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(project);

        return
        [
            .. Screens(project, compositionId)
                .SelectMany(output =>
                {
                    var slice = Slice(output);

                    return horizontal
                        ? new[] { slice.X, slice.X + slice.Width }
                        : [slice.Y, slice.Y + slice.Height];
                })
                .Distinct()
                .Order(),
        ];
    }

    /// <summary>
    /// The sliceable outputs showing a canvas, in document order - the boxes it is divided into.
    /// </summary>
    /// <remarks>
    /// Local screens AND NDI feeds: an NDI out sending one screen's portion of a wall is the same
    /// authoring act as pointing a projector at it, and excluding it left the feed stuck on the
    /// whole canvas with no way to say otherwise. Recorders and streams still take the whole
    /// canvas - they archive the show, not a screen of it.
    /// </remarks>
    public static IReadOnlyList<VideoOutputDefinition> Screens(HaCueProject project, Guid compositionId)
    {
        ArgumentNullException.ThrowIfNull(project);

        return
        [
            .. project.VideoOutputs.Where(output =>
                output.CompositionId == compositionId
                && output.Kind is VideoOutputKind.LocalScreen or VideoOutputKind.Ndi),
        ];
    }

    /// <summary>
    /// The part of the canvas one output shows.
    /// </summary>
    /// <remarks>
    /// The bounds of every mapping section's SOURCE rectangle. A mapping may be split into many warp
    /// panels, but those panels still describe one screen in the composition layout; treating panel 1
    /// as the whole screen makes a 3×3 split appear to shrink the output to its top-left ninth after a
    /// reload. Disabled panels remain part of the bounds because disabling one is an audition aid, not
    /// a request to rearrange the physical wall. An output with no usable mapping shows the whole canvas.
    /// </remarks>
    public static NormalizedRect Slice(VideoOutputDefinition output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var sections = output.Mapping
            .Where(section =>
                double.IsFinite(section.SourceX)
                && double.IsFinite(section.SourceY)
                && double.IsFinite(section.SourceWidth)
                && double.IsFinite(section.SourceHeight)
                && section.SourceWidth > 0
                && section.SourceHeight > 0)
            .ToList();

        if (sections.Count == 0)
            return new NormalizedRect(0, 0, 1, 1);

        var left = sections.Min(section => section.SourceX);
        var top = sections.Min(section => section.SourceY);
        var right = sections.Max(section => section.SourceX + section.SourceWidth);
        var bottom = sections.Max(section => section.SourceY + section.SourceHeight);
        return new NormalizedRect(left, top, right - left, bottom - top);
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
                .FirstOrDefault(composition => composition.Id == output.CompositionId)?.Name
                // "unassigned" rather than a dash: an output showing nothing is the state EVERY output
                // starts in now that they are created before any canvas exists, so the row has to say
                // what to do about it rather than look like a value that failed to load.
                ?? "unassigned",
            Map = output.IsMapped
                ? $"{output.Mapping.Count(section => section.Enabled)}/{output.Mapping.Count} sect"
                : "clean",
            State = output.CompositionId is null
                // Not an error and not "live": an output with nothing on it is a rig that is only half
                // patched, and reporting it as absent would send somebody to check a cable.
                ? new Status("no composition", Gel.Amber)
                : runtime.AbsentVideoOutputs.Contains(output.Id)
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
            IsDisabled = !section.Enabled,
        }),
    ];

    /// <summary>Where each section lands on the output (screen 10, right canvas).</summary>
    public static IReadOnlyList<PlacementBox> MappingTarget(VideoOutputDefinition output, int selected) =>
    [
        .. output.Mapping.Select((section, index) => new PlacementBox
        {
            SubjectId = section.Id,
            Label = section.HasMesh
                ? $"{index + 1} · warp {section.MeshColumns}×{section.MeshRows}"
                : $"{index + 1}",
            Left = section.TargetX, Top = section.TargetY,
            Width = section.TargetWidth, Height = section.TargetHeight,
            IsSecondary = index % 2 == 1,
            IsSelected = index == selected,
            IsDisabled = !section.Enabled,
        }),
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
                LastSeen = runtime.LastSent.GetValueOrDefault(endpoint.Id, "-"),
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
                TriggerInputKind.Schedule => "wall clock",
                TriggerInputKind.Timecode => "MTC",
                _ => "keyboard",
            },
            Bindings = $"{input.Bindings.Count} cue{(input.Bindings.Count == 1 ? "" : "s")}",
            LastSeen = runtime.LastSeen.GetValueOrDefault(input.Id, "-"),
            State = input.Kind switch
            {
                // A hotkey is not gated by external input at all, so it is never "off".
                TriggerInputKind.Keyboard => new Status("always"),
                TriggerInputKind.OscIn => new Status("listening", Gel.Green),
                // A clock is not a port: it is neither open nor closed, it is simply watched. Saying
                // "open" would invite somebody to go looking for the device it is open ON.
                TriggerInputKind.Schedule => new Status("watching", Gel.Green),
                // The chase readout in the transport row is the honest answer to whether timecode is
                // arriving; this column only says the source is armed to act on it.
                TriggerInputKind.Timecode => new Status("watching", Gel.Green),
                _ => new Status("open", Gel.Green),
            },
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
            : "- cue no longer in the show -",
    };

    private static string Filter(TriggerBinding binding)
    {
        var parts = new List<string>();

        if (binding.Target == TriggerTarget.Parameter)
            parts.Add($"0–127 → {CuePresentation.Db(binding.RangeMin)}..{CuePresentation.Db(binding.RangeMax)}");

        if (binding.NoRepeatMs > 0)
            parts.Add($"no-repeat {binding.NoRepeatMs} ms");

        if (binding.AllowWhileTyping)
            parts.Add("global while typing");

        return parts.Count == 0 ? "-" : string.Join(" · ", parts);
    }
}
