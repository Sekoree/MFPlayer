using System.Text.Json.Serialization;

namespace HaPlay.Models;

// Serialized model. Non-CLR-default properties use `set`, not `init`: the source-generated
// serializer assigns EVERY init property through one object initializer, so a field absent from
// the JSON would load as the CLR default instead of the property initializer (see the FadeCueNode
// doc note in CueList.cs). `init` remains only where the initializer IS the CLR default.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OSCActionEndpoint), typeDiscriminator: "osc")]
[JsonDerivedType(typeof(MIDIActionEndpoint), typeDiscriminator: "midi")]
public abstract record ActionEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public virtual string KindLabel => "Endpoint";

    [JsonIgnore]
    public virtual string Summary => string.Empty;
}

public sealed record OSCActionEndpoint : ActionEndpoint
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    [JsonIgnore]
    public override string KindLabel => "OSC";

    [JsonIgnore]
    public override string Summary => $"{Host}:{Port}";
}

public sealed record MIDIActionEndpoint : ActionEndpoint
{
    public int? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public int Channel { get; init; } = 0;

    [JsonIgnore]
    public override string KindLabel => "MIDI";

    [JsonIgnore]
    public override string Summary => $"{DeviceName ?? "(auto device)"} · ch {Channel + 1}";
}
