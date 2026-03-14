using System.Runtime.InteropServices;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioSharp.Video.Decoders;

namespace Seko.OwnAudioSharp.Video.Sources;

/// <summary>
/// OwnAudio audio source backed by <see cref="FFAudioDecoder"/>.
/// Implements <see cref="IMasterClockSource"/> so it can drive or follow an OwnAudio
/// <see cref="MasterClock"/> for A/V synchronisation.
/// </summary>
public class FFAudioSource : BaseAudioSource, IMasterClockSource
{
    private readonly Lock _decoderLock = new();
    private readonly FFAudioDecoder _audioDecoder;
    private readonly bool _ownsDecoder;
    private readonly AudioConfig _config;
    private readonly AudioStreamInfo _streamInfo;
    private byte[] _decodeBuffer;

    private MasterClock? _masterClock;
    private double _positionSeconds;
    private bool _isEndOfStream;
    private bool _disposed;

    private const double HardSyncThresholdSeconds = 0.050;

    /// <summary>Initializes a new instance that reads from a file.</summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="config">Audio engine configuration (sample rate, channels, buffer size).</param>
    public FFAudioSource(string filePath, AudioConfig config, int? streamIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _audioDecoder = new FFAudioDecoder(filePath, config.SampleRate, config.Channels, streamIndex);
        _ownsDecoder = true;
        _streamInfo = _audioDecoder.StreamInfo;
        _decodeBuffer = new byte[Math.Max(1, config.BufferSize * config.Channels * sizeof(float))];
    }

    /// <summary>Initializes a new instance that reads from a <see cref="Stream"/>.</summary>
    /// <param name="stream">Readable input stream.</param>
    /// <param name="config">Audio engine configuration.</param>
    /// <param name="leaveOpen">When <see langword="true"/> the stream is not disposed with this instance.</param>
    public FFAudioSource(Stream stream, AudioConfig config, bool leaveOpen = false, int? streamIndex = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _audioDecoder = new FFAudioDecoder(stream, config.SampleRate, config.Channels, leaveOpen, streamIndex);
        _ownsDecoder = true;
        _streamInfo = _audioDecoder.StreamInfo;
        _decodeBuffer = new byte[Math.Max(1, config.BufferSize * config.Channels * sizeof(float))];
    }

    /// <summary>Initializes a new instance wrapping a pre-built <see cref="FFAudioDecoder"/>.</summary>
    /// <param name="audioDecoder">Decoder whose output parameters must match <paramref name="config"/>.</param>
    /// <param name="config">Audio engine configuration.</param>
    /// <param name="ownsDecoder">When <see langword="true"/> the decoder is disposed with this instance.</param>
    public FFAudioSource(FFAudioDecoder audioDecoder, AudioConfig config, bool ownsDecoder = false)
    {
        ArgumentNullException.ThrowIfNull(audioDecoder);
        ArgumentNullException.ThrowIfNull(config);

        _audioDecoder = audioDecoder;
        _ownsDecoder = ownsDecoder;
        _config = config;
        _streamInfo = audioDecoder.StreamInfo;

        if (_streamInfo.Channels != _config.Channels || _streamInfo.SampleRate != _config.SampleRate)
        {
            throw new ArgumentException(
                $"Decoder output ({_streamInfo.SampleRate}Hz/{_streamInfo.Channels}ch) must match source config ({_config.SampleRate}Hz/{_config.Channels}ch).",
                nameof(audioDecoder));
        }

        _decodeBuffer = new byte[Math.Max(1, config.BufferSize * config.Channels * sizeof(float))];
    }

    /// <inheritdoc/>
    public override AudioConfig Config => _config;
    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => _streamInfo;
    /// <summary>Current playback position in seconds.</summary>
    public override double Position => Volatile.Read(ref _positionSeconds);
    /// <summary>Total stream duration in seconds.</summary>
    public override double Duration => _streamInfo.Duration.TotalSeconds;
    /// <summary><see langword="true"/> once the end of the audio stream has been reached.</summary>
    public override bool IsEndOfStream => _isEndOfStream;

    /// <summary>
    /// Master clock offset in seconds. The source treats <c>masterTimestamp - StartOffset</c> as
    /// the stream-relative playback position when synchronising.
    /// </summary>
    public double StartOffset { get; set; }

    /// <summary><see langword="true"/> when a <see cref="MasterClock"/> is attached.</summary>
    public bool IsAttachedToClock => _masterClock != null;

    /// <inheritdoc/>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (frameCount <= 0)
            return 0;

