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

    public void Start() => _source.Start();

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
    /// Returns audio minus video position drift.
    /// Positive means audio is ahead; negative means video is ahead.
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
