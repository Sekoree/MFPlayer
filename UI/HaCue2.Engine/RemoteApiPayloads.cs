using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaCue2.Engine;

/// <summary>
/// The remote API's wire shapes.
/// </summary>
/// <remarks>
/// <para>
/// Real records with a source-generated serializer, not anonymous types. Two reasons, and the second
/// is what forced it: an API payload IS a contract with somebody else's show-control system and
/// deserves a named type that a reader can find, and the repo is NativeAOT-clean by policy - the
/// reflection-based serializer fails the build, which is the rule doing its job.
/// </para>
/// <para>
/// Ids are strings throughout. A GUID is not JSON-native, and every consumer of this API is going to
/// paste one into a macro editor.
/// </para>
/// </remarks>
public sealed record RemoteError(string Error);

public sealed record RemoteStatus(
    IReadOnlyList<string> Sounding,
    bool Paused,
    string? Previewing,
    IReadOnlyList<string> Problems,
    IReadOnlyDictionary<string, string> Standby);

public sealed record RemoteRoute(
    string Method, string Path, string Does, string Domain, long Calls);

public sealed record RemoteList(string Id, string Name, int Cues, string? Standby);

/// <summary>The answer to a transport call: what it did, in the caller's terms.</summary>
public sealed record RemoteAck(
    string? Fired = null,
    string? Stopped = null,
    string? Standby = null,
    string? List = null,
    bool? Paused = null,
    bool? Ok = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteError))]
[JsonSerializable(typeof(RemoteStatus))]
[JsonSerializable(typeof(RemoteRoute[]))]
[JsonSerializable(typeof(RemoteList[]))]
[JsonSerializable(typeof(RemoteAck))]
internal sealed partial class RemoteApiJsonContext : JsonSerializerContext;
