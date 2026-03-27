namespace S.Media.Core.Mixing;

/// <summary>
/// Routes a specific source channel to a specific output channel.
/// <see cref="SourceId"/> + <see cref="SourceChannel"/> identify the input signal.
/// <see cref="OutputId"/> + <see cref="OutputChannel"/> identify the destination.
/// <see cref="Gain"/> allows per-route volume control (1.0 = unity, 0.0 = silence).
/// </summary>
public readonly record struct AudioRoutingRule(
    Guid SourceId,
    int SourceChannel,
    Guid OutputId,
    int OutputChannel,
    float Gain = 1.0f);
