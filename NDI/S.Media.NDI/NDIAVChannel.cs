using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Combined NDI A/V receive source wrapper around <see cref="NDISource"/>.
/// Exposes audio and video channels as one logical source and provides
/// simple A/V drift helpers.
/// </summary>
public sealed class NDIAVChannel : IDisposable
{
    private readonly NDISource _source;

    public IAudioChannel AudioChannel { get; }
    public IVideoChannel? VideoChannel { get; }
    public NDIClock Clock => _source.Clock;
    public NDISourceState State => _source.State;

    public event EventHandler<NDISourceStateChangedEventArgs>? StateChanged
    {
        add => _source.StateChanged += value;
        remove => _source.StateChanged -= value;
    }

    private NDIAVChannel(NDISource source)
    {
        _source = source;
        AudioChannel = source.AudioChannel
            ?? throw new InvalidOperationException("The selected NDI source has no audio stream.");
        VideoChannel = source.VideoChannel;
    }

    public static NDIAVChannel Open(NDIDiscoveredSource source, NDISourceOptions? options = null)
        => new(NDISource.Open(source, options));

    public static async Task<NDIAVChannel> OpenByNameAsync(
        string sourceName,
        NDISourceOptions? options = null,
        CancellationToken ct = default)
        => new(await NDISource.OpenByNameAsync(sourceName, options, ct).ConfigureAwait(false));

    /// <summary>
    /// Starts all capture threads (audio + video). Equivalent to calling
    /// <see cref="StartVideoCapture"/> followed by <see cref="StartAudioCapture"/>.
    /// For lower latency, prefer calling them separately — see <see cref="StartVideoCapture"/>.
    /// </summary>
    public void Start() => _source.Start();

    /// <summary>
    /// Starts only the video capture thread (and the internal clock / watchdog).
    /// Use this to detect the video format before committing to audio capture.
    /// Call <see cref="StartAudioCapture"/> once <see cref="WaitForVideoBufferAsync"/>
    /// confirms real NDI content is flowing, so the audio ring is never pre-filled
    /// with framesync silence from before the NDI source began streaming.
    /// </summary>
    public void StartVideoCapture() => _source.StartVideoCapture();

    /// <summary>
    /// Starts only the audio capture thread (and the clock / watchdog if not yet running).
    /// Call this after <see cref="StartVideoCapture"/> and after the first video frame
    /// has arrived to ensure the audio ring contains only real audio content.
    /// </summary>
    public void StartAudioCapture() => _source.StartAudioCapture();

    public void Stop() => _source.Stop();

    /// <summary>
    /// Waits until the audio capture ring reaches a minimum number of chunks.
    /// </summary>
    public Task WaitForAudioBufferAsync(int minChunks, CancellationToken ct = default)
    {
        if (AudioChannel is NDIAudioChannel ndiAudio)
            return ndiAudio.WaitForBufferAsync(minChunks, ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits until the video capture ring reaches a minimum number of frames.
    /// <para>
    /// Use <see cref="Task.WhenAll"/> to wait for both audio and video simultaneously
    /// before starting playback. This ensures both rings contain content from the same
    /// NDI timestamp origin, which prevents the fixed A/V offset that would otherwise
    /// occur if the audio ring is allowed to accumulate many more milliseconds than the
    /// video ring before playback starts.
    /// </para>
    /// </summary>
    public Task WaitForVideoBufferAsync(int minFrames, CancellationToken ct = default)
    {
        if (VideoChannel is NDIVideoChannel ndiVideo)
            return ndiVideo.WaitForBufferAsync(minFrames, ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns audio minus video position drift.
    /// Positive means audio is ahead; negative means video is ahead.
    /// <para>
    /// Both positions are measured in media time (seconds since the first consumed frame),
    /// so a non-zero value indicates that the two streams have drifted apart. When
    /// pre-buffering is done with <see cref="Task.WhenAll"/> across
    /// <see cref="WaitForAudioBufferAsync"/> and <see cref="WaitForVideoBufferAsync"/>,
    /// startup drift is near zero; any residual value represents runtime clock skew.
    /// Pass the result to <see cref="IAVMixer.SetVideoChannelTimeOffset"/> to correct it.
    /// </para>
    /// </summary>
    public bool TryGetAvDrift(out TimeSpan drift)
    {
        if (VideoChannel == null)
        {
            drift = TimeSpan.Zero;
            return false;
        }

        drift = AudioChannel.Position - VideoChannel.Position;
        return true;
    }

    public void Dispose() => _source.Dispose();
}
