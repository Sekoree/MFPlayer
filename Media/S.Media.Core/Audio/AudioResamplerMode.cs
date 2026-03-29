namespace S.Media.Core.Audio;

/// <summary>
/// Selects the interpolation algorithm used by <see cref="AudioResampler"/>.
/// </summary>
public enum AudioResamplerMode
{
    /// <summary>2-tap linear interpolation. Low CPU, good for previews.</summary>
    Linear = 0,

    /// <summary>Windowed-sinc (Kaiser β=5.0, 64-tap). Broadcast-grade quality.</summary>
    Sinc = 1,
}

