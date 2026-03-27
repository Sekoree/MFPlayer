namespace S.Media.Core.Mixing;

/// <summary>
/// Routes a specific video source to a specific video output.
/// When video routing rules are defined, only sources with a matching rule
/// are pushed to the corresponding output.
/// </summary>
public readonly record struct VideoRoutingRule(
    Guid SourceId,
    Guid OutputId);
