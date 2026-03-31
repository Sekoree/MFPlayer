using System.Buffers;

namespace S.Media.Core.Audio;

/// <summary>
/// Pure-C# audio resampler that converts interleaved float32 PCM between arbitrary
/// sample rates and channel counts. Maintains fractional position state across calls
/// for gapless streaming.
/// <para>
/// When <c>S.Media.FFmpeg</c> is available, prefer <c>FFAudioResampler</c> (backed by libswresample)
/// for higher quality. This implementation serves as the zero-dependency fallback.
/// </para>
/// <para>
/// Use the static <see cref="Create"/> factory to construct an instance using the project's
/// error-code convention instead of exceptions.
/// </para>
/// </summary>
public sealed class AudioResampler : IAudioResampler
{
    // Sinc kernel parameters
    private const int SincKernelHalfSize = 32;
    private const int SincKernelSize = SincKernelHalfSize * 2;
    private const double KaiserBeta = 5.0;

    // F.2 — cache the denominator; BesselI0(KaiserBeta) is constant for the lifetime of the class.
    private static readonly double BesselI0Beta = BesselI0(KaiserBeta);

    private readonly int _sourceSampleRate;
    private readonly int _sourceChannelCount;
    private readonly int _targetSampleRate;
    private readonly int _targetChannelCount;
    private readonly AudioResamplerMode _mode;
    private readonly ChannelMismatchPolicy _channelPolicy;
    private readonly double _ratio; // source rate / target rate

    // Streaming state for gapless operation
    private double _fractionalPosition;

    // Ring buffer for sinc look-back across PushFrame boundaries (per channel)
    private readonly float[] _ringBuffer;
    private int _ringWritePos;
    private int _ringValidSamples;

    // F.1 — last source frame from previous call, used as s0 for linear interpolation at chunk boundaries.
    private float[]? _linearHistory; // length = sourceChannelCount (allocated lazily)

    private bool _disposed;

    // ── F.3: static factory ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a resampler using the project's error-code convention.
    /// Returns <see cref="S.Media.Core.Errors.MediaResult.Success"/> and populates
    /// <paramref name="resampler"/> on success; returns an error code and sets
    /// <paramref name="resampler"/> to <see langword="null"/> on invalid arguments.
    /// </summary>
    public static int Create(
        int sourceSampleRate,
        int sourceChannelCount,
        int targetSampleRate,
        int targetChannelCount,
        out AudioResampler? resampler,
        AudioResamplerMode mode = AudioResamplerMode.Sinc,
        ChannelMismatchPolicy channelPolicy = ChannelMismatchPolicy.Drop)
    {
        resampler = null;
        if (sourceSampleRate  <= 0) return (int)S.Media.Core.Errors.MediaErrorCode.MediaInvalidArgument;
        if (sourceChannelCount <= 0) return (int)S.Media.Core.Errors.MediaErrorCode.MediaInvalidArgument;
        if (targetSampleRate  <= 0) return (int)S.Media.Core.Errors.MediaErrorCode.MediaInvalidArgument;
        if (targetChannelCount <= 0) return (int)S.Media.Core.Errors.MediaErrorCode.MediaInvalidArgument;
        resampler = new AudioResampler(sourceSampleRate, sourceChannelCount,
            targetSampleRate, targetChannelCount, mode, channelPolicy);
        return S.Media.Core.Errors.MediaResult.Success;
    }

