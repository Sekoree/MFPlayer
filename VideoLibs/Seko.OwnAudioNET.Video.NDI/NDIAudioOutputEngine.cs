using NdiLib;

namespace Seko.OwnAudioNET.Video.NDI;

public sealed class NDIAudioOutputEngine : INDIAudioOutputEngine
{
    private readonly NDISenderSession _session;
    private readonly NDITimelineClock _timeline;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Lock _syncLock = new();

    private float[] _planarScratch = Array.Empty<float>();
    private bool _running;
    private bool _disposed;

    internal NDIAudioOutputEngine(NDISenderSession session, NDITimelineClock timeline, int sampleRate, int channels)
    {
        _session = session;
        _timeline = timeline;
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    public bool IsRunning
    {
        get
        {
            lock (_syncLock)
                return _running;
        }
    }

    public double PositionSeconds => _timeline.CurrentSeconds;

    public void Start()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            _timeline.ResetAudioTimeline(_sampleRate);
            _running = true;
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            _running = false;
            _timeline.ResetAudioTimeline(_sampleRate);
        }
    }

    public bool Send(ReadOnlySpan<float> interleavedSamples)
    {
        if (interleavedSamples.Length == 0)
            return true;

        if (interleavedSamples.Length % _channels != 0)
            return false;

        var frameCount = interleavedSamples.Length / _channels;
        return Send(interleavedSamples, frameCount);
    }

    public bool Send(ReadOnlySpan<float> interleavedSamples, int frameCount)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            if (!_running)
                return false;

            if (frameCount <= 0)
                return true;

            var sampleCount = frameCount * _channels;
            if (interleavedSamples.Length < sampleCount)
                return false;

            EnsureScratchCapacity(sampleCount);

            // Deinterleave into planar FLTP layout expected by NDI audio_frame_v3.
            for (var frame = 0; frame < frameCount; frame++)
            {
                var srcOffset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    _planarScratch[channel * frameCount + frame] = interleavedSamples[srcOffset + channel];
                }
            }

            var timecode = _timeline.GetAudioTimecode100nsAndAdvance(frameCount, _sampleRate);

            unsafe
            {
                fixed (float* pData = _planarScratch)
                {
                    var frame = new NdiAudioFrameV3
                    {
                        SampleRate = _sampleRate,
                        NoChannels = _channels,
                        NoSamples = frameCount,
                        Timecode = timecode,
                        FourCC = NdiFourCCAudioType.Fltp,
                        PData = (nint)pData,
                        ChannelStrideInBytes = frameCount * sizeof(float),
                        PMetadata = nint.Zero,
                        Timestamp = 0
                    };

                    _session.SendAudio(frame);
                }
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
                return;

            _running = false;
            _planarScratch = Array.Empty<float>();
            _disposed = true;
        }
    }

    private void EnsureScratchCapacity(int sampleCount)
    {
        if (_planarScratch.Length >= sampleCount)
            return;

        _planarScratch = new float[sampleCount];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NDIAudioOutputEngine));
    }
}

