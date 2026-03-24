namespace S.Media.Core.Audio;

public readonly record struct AudioFrame(
    ReadOnlyMemory<float> Samples,
    int FrameCount,
    int SourceChannelCount,
    AudioFrameLayout Layout,
    int SampleRate,
    TimeSpan PresentationTime);

