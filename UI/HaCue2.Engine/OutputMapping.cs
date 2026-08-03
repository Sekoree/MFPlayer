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
/// rather than at compile time — a fraction survives the projector being swapped for one of a
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
    /// presented whole, which is a different code path from "mapped, with nothing in it" — the latter
    /// would render a black output and look exactly like a dead one.
    /// </remarks>
    public static ClipOutputMappingSpec? Spec(VideoOutputDefinition output, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (output.Mapping.Count == 0 || width <= 0 || height <= 0)
            return null;

        var sections = output.Mapping.Select(section => Section(section, width, height)).ToList();

        return new ClipOutputMappingSpec(sections, width, height);
    }

    private static ClipOutputMappingSection Section(MappingSection section, int width, int height) =>
        new(
            Id: section.Id.ToString("N"),
            // Every authored section is enabled: the document has no disable flag, and inventing one
            // that is always true would suggest the UI could turn a section off when it cannot.
            Enabled: true,
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
            MeshColumns: Mesh(section) ? section.WarpGrid : 0,
            MeshRows: Mesh(section) ? section.WarpGrid : 0,
            MeshPoints: Mesh(section) ? Points(section) : null);

    /// <summary>
    /// Whether this section actually carries a warp.
    /// </summary>
    /// <remarks>
    /// A grid of 0 or 1 is not a mesh, and a grid whose offsets have never been touched is an identity
    /// the resolver would discard anyway. Both cases are reported as no-mesh so the affine path is
    /// taken, which every backend supports — the warp path is GL-only.
    /// </remarks>
    private static bool Mesh(MappingSection section) =>
        section.WarpGrid >= 2 && section.WarpOffsets.Count == section.WarpGrid * section.WarpGrid * 2;

    /// <summary>
    /// The mesh control points, in normalized destination-rect space.
    /// </summary>
    /// <remarks>
    /// The document stores OFFSETS from an even grid, so a section that is moved or resized carries its
    /// warp with it; the engine wants absolute positions within the destination rect. The even grid is
    /// added back here — the one place that knows both conventions.
    /// </remarks>
    private static List<ClipMeshPoint> Points(MappingSection section)
    {
        var grid = section.WarpGrid;
        var points = new List<ClipMeshPoint>(grid * grid);

        for (var row = 0; row < grid; row++)
        {
            for (var column = 0; column < grid; column++)
            {
                var at = ((row * grid) + column) * 2;

                points.Add(new ClipMeshPoint(
                    ((double)column / (grid - 1)) + section.WarpOffsets[at],
                    ((double)row / (grid - 1)) + section.WarpOffsets[at + 1]));
            }
        }

        return points;
    }
}
