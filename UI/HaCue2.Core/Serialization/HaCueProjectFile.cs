using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaCue2.Core.Model;

namespace HaCue2.Core.Serialization;

/// <summary>
/// Reads and writes <c>.hacue2proj</c> files, and computes the hash the dirty flag hangs off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic on write.</b> The show file is written to a sibling temp file and then moved over the
/// original, so a crash or a full disk mid-save leaves the previous show intact. Writing in place is
/// how an autosave every 30 s eventually eats somebody's project.
/// </para>
/// <para>
/// <b>Tolerant below, closed above.</b> An older document loads, because every addition to this schema
/// is additive and nullable. A NEWER one is refused with a clear message rather than partially read -
/// this build cannot know what it would be ignoring, and a show that silently lost its patch on open
/// is worse than one that refuses to open.
/// </para>
/// </remarks>
public static class HaCueProjectFile
{
    /// <summary>The extension the launcher and the file dialogs use.</summary>
    public const string Extension = ".hacue2proj";

    /// <summary>Serializes to the exact bytes <see cref="SaveAsync"/> would write.</summary>
    public static string Serialize(HaCueProject project) =>
        JsonSerializer.Serialize(project, HaCueProjectJsonContext.Default.HaCueProject);

    public static HaCueProject Deserialize(string json)
    {
        RequireSupportedVersion(json);
        var project = JsonSerializer.Deserialize(json, HaCueProjectJsonContext.Default.HaCueProject)
                      ?? throw new HaCueProjectFormatException("The project file is empty.");
        RepairSplitDestinationWriteback(project);
        foreach (var cue in project.AllCues())
            foreach (var placement in CuePlacements.Of(cue))
                LayerEffectRack.MigrateLegacy(placement);
        AutomationMigration.Migrate(project);
        return project;
    }

    /// <summary>
    /// Repairs files written by the short-lived splitter build which allowed the mounted layout
    /// editor to restore panel 1's old full-raster destination after creating an otherwise-correct
    /// source-and-destination grid.
    /// </summary>
    /// <remarks>
    /// This deliberately recognises only the split command's exact generated names and regular source
    /// geometry, requires every later destination tile to be correct, and changes only an identity
    /// first destination. A hand-authored mapping therefore does not get "helpfully" rearranged.
    /// </remarks>
    private static void RepairSplitDestinationWriteback(HaCueProject project)
    {
        const double tolerance = 0.0000001;

        foreach (var output in project.VideoOutputs)
        {
            var mapping = output.Mapping;
            if (mapping.Count < 2
                || mapping.Any(section =>
                    !section.Enabled
                    || !Near(section.RotationDegrees, 0, tolerance)
                    || !Near(section.Opacity, 1, tolerance)
                    || !Near(section.Brightness, 1, tolerance)
                    || section.MeshColumns > 0
                    || section.MeshRows > 0
                    || section.WarpOffsets is { Count: > 0 }))
                continue;

            var (rows, columns) = GeneratedGridDimensions(mapping);
            if (rows <= 0 || columns <= 0 || rows * columns != mapping.Count)
                continue;

            var left = mapping.Min(section => section.SourceX);
            var top = mapping.Min(section => section.SourceY);
            var right = mapping.Max(section => section.SourceX + section.SourceWidth);
            var bottom = mapping.Max(section => section.SourceY + section.SourceHeight);
            var sourceWidth = right - left;
            var sourceHeight = bottom - top;
            if (!double.IsFinite(left) || !double.IsFinite(top)
                || !double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight)
                || sourceWidth <= 0 || sourceHeight <= 0)
                continue;

            var regular = true;
            for (var index = 0; index < mapping.Count; index++)
            {
                var row = index / columns;
                var column = index % columns;
                var section = mapping[index];
                regular &= Near(section.SourceX, left + (sourceWidth * column / columns), tolerance)
                           && Near(section.SourceY, top + (sourceHeight * row / rows), tolerance)
                           && Near(section.SourceWidth, sourceWidth / columns, tolerance)
                           && Near(section.SourceHeight, sourceHeight / rows, tolerance);

                if (index > 0)
                {
                    regular &= Near(section.TargetX, (double)column / columns, tolerance)
                               && Near(section.TargetY, (double)row / rows, tolerance)
                               && Near(section.TargetWidth, 1d / columns, tolerance)
                               && Near(section.TargetHeight, 1d / rows, tolerance);
                }
            }

            var first = mapping[0];
            if (!regular
                || !Near(first.TargetX, 0, tolerance)
                || !Near(first.TargetY, 0, tolerance)
                || !Near(first.TargetWidth, 1, tolerance)
                || !Near(first.TargetHeight, 1, tolerance))
                continue;

            first.TargetWidth = 1d / columns;
            first.TargetHeight = 1d / rows;
        }
    }

    private static (int Rows, int Columns) GeneratedGridDimensions(IReadOnlyList<MappingSection> mapping)
    {
        if (mapping[0].Name == "Panel 1")
        {
            for (var index = 0; index < mapping.Count; index++)
                if (mapping[index].Name != $"Panel {index + 1}")
                    return (0, 0);

            return (1, mapping.Count);
        }

        for (var columns = 1; columns <= mapping.Count; columns++)
        {
            if (mapping.Count % columns != 0)
                continue;

            var rows = mapping.Count / columns;
            var namesMatch = true;
            for (var index = 0; index < mapping.Count; index++)
            {
                var row = index / columns;
                var column = index % columns;
                if (mapping[index].Name != $"Panel r{row + 1} c{column + 1}")
                {
                    namesMatch = false;
                    break;
                }
            }

            if (namesMatch)
                return (rows, columns);
        }

        return (0, 0);
    }

    private static bool Near(double left, double right, double tolerance) =>
        double.IsFinite(left) && Math.Abs(left - right) <= tolerance;

    /// <summary>
    /// The project hash: SHA-256 over the serialized document.
    /// </summary>
    /// <remarks>
    /// Over the SERIALIZED form rather than the object graph, because "has this project changed" has
    /// to mean "would saving it produce a different file". A structural hash would report a change for
    /// something that does not survive the write, and miss one that only appears in it.
    /// </remarks>
    public static string ComputeHash(HaCueProject project) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(project))));

    /// <summary>
    /// Serializes and writes. The serialization happens SYNCHRONOUSLY, before the first await - a
    /// caller on the UI thread therefore reads the document atomically, and edits made while the
    /// disk write is still in flight cannot tear the bytes being written. Callers that must also
    /// know whether such edits happened (the manual save's dirty flag) serialize themselves and use
    /// <see cref="SaveSerializedAsync"/> with a journal revision check.
    /// </summary>
    public static Task SaveAsync(HaCueProject project, string path, CancellationToken ct = default) =>
        SaveSerializedAsync(Serialize(project), path, ct);

    /// <summary>
    /// Writes already-serialized bytes atomically and durably.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same directory as the target, so the move is a rename within one filesystem and therefore
    /// atomic. A temp file in the system temp dir would make this a copy across devices, which is
    /// exactly the non-atomic write we are avoiding.
    /// </para>
    /// <para>
    /// A UNIQUE temp name per write: the fixed <c>.tmp</c> sibling meant two overlapping saves (a
    /// double Ctrl+S, or a manual save racing an autosave of the same show) wrote into one file and
    /// whichever finished second could move a torn mix of both. And flushed to disk before the
    /// rename, so a power cut straight after a "successful" save cannot leave a valid-looking rename
    /// over unwritten data - the previous file survives instead.
    /// </para>
    /// </remarks>
    public static async Task SaveSerializedAsync(string json, string path, CancellationToken ct = default)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Never leave a stray temp beside the show; the target is untouched either way.
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static async Task<HaCueProject> LoadAsync(string path, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Deserialize(json);
    }

    /// <summary>
    /// Reads the schema version before deserializing, so an unreadable document produces "written by a
    /// newer HaCue2" rather than whatever type error its new shapes happen to cause first.
    /// </summary>
    private static void RequireSupportedVersion(string json)
    {
        int version;
        try
        {
            using var document = JsonDocument.Parse(json);
            version = document.RootElement.TryGetProperty("schemaVersion", out var element)
                      && element.TryGetInt32(out var value)
                ? value
                : HaCueProject.CurrentSchemaVersion;
        }
        catch (JsonException error)
        {
            throw new HaCueProjectFormatException("The project file is not valid JSON.", error);
        }

        if (version > HaCueProject.CurrentSchemaVersion)
            throw new HaCueProjectFormatException(
                $"This project was written by a newer HaCue2 (schema {version}; this build reads up to "
                + $"{HaCueProject.CurrentSchemaVersion}). Update HaCue2 to open it.");

        if (version < HaCueProject.MinimumSupportedSchemaVersion)
            throw new HaCueProjectFormatException(
                $"This project uses schema {version}, which this build no longer reads (minimum "
                + $"{HaCueProject.MinimumSupportedSchemaVersion}).");
    }
}

