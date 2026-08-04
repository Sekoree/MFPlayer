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
/// is additive and nullable. A NEWER one is refused with a clear message rather than partially read —
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
        return JsonSerializer.Deserialize(json, HaCueProjectJsonContext.Default.HaCueProject)
               ?? throw new HaCueProjectFormatException("The project file is empty.");
    }

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

    public static async Task SaveAsync(HaCueProject project, string path, CancellationToken ct = default)
    {
        var json = Serialize(project);

        // Same directory as the target, so the move is a rename within one filesystem and therefore
        // atomic. A temp file in the system temp dir would make this a copy across devices, which is
        // exactly the non-atomic write we are avoiding.
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
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
/// and a missing one fails at run time on the first document that contains it — not at build time.
/// <para>
/// Three settings are decisions, not defaults:
/// </para>
/// <list type="bullet">
///   <item><b>camelCase</b> — safe because this format is new and HaCue2 is its only reader. The
///   engine's <c>ShowDocument</c> deliberately has no naming policy, since its property names ARE its
///   wire names for an external C ABI consumer; that constraint does not apply here.</item>
///   <item><b>String enums</b> — a numeric enum silently changes meaning when someone inserts a member,
///   and this document has no external reader forcing the numeric form on it. A show file that says
///   <c>"skipOnward"</c> also survives being read by a human during a support call.</item>
///   <item><b>No <c>WhenWritingDefault</c></b> — omitting CLR defaults would drop <c>enabled: false</c>
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
[JsonSerializable(typeof(FadeCueNode))]
[JsonSerializable(typeof(JumpCueNode))]
[JsonSerializable(typeof(VisualizerCueNode))]
[JsonSerializable(typeof(PatchCueNode))]
[JsonSerializable(typeof(CommentCueNode))]
[JsonSerializable(typeof(TextCueNode))]
internal sealed partial class HaCueProjectJsonContext : JsonSerializerContext;
