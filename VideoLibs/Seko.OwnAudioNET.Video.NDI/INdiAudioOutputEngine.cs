namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Audio send path for outbound NDI.
/// Similar in spirit to OwnAudio engine send APIs, with a span-based fast path.
/// </summary>
public interface INdiAudioOutputEngine : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    bool IsRunning { get; }

    double PositionSeconds { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Sends interleaved float32 PCM samples (LRLR... for stereo).
    /// Sample count must be divisible by <see cref="Channels"/>.
    /// </summary>
    bool Send(ReadOnlySpan<float> interleavedSamples);

    /// <summary>
    /// Sends interleaved float32 PCM for an explicit frame count.
    /// </summary>
    bool Send(ReadOnlySpan<float> interleavedSamples, int frameCount);
}

