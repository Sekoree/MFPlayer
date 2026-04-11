using NDILib;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// Options for <see cref="NDISource.Open"/>.
/// </summary>
public sealed class NDISourceOptions
{
    /// <summary>Desired audio sample rate. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Desired audio channel count. Default 2.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>Audio ring buffer depth in chunks. Default 16.</summary>
    public int AudioBufferDepth { get; init; } = 16;

    /// <summary>Video ring buffer depth in frames. Default 4.</summary>
    public int VideoBufferDepth { get; init; } = 4;

    /// <summary>
    /// Whether to create and start the video capture channel. Default: <see langword="true"/>.
    /// Set to <see langword="false"/> for audio-only use cases to avoid the overhead (and any
    /// potential format-mismatch crashes) of decoding video frames.
    /// </summary>
    public bool EnableVideo { get; init; } = true;

    /// <summary>NDI receiver settings. <see langword="null"/> uses defaults.</summary>
    public NDIReceiverSettings? ReceiverSettings { get; init; }
}

/// <summary>
/// Manages the full NDI receive lifecycle: creates an <see cref="NDIReceiver"/>,
/// attaches an <see cref="NDIFrameSync"/>, constructs <see cref="NDIAudioChannel"/> and
/// <see cref="NDIVideoChannel"/>, and starts their capture threads.
/// Analogous to <c>FFmpegDecoder</c> for the NDI pipeline.
/// </summary>
public sealed class NDISource : IDisposable
{
    private readonly NDIReceiver  _receiver;
    private readonly NDIFrameSync _frameSync;
    private bool _disposed;

    /// <summary>The audio channel for this source. <see langword="null"/> if audio is not available.</summary>
    public NDIAudioChannel? AudioChannel { get; }

    /// <summary>The video channel for this source. <see langword="null"/> if video is not available.</summary>
    public NDIVideoChannel? VideoChannel { get; }

    /// <summary>The clock driven by NDI frame timestamps.</summary>
    public NDIClock Clock { get; }

    private NDISource(
        NDIReceiver      receiver,
        NDIFrameSync     frameSync,
        NDIClock         clock,
        NDIAudioChannel? audio,
        NDIVideoChannel? video)
    {
        _receiver    = receiver;
        _frameSync   = frameSync;
        Clock        = clock;
        AudioChannel = audio;
        VideoChannel = video;
    }

    /// <summary>
    /// Connects to an NDI source by name, creates all channels, and returns a ready-to-use
    /// <see cref="NDISource"/>. Call <see cref="Start"/> after adding channels to mixers.
    /// </summary>
    /// <param name="source">Discovered NDI source (from <see cref="NDIFinder"/>).</param>
    /// <param name="options">Options; <see langword="null"/> uses defaults.</param>
    /// <exception cref="InvalidOperationException">Thrown if the receiver or frame-sync cannot be created.</exception>
    public static NDISource Open(NDIDiscoveredSource source, NDISourceOptions? options = null)
    {
        options ??= new NDISourceOptions();

        int ret = NDIReceiver.Create(out var receiver, options.ReceiverSettings);
        if (ret != 0 || receiver == null)
            throw new InvalidOperationException($"NDIReceiver.Create failed: {ret}");

        receiver.Connect(source);

        ret = NDIFrameSync.Create(out var frameSync, receiver);
        if (ret != 0 || frameSync == null)
        {
            receiver.Dispose();
            throw new InvalidOperationException($"NDIFrameSync.Create failed: {ret}");
        }

        var clock = new NDIClock();
        var audio = new NDIAudioChannel(frameSync, clock,
            sampleRate:  options.SampleRate,
            channels:    options.Channels,
            bufferDepth: options.AudioBufferDepth);
        var video = options.EnableVideo
            ? new NDIVideoChannel(frameSync, clock, bufferDepth: options.VideoBufferDepth)
            : null;

        return new NDISource(receiver, frameSync, clock, audio, video);
    }

    /// <summary>
    /// Starts capture threads for all channels and the clock.
    /// Call after adding channels to mixers/consumers.
    /// </summary>
    public void Start()
    {
        Clock.Start();
        AudioChannel?.StartCapture();
        VideoChannel?.StartCapture();
    }

    /// <summary>Stops the clock. Capture threads continue running until <see cref="Dispose"/> is called.</summary>
    public void Stop()
    {
        Clock.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clock.Stop();
        AudioChannel?.Dispose();
        VideoChannel?.Dispose();
        _frameSync.Dispose();
        _receiver.Dispose();
        Clock.Dispose();
    }
}

