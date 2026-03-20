using NdiLib;
using Seko.OwnAudioNET.Video.Engine;

namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Combined NDI sender engine exposing both a video sink and an audio send API.
/// </summary>
public sealed class NdiOutputEngine : INdiOutputEngine
{
    private readonly NdiRuntimeScope _runtime;
    private readonly NdiSenderSession _session;
    private readonly NdiTimelineClock _timeline;
    private bool _disposed;

    public NdiOutputEngine(NdiEngineConfig? config = null)
    {
        Config = (config ?? new NdiEngineConfig()).CloneNormalized();

        _runtime = new NdiRuntimeScope();
        _timeline = new NdiTimelineClock(Config.ExternalClockSecondsProvider);
        _session = new NdiSenderSession(Config);

        VideoOutput = new NdiVideoOutput(_session, _timeline, Config);
        AudioEngine = new NdiAudioOutputEngine(_session, _timeline, Config.AudioSampleRate, Config.AudioChannels);
    }

    public NdiEngineConfig Config { get; }

    public bool IsRunning { get; private set; }

    public NdiVideoOutput VideoOutput { get; }

    public INdiAudioOutputEngine AudioEngine { get; }

    public void Start()
    {
        ThrowIfDisposed();
        if (IsRunning)
            return;

        AudioEngine.Start();
        VideoOutput.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (!IsRunning)
            return;

        VideoOutput.Stop();
        AudioEngine.Stop();
        IsRunning = false;
    }

    public VideoEngine CreateVideoEngine(VideoEngineConfig? config = null)
    {
        ThrowIfDisposed();
        var engine = new VideoEngine(config);
        engine.AddOutput(VideoOutput);
        engine.SetCurrentOutput(VideoOutput);
        return engine;
    }

    public int GetConnectionCount(uint timeoutMs = 0)
    {
        ThrowIfDisposed();
        return _session.GetConnectionCount(timeoutMs);
    }

    public bool SendVideoRgba(ReadOnlySpan<byte> rgbaData, int width, int height, int strideBytes = 0, double? timestampSeconds = null)
    {
        ThrowIfDisposed();
        if (!IsRunning)
            return false;

        if (width <= 0 || height <= 0)
            return false;

        var requiredLength = (strideBytes > 0 ? strideBytes : width * 4) * height;
        if (rgbaData.Length < requiredLength)
            return false;

        var fourCc = Config.RgbaSendFormat == NdiVideoRgbaSendFormat.Bgra
            ? NdiFourCCVideoType.Bgra
            : NdiFourCCVideoType.Rgba;

        byte[]? tmp = null;
        ReadOnlySpan<byte> sendSpan = rgbaData.Slice(0, requiredLength);

        if (Config.RgbaSendFormat == NdiVideoRgbaSendFormat.Bgra)
        {
            tmp = new byte[requiredLength];
            for (var i = 0; i + 3 < requiredLength; i += 4)
            {
                tmp[i + 0] = sendSpan[i + 2];
                tmp[i + 1] = sendSpan[i + 1];
                tmp[i + 2] = sendSpan[i + 0];
                tmp[i + 3] = sendSpan[i + 3];
            }

            sendSpan = tmp;
        }

        unsafe
        {
            fixed (byte* pData = sendSpan)
            {
                var nativeFrame = new NdiVideoFrameV2
                {
                    Xres = width,
                    Yres = height,
                    FourCC = fourCc,
                    FrameRateN = 0,
                    FrameRateD = 0,
                    PictureAspectRatio = 0f,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = _timeline.ResolveVideoTimecode100ns(timestampSeconds ?? 0, Config.UseIncomingVideoTimestamps && timestampSeconds.HasValue),
                    PData = (nint)pData,
                    LineStrideInBytes = strideBytes > 0 ? strideBytes : width * 4,
                    PMetadata = nint.Zero,
                    Timestamp = 0
                };

                _session.SendVideo(nativeFrame);
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (IsRunning)
            Stop();

        AudioEngine.Dispose();
        VideoOutput.Dispose();
        _session.Dispose();
        _runtime.Dispose();

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NdiOutputEngine));
    }
}