        var requestedSamples = frameCount * _config.Channels;
        if (buffer.Length < requestedSamples)
            throw new ArgumentException("Buffer is too small for requested frame count.", nameof(buffer));

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, requestedSamples);
            OnSamplesRead(buffer, requestedSamples);
            return frameCount;
        }

        var framesWritten = 0;

        lock (_decoderLock)
        {
            while (framesWritten < frameCount)
            {
                var remainingFrames = frameCount - framesWritten;
                var requestedBytes = remainingFrames * _config.Channels * sizeof(float);
                EnsureDecodeBufferCapacity(requestedBytes);

                var result = _audioDecoder.ReadFrames(_decodeBuffer.AsSpan(0, requestedBytes));
                if (!result.IsSucceeded)
                {
                    if (result.IsEOF)
                    {
                        if (Loop && Duration > 0)
                        {
                            if (!SeekInternal(0))
                                break;
                            continue;
                        }

                        _isEndOfStream = true;
                        State = AudioState.EndOfStream;
                    }
                    else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        OnError(new OwnaudioNET.Events.AudioErrorEventArgs(result.ErrorMessage));
                    }

                    break;
                }

                if (result.FramesRead <= 0)
                    break;

                var floatsToCopy = result.FramesRead * _config.Channels;
                var src = MemoryMarshal.Cast<byte, float>(_decodeBuffer.AsSpan(0, floatsToCopy * sizeof(float)));
                src.CopyTo(buffer.Slice(framesWritten * _config.Channels, floatsToCopy));
                framesWritten += result.FramesRead;
            }

            if (framesWritten < frameCount)
            {
                var silenceSamples = (frameCount - framesWritten) * _config.Channels;
                FillWithSilence(buffer.Slice(framesWritten * _config.Channels), silenceSamples);
            }
        }

        if (framesWritten > 0)
        {
            UpdateSamplePosition(framesWritten);
            Volatile.Write(ref _positionSeconds, Position + (framesWritten / (double)_config.SampleRate));
        }

        ApplyVolume(buffer, requestedSamples);
        OnSamplesRead(buffer, requestedSamples);
        return framesWritten;
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds))
            return false;

        var clamped = Math.Clamp(positionInSeconds, 0, Duration);

        lock (_decoderLock)
        {
            if (!SeekInternal(clamped))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads audio frames aligned to <paramref name="masterTimestamp"/>. If the current position
    /// drifts beyond <c>50 ms</c> a hard seek is performed before decoding.
    /// </summary>
    /// <param name="masterTimestamp">Absolute master clock position in seconds.</param>
    /// <param name="buffer">Destination float sample buffer.</param>
    /// <param name="frameCount">Number of audio frames requested.</param>
    /// <param name="result">Details of the read operation on return.</param>
    /// <returns><see langword="true"/> on success or end-of-stream.</returns>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        var relativeTimestamp = masterTimestamp - StartOffset;
        if (relativeTimestamp < 0)
        {
            var sampleCount = frameCount * _config.Channels;
            FillWithSilence(buffer, sampleCount);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        var currentPosition = Position;
        var drift = Math.Abs(relativeTimestamp - currentPosition);
        if (drift > HardSyncThresholdSeconds)
        {
            if (!Seek(relativeTimestamp))
            {
                FillWithSilence(buffer, frameCount * _config.Channels);
                result = ReadResult.CreateFailure(0, "Seek failed during synchronization.");
                return false;
            }
        }

        var framesRead = ReadSamples(buffer, frameCount);

        if (framesRead == 0 && !IsEndOfStream)
        {
            result = ReadResult.CreateFailure(0, "Decoder returned no frames.");
            return false;
        }

        result = ReadResult.CreateSuccess(framesRead);
        return true;
    }

    /// <summary>
    /// Attaches the source to a <see cref="MasterClock"/>. The source seeks to the current clock
    /// position if the clock is already running ahead of <see cref="StartOffset"/>.
    /// </summary>
    public void AttachToClock(MasterClock clock)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(clock);

        _masterClock = clock;

        var targetTrackPosition = clock.CurrentTimestamp - StartOffset;
        if (targetTrackPosition > 0)
        {
            Seek(targetTrackPosition);
        }
        else
        {
            Seek(0);
            Volatile.Write(ref _positionSeconds, targetTrackPosition);
            SetSamplePosition(0);
        }

        IsSynchronized = true;
    }

    /// <summary>Detaches the source from its current <see cref="MasterClock"/>.</summary>
    public void DetachFromClock()
    {
        ThrowIfDisposed();

        _masterClock = null;
        IsSynchronized = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Let BaseAudioSource.Stop()->Seek(0) run while decoder is still alive.
            base.Dispose(disposing);

            lock (_decoderLock)
            {
                _masterClock = null;
                if (_ownsDecoder)
                    _audioDecoder.Dispose();
            }

            _disposed = true;
            return;
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private bool SeekInternal(double positionInSeconds)
    {
        var target = TimeSpan.FromSeconds(positionInSeconds);
        if (!_audioDecoder.TrySeek(target, out var error))
        {
            OnError(new OwnaudioNET.Events.AudioErrorEventArgs($"Seek failed: {error}"));
            return false;
        }

        _isEndOfStream = false;
        Volatile.Write(ref _positionSeconds, positionInSeconds);
        SetSamplePosition((long)(positionInSeconds * _config.SampleRate));
        if (State == AudioState.EndOfStream)
            State = AudioState.Paused;
        return true;
    }

    private void EnsureDecodeBufferCapacity(int requiredBytes)
    {
        if (_decodeBuffer.Length >= requiredBytes)
            return;

        var newSize = Math.Max(requiredBytes, Math.Max(_decodeBuffer.Length * 2, 4096));
        Array.Resize(ref _decodeBuffer, newSize);
    }
}