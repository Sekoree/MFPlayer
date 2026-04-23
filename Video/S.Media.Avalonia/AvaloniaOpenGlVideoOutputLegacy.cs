namespace S.Media.Avalonia;

/// <summary>
/// Legacy name for <see cref="AvaloniaOpenGlVideoEndpoint"/>. Kept as an
/// <see cref="ObsoleteAttribute"/> forwarder per the obsoletion policy
/// (checklist §0.4.3) for one release; will be removed thereafter.
/// </summary>
[Obsolete("Renamed to AvaloniaOpenGlVideoEndpoint. This type-forwarder will be removed in the next release.", error: false)]
public sealed class AvaloniaOpenGlVideoOutput : AvaloniaOpenGlVideoEndpoint
{
}