/// <summary>A project file that cannot be read, with a message meant for an operator.</summary>
public sealed class HaCueProjectFormatException : Exception
{
    public HaCueProjectFormatException(string message) : base(message)
    {
    }

    public HaCueProjectFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The source-generated serializer for the project document.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based so the app stays NativeAOT-clean, matching the rest
/// of the repo. Every derived cue kind is listed because the generator only walks types it can see,
/// and a missing one fails at run time on the first document that contains it - not at build time.
/// <para>
/// Three settings are decisions, not defaults:
/// </para>
/// <list type="bullet">
///   <item><b>camelCase</b> - safe because this format is new and HaCue2 is its only reader. The
///   engine's <c>ShowDocument</c> deliberately has no naming policy, since its property names ARE its
///   wire names for an external C ABI consumer; that constraint does not apply here.</item>
///   <item><b>String enums</b> - a numeric enum silently changes meaning when someone inserts a member,
///   and this document has no external reader forcing the numeric form on it. A show file that says
///   <c>"skipOnward"</c> also survives being read by a human during a support call.</item>
///   <item><b>No <c>WhenWritingDefault</c></b> - omitting CLR defaults would drop <c>enabled: false</c>
///   from the file, and reading it back would hit the property initializer and turn the cue back on:
///   a cue disabled for a performance silently firing in the next one. Every property is written.</item>
/// </list>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(HaCueProject))]
[JsonSerializable(typeof(MediaCueNode))]
[JsonSerializable(typeof(GroupCueNode))]
[JsonSerializable(typeof(ActionCueNode))]
[JsonSerializable(typeof(AutomationCueNode))]
[JsonSerializable(typeof(FadeCueNode))]
[JsonSerializable(typeof(JumpCueNode))]
[JsonSerializable(typeof(VisualizerCueNode))]
[JsonSerializable(typeof(PatchCueNode))]
[JsonSerializable(typeof(CommentCueNode))]
[JsonSerializable(typeof(TextCueNode))]
internal sealed partial class HaCueProjectJsonContext : JsonSerializerContext;
