namespace S.Media.Core.Media;

/// <summary>
/// Describes the PCM format of an audio stream.
/// Source formats are independent of the hardware output format;
/// the mixer handles resampling and channel routing between the two.
/// </summary>
public readonly record struct AudioFormat(
    int        SampleRate,
    int        Channels,
    SampleType SampleType = SampleType.Float32)
{
    /// <summary>Number of samples (across all channels) per second.</summary>
    public int SamplesPerSecond => SampleRate * Channels;

    /// <summary>Returns true when this format exactly matches the hardware canonical format.</summary>
    public bool IsCanonical => SampleType == SampleType.Float32;

    public override string ToString() => $"{SampleRate} Hz / {Channels} ch / {SampleType}";
}

