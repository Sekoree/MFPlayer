using Seko.OwnAudioNET.Video.Engine;

namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Combined outbound NDI engine exposing both video and audio send paths.
/// </summary>
public interface INdiOutputEngine : IDisposable
{
    NdiEngineConfig Config { get; }

    bool IsRunning { get; }

    NdiVideoOutput VideoOutput { get; }

    INdiAudioOutputEngine AudioEngine { get; }

    void Start();

    void Stop();

    /// <summary>
    /// Creates a regular <see cref="VideoEngine"/> pre-bound to this NDI output sink.
    /// Useful for decoder -&gt; engine -&gt; sink flow without a mixer.
    /// </summary>
    VideoEngine CreateVideoEngine(VideoEngineConfig? config = null);

    int GetConnectionCount(uint timeoutMs = 0);

    bool SendVideoRgba(ReadOnlySpan<byte> rgbaData, int width, int height, int strideBytes = 0, double? timestampSeconds = null);
}