    /// <summary>
    /// Creates a resampler for the given source→target conversion.
    /// Prefer the <see cref="Create"/> factory for error-code-based error handling.
    /// </summary>
    internal AudioResampler(
        int sourceSampleRate,
        int sourceChannelCount,
        int targetSampleRate,
        int targetChannelCount,
        AudioResamplerMode mode = AudioResamplerMode.Sinc,
        ChannelMismatchPolicy channelPolicy = ChannelMismatchPolicy.Drop)
    {
        if (sourceSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        if (sourceChannelCount <= 0) throw new ArgumentOutOfRangeException(nameof(sourceChannelCount));
        if (targetSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        if (targetChannelCount <= 0) throw new ArgumentOutOfRangeException(nameof(targetChannelCount));

        _sourceSampleRate = sourceSampleRate;
        _sourceChannelCount = sourceChannelCount;
        _targetSampleRate = targetSampleRate;
        _targetChannelCount = targetChannelCount;
        _mode = mode;
        _channelPolicy = channelPolicy;
        _ratio = (double)sourceSampleRate / targetSampleRate;

        // Ring buffer stores per-channel history for the sinc kernel
        _ringBuffer = new float[SincKernelSize * targetChannelCount];
        _ringWritePos = 0;
        _ringValidSamples = 0;
    }

    /// <summary>Source sample rate this instance was configured for.</summary>
    public int SourceSampleRate => _sourceSampleRate;

    /// <summary>Source channel count this instance was configured for.</summary>
    public int SourceChannelCount => _sourceChannelCount;

    /// <summary>Target sample rate this instance was configured for.</summary>
    public int TargetSampleRate => _targetSampleRate;

    /// <summary>Target channel count this instance was configured for.</summary>
    public int TargetChannelCount => _targetChannelCount;

    /// <summary>
    /// Upper-bound estimate of output frames for a given input frame count.
    /// </summary>
    public int EstimateOutputFrameCount(int inputFrameCount)
    {
        if (inputFrameCount <= 0) return 0;
        if (_sourceSampleRate == _targetSampleRate) return inputFrameCount;
        // Ceiling division to account for fractional accumulation
        return (int)Math.Ceiling(inputFrameCount / _ratio) + 2;
    }

    /// <summary>
    /// Resamples a chunk of interleaved float32 samples.
    /// Returns the number of output frames written to <paramref name="destination"/>,
    /// or -1 if <see cref="ChannelMismatchPolicy.Fail"/> is active and channels don't match.
    /// </summary>
    public int Resample(ReadOnlySpan<float> source, int inputFrameCount, Span<float> destination)
    {
        if (_disposed) return 0;
        if (inputFrameCount <= 0) return 0;

        // --- Channel reshape (source channels → target channels) ---
        ReadOnlySpan<float> reshapedSource;
        float[]? reshapeRented = null;

        try
        {
            int reshapedChannelCount;
            if (_sourceChannelCount != _targetChannelCount)
            {
                var reshapeResult = ReshapeChannels(source, inputFrameCount, out reshapeRented, out reshapedChannelCount);
                if (reshapeResult < 0) return -1; // Fail policy
                reshapedSource = reshapeRented.AsSpan(0, inputFrameCount * reshapedChannelCount);
            }
            else
            {
                reshapedChannelCount = _sourceChannelCount;
                reshapedSource = source[..(inputFrameCount * reshapedChannelCount)];
            }

            // --- Rate conversion ---
            if (_sourceSampleRate == _targetSampleRate)
            {
                // No rate conversion needed — just copy
                var copyCount = Math.Min(reshapedSource.Length, destination.Length);
                reshapedSource[..copyCount].CopyTo(destination);
                return inputFrameCount;
            }

            return _mode switch
            {
                AudioResamplerMode.Linear => ResampleLinear(reshapedSource, inputFrameCount, reshapedChannelCount, destination),
                _ => ResampleSinc(reshapedSource, inputFrameCount, reshapedChannelCount, destination),
            };
        }
        finally
        {
            if (reshapeRented is not null)
            {
                ArrayPool<float>.Shared.Return(reshapeRented, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Resets fractional position and ring buffer state (e.g. after a seek).
    /// </summary>
    public void Reset()
    {
        _fractionalPosition = 0.0;
        _ringWritePos = 0;
        _ringValidSamples = 0;
        Array.Clear(_ringBuffer);
        // F.1 — also clear linear history so the next chunk starts fresh.
        if (_linearHistory is not null) Array.Clear(_linearHistory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // F.4 — zero buffers so any use-after-dispose reads zero rather than stale audio data.
        Array.Clear(_ringBuffer);
        if (_linearHistory is not null) Array.Clear(_linearHistory);
    }

    // ──────────────────────────────────────────────────────────────
    //  Channel reshape
    // ──────────────────────────────────────────────────────────────

    private int ReshapeChannels(ReadOnlySpan<float> source, int frameCount, out float[]? rented, out int outputChannelCount)
    {
        outputChannelCount = _targetChannelCount;

        if (_channelPolicy == ChannelMismatchPolicy.Fail && _sourceChannelCount > _targetChannelCount)
        {
            rented = null;
            return -1;
        }

        var totalSamples = frameCount * _targetChannelCount;
        rented = ArrayPool<float>.Shared.Rent(totalSamples);
        var dest = rented.AsSpan(0, totalSamples);

        switch (_channelPolicy)
        {
            case ChannelMismatchPolicy.MixToMono:
                MixToMono(source, frameCount, dest);
                outputChannelCount = _targetChannelCount;
                break;

            case ChannelMismatchPolicy.MixToStereo:
                MixToStereo(source, frameCount, dest);
                outputChannelCount = _targetChannelCount;
                break;

            case ChannelMismatchPolicy.Drop:
            default:
                DropOrPadChannels(source, frameCount, dest);
                outputChannelCount = _targetChannelCount;
                break;
        }

        return 0;
    }

    private void DropOrPadChannels(ReadOnlySpan<float> source, int frameCount, Span<float> dest)
    {
        var minCh = Math.Min(_sourceChannelCount, _targetChannelCount);
        for (var f = 0; f < frameCount; f++)
        {
            var srcBase = f * _sourceChannelCount;
            var dstBase = f * _targetChannelCount;
            for (var ch = 0; ch < _targetChannelCount; ch++)
            {
                dest[dstBase + ch] = ch < minCh && srcBase + ch < source.Length
                    ? source[srcBase + ch]
                    : 0f;
            }
        }
    }

    private void MixToMono(ReadOnlySpan<float> source, int frameCount, Span<float> dest)
    {
        // Mix all source channels to mono, then pad/duplicate to fill target channels
        for (var f = 0; f < frameCount; f++)
        {
            var srcBase = f * _sourceChannelCount;
            var sum = 0f;
            for (var ch = 0; ch < _sourceChannelCount; ch++)
            {
                var idx = srcBase + ch;
                if (idx < source.Length)
                    sum += source[idx];
            }

            var mono = sum / _sourceChannelCount;
            var dstBase = f * _targetChannelCount;
            for (var ch = 0; ch < _targetChannelCount; ch++)
            {
                dest[dstBase + ch] = ch == 0 ? mono : 0f;
            }
        }
    }

    private void MixToStereo(ReadOnlySpan<float> source, int frameCount, Span<float> dest)
    {
        for (var f = 0; f < frameCount; f++)
        {
            var srcBase = f * _sourceChannelCount;
            float left = 0f, right = 0f;
            int leftCount = 0, rightCount = 0;

            if (_sourceChannelCount == 1)
            {
                var idx = srcBase;
                var val = idx < source.Length ? source[idx] : 0f;
                left = val;
                right = val;
                leftCount = 1;
                rightCount = 1;
            }
            else
            {
                for (var ch = 0; ch < _sourceChannelCount; ch++)
                {
                    var idx = srcBase + ch;
                    var val = idx < source.Length ? source[idx] : 0f;
                    if ((ch & 1) == 0) { left += val; leftCount++; }
                    else { right += val; rightCount++; }
                }
            }

            var dstBase = f * _targetChannelCount;
            if (_targetChannelCount >= 1)
                dest[dstBase] = leftCount > 0 ? left / leftCount : 0f;
            if (_targetChannelCount >= 2)
                dest[dstBase + 1] = rightCount > 0 ? right / rightCount : 0f;

            for (var ch = 2; ch < _targetChannelCount; ch++)
                dest[dstBase + ch] = 0f;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Linear interpolation resampler
    // ──────────────────────────────────────────────────────────────

    private int ResampleLinear(ReadOnlySpan<float> source, int inputFrameCount, int channelCount, Span<float> destination)
    {
        // F.1 — ensure history buffer is allocated.
        if (_linearHistory is null || _linearHistory.Length < channelCount)
            _linearHistory = new float[channelCount];

        var outputFrames = 0;
        var maxOutputFrames = destination.Length / channelCount;
        var frac = _fractionalPosition;

        while (outputFrames < maxOutputFrames)
        {
            var intPos = (int)frac;
            var t = (float)(frac - intPos);

            // Check if we've consumed all input
            if (intPos >= inputFrameCount)
                break;

            var nextPos = intPos + 1;
            var dstBase = outputFrames * channelCount;

            for (var ch = 0; ch < channelCount; ch++)
            {
                // F.1 — when intPos == 0 and frac started negative (carry-over from previous chunk),
                // the "previous" sample is in _linearHistory; otherwise read from the current source.
                float s0;
                if (intPos == 0 && frac < 0.0)
                    s0 = ch < _linearHistory.Length ? _linearHistory[ch] : 0f;
                else
                {
                    var s0Idx = intPos * channelCount + ch;
                    s0 = s0Idx < source.Length ? source[s0Idx] : 0f;
                }

                var s1Idx = nextPos * channelCount + ch;
                var s1 = s1Idx < source.Length ? source[s1Idx] : s0;

                destination[dstBase + ch] = s0 + t * (s1 - s0);
            }

            outputFrames++;
            frac += _ratio;
        }

        // F.1 — save the last source frame as history for the next call.
        var lastFrame = inputFrameCount - 1;
        if (lastFrame >= 0)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var idx = lastFrame * channelCount + ch;
                _linearHistory[ch] = idx < source.Length ? source[idx] : 0f;
            }
        }

        // Store the residual fractional position relative to unconsumed input
        _fractionalPosition = frac - inputFrameCount;
        if (_fractionalPosition < 0) _fractionalPosition = 0;

        return outputFrames;
    }

    // ──────────────────────────────────────────────────────────────
    //  Windowed-sinc resampler
    // ──────────────────────────────────────────────────────────────

    private int ResampleSinc(ReadOnlySpan<float> source, int inputFrameCount, int channelCount, Span<float> destination)
    {
        var outputFrames = 0;
        var maxOutputFrames = destination.Length / channelCount;
        var frac = _fractionalPosition;

        // Anti-aliasing: when downsampling, widen the kernel and lower the cutoff
        var isDownsampling = _ratio > 1.0;
        var cutoff = isDownsampling ? 1.0 / _ratio : 1.0;
        var kernelScale = isDownsampling ? _ratio : 1.0;
        var halfSize = SincKernelHalfSize;

        // Snapshot ring buffer state for read-only look-back during this call.
        var historyWritePos = _ringWritePos;
        var historyValidSamples = _ringValidSamples;

        var inputLimit = inputFrameCount - halfSize;

        while (outputFrames < maxOutputFrames)
        {
            var intPos = (int)Math.Floor(frac);

            if (intPos >= inputLimit)
                break;

            var dstBase = outputFrames * channelCount;
            var subSampleOffset = frac - intPos;

            for (var ch = 0; ch < channelCount; ch++)
            {
                var sum = 0.0;
                var weightSum = 0.0;

                for (var k = -halfSize + 1; k <= halfSize; k++)
                {
                    var inputFrame = intPos + k;
                    float sample;

                    if (inputFrame >= 0 && inputFrame < inputFrameCount)
                    {
                        var srcIdx = inputFrame * channelCount + ch;
                        sample = srcIdx < source.Length ? source[srcIdx] : 0f;
                    }
                    else if (inputFrame < 0)
                    {
                        var age = -inputFrame;
                        if (age <= historyValidSamples)
                        {
                            var ringIdx = ((historyWritePos - age) % SincKernelSize + SincKernelSize) % SincKernelSize;
                            sample = _ringBuffer[ringIdx * channelCount + ch];
                        }
                        else
                        {
                            sample = 0f;
                        }
                    }
                    else
                    {
                        sample = 0f;
                    }

                    var x = (k - subSampleOffset) * cutoff;
                    var w = WindowedSinc(x, halfSize / kernelScale) * cutoff;
                    sum += sample * w;
                    weightSum += w;
                }

                destination[dstBase + ch] = weightSum > 0 ? (float)(sum / weightSum) : 0f;
            }

            outputFrames++;
            frac += _ratio;
        }

        // Update ring buffer with frames from the current chunk for the next call's look-back.
        var startFrame = Math.Max(0, inputFrameCount - SincKernelSize);
        for (var f = startFrame; f < inputFrameCount; f++)
        {
            var ringBase = _ringWritePos * channelCount;
            var srcBase = f * channelCount;
            for (var ch = 0; ch < channelCount; ch++)
            {
                var srcIdx = srcBase + ch;
                _ringBuffer[ringBase + ch] = srcIdx < source.Length ? source[srcIdx] : 0f;
            }

            _ringWritePos = (_ringWritePos + 1) % SincKernelSize;
            if (_ringValidSamples < SincKernelSize)
                _ringValidSamples++;
        }

        _fractionalPosition = frac - inputFrameCount;

        return outputFrames;
    }

    /// <summary>
    /// Windowed sinc function: sinc(x) * Kaiser(x, halfSize).
    /// </summary>
    private static double WindowedSinc(double x, double halfSize)
    {
        if (Math.Abs(x) < 1e-10)
            return 1.0;

        var sinc = Math.Sin(Math.PI * x) / (Math.PI * x);
        var window = KaiserWindow(x, halfSize);
        return sinc * window;
    }

    /// <summary>
    /// Kaiser window: I0(β * sqrt(1 - (x/halfSize)²)) / I0(β)
    /// </summary>
    private static double KaiserWindow(double x, double halfSize)
    {
        var r = x / halfSize;
        if (Math.Abs(r) >= 1.0)
            return 0.0;

        var arg = KaiserBeta * Math.Sqrt(1.0 - r * r);
        // F.2 — use pre-computed BesselI0Beta as denominator (no recomputation per kernel sample).
        return BesselI0(arg) / BesselI0Beta;
    }

    /// <summary>
    /// Modified Bessel function of the first kind, order 0.
    /// </summary>
    private static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var halfX = x * 0.5;

        for (var k = 1; k <= 30; k++)
        {
            term *= (halfX / k);
            var termSq = term * term;
            sum += termSq;
            if (termSq < sum * 1e-16)
                break;
        }

        return sum;
    }
}

