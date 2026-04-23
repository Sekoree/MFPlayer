namespace S.Media.SDL3;

/// <summary>
/// Legacy name for <see cref="SDL3VideoEndpoint"/>. Kept as an
/// <see cref="ObsoleteAttribute"/> forwarder per the obsoletion policy
/// (checklist §0.4.3) for one release; will be removed thereafter.
/// </summary>
[Obsolete("Renamed to SDL3VideoEndpoint. This type-forwarder will be removed in the next release.", error: false)]
public sealed class SDL3VideoOutput : SDL3VideoEndpoint
{
}

