using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>
/// Turns a video output's mapping sections into the engine's mapping spec.
/// </summary>
/// <remarks>
/// <para>
/// Mapping is per OUTPUT BINDING, not per composition (register item 22): the same composition renders
/// warped to a projector and clean to a TV, so this conversion happens once per output rather than
/// once per canvas.
/// </para>
/// <para>
/// The document stores destination geometry in FRACTIONS of the output; the engine wants output
/// PIXELS. The conversion needs the output's real size, which is why it is done when the output opens
/// rather than at compile time - a fraction survives the projector being swapped for one of a
/// different resolution, and a baked pixel rectangle does not.
/// </para>
/// </remarks>
public static class OutputMapping
{
    /// <summary>
    /// The engine's spec for one output, or null when the output is a clean feed.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty section list: "no mapping" is the common case and means the canvas is
    /// presented whole, which is a different code path from "mapped, with nothing in it" - the latter
    /// would render a black output and look exactly like a dead one.
    /// </remarks>
    public static ClipOutputMappingSpec? Spec(VideoOutputDefinition output, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (width <= 0 || height <= 0)
            return null;

        // IsMapped, not Mapping.Count: an output switched to clean keeps its authored sections and
        // must still render unwarped, or "clean" would be a setting that changed nothing.
        if (!output.IsMapped)
        {
            // An NDI feed with its own raster still needs the mapping stage - it is what scales the
            // canvas onto the wire size. One full-canvas section is "clean, at that resolution".
            if (output.Kind == VideoOutputKind.Ndi && output is { NdiWidth: > 0, NdiHeight: > 0 })
            {
                return new ClipOutputMappingSpec(
                    [
                        new ClipOutputMappingSection(
                            Id: "ndi-raster",
                            Enabled: true,
                            SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                            DestX: 0, DestY: 0,
                            DestWidth: output.NdiWidth, DestHeight: output.NdiHeight,
                            RotationDegrees: 0, Opacity: 1, Brightness: 1),
                    ],
                    output.NdiWidth,
                    output.NdiHeight);
            }

            return null;
        }

        var (rasterWidth, rasterHeight) = Raster(output, width, height);
        var sections = output.Mapping
            .Select(section => Section(section, rasterWidth, rasterHeight))
            .ToList();

        return new ClipOutputMappingSpec(sections, rasterWidth, rasterHeight);
    }

    /// <summary>
    /// The pixel raster this output's destination rectangles are measured in.
    /// </summary>
    /// <remarks>
    /// The output's own when the document states one, and the composition's otherwise. The distinction
    /// is what a video wall is made of: a 1920×2160 stacked canvas split across two 1920×1080 projectors
    /// has to resolve each half against 1080, and against the canvas both halves would be described as
    /// 2160 tall and land at half height.
    /// </remarks>
    public static (int Width, int Height) Raster(VideoOutputDefinition output, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(output);

        // An NDI feed's wire raster IS its physical raster; MappingWidth stays what the local
        // kinds use, and the composition remains the honest fallback for both.
        if (output.Kind == VideoOutputKind.Ndi && output is { NdiWidth: > 0, NdiHeight: > 0 })
            return (output.NdiWidth, output.NdiHeight);

        return (
            output.MappingWidth > 0 ? output.MappingWidth : width,
            output.MappingHeight > 0 ? output.MappingHeight : height);
    }

    private static ClipOutputMappingSection Section(MappingSection section, int width, int height) =>
        new(
            Id: section.Id.ToString("N"),
            Enabled: section.Enabled,
            SrcX: section.SourceX,
            SrcY: section.SourceY,
            SrcWidth: section.SourceWidth,
            SrcHeight: section.SourceHeight,
            // Fractions to pixels. The engine measures the destination in output pixels because that
            // is what a warp mesh has to be resolved against.
            DestX: section.TargetX * width,
            DestY: section.TargetY * height,
            DestWidth: section.TargetWidth * width,
            DestHeight: section.TargetHeight * height,
            RotationDegrees: section.RotationDegrees,
            Opacity: Math.Clamp(section.Opacity, 0, 1),
            Brightness: Math.Clamp(section.Brightness, 0, 1),
            MeshColumns: section.HasMesh ? section.MeshColumns : 0,
            MeshRows: section.HasMesh ? section.MeshRows : 0,
            MeshPoints: section.HasMesh ? Points(section) : null);

    /// <summary>
    /// The mesh control points, in normalized destination-rect space.
    /// </summary>
    /// <remarks>
    /// The document stores OFFSETS from an even grid, so a section that is moved or resized carries its
    /// warp with it; the engine wants absolute positions within the destination rect. The even grid is
    /// added back here - the one place that knows both conventions.
    /// </remarks>
    private static List<ClipMeshPoint> Points(MappingSection section)
    {
        var columns = section.MeshColumns;
        var rows = section.MeshRows;
        var points = new List<ClipMeshPoint>(columns * rows);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var at = ((row * columns) + column) * 2;

                points.Add(new ClipMeshPoint(
                    ((double)column / (columns - 1)) + section.WarpOffsets[at],
                    ((double)row / (rows - 1)) + section.WarpOffsets[at + 1]));
            }
        }

        return points;
    }
}
