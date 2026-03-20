using System.Runtime.InteropServices;

namespace NdiLib;

public sealed class NdiFinder : IDisposable
{
    private nint _instance;

    public NdiFinder(bool showLocalSources = true)
    {
        var settings = NdiFindCreate.CreateDefault();
        settings.ShowLocalSources = showLocalSources ? (byte)1 : (byte)0;

        _instance = Native.NDIlib_find_create_v2(settings);
        if (_instance == nint.Zero)
            throw new InvalidOperationException("Failed to create NDI finder instance.");
    }

    public bool WaitForSources(uint timeoutMs) => Native.NDIlib_find_wait_for_sources(_instance, timeoutMs);

    public NdiDiscoveredSource[] GetCurrentSources()
    {
        var ptr = Native.NDIlib_find_get_current_sources(_instance, out var count);
        if (ptr == nint.Zero || count == 0)
            return [];

        var sourceSize = Marshal.SizeOf<NdiSource>();
        var result = new NdiDiscoveredSource[count];

        for (var i = 0; i < count; i++)
        {
            var sourcePtr = nint.Add(ptr, i * sourceSize);
            var source = Marshal.PtrToStructure<NdiSource>(sourcePtr);
            result[i] = new NdiDiscoveredSource(source.NdiName ?? string.Empty, source.UrlAddress);
        }

        return result;
    }

    public void Dispose()
    {
        if (_instance == nint.Zero)
            return;

        Native.NDIlib_find_destroy(_instance);
        _instance = nint.Zero;
    }
}

public sealed class NdiReceiverSettings
{
    public NdiRecvColorFormat ColorFormat { get; init; } = NdiRecvColorFormat.UyvyBgra;
    public NdiRecvBandwidth Bandwidth { get; init; } = NdiRecvBandwidth.Highest;
    public bool AllowVideoFields { get; init; } = true;
    public string? ReceiverName { get; init; }
}

public sealed class NdiReceiver : IDisposable
{
    private nint _instance;

    public NdiReceiver(NdiReceiverSettings? settings = null)
    {
        settings ??= new NdiReceiverSettings();

        using var recvName = Utf8Buffer.From(settings.ReceiverName);

        var create = new NdiRecvCreateV3
        {
            SourceToConnectTo = default,
            ColorFormat = settings.ColorFormat,
            Bandwidth = settings.Bandwidth,
            AllowVideoFields = settings.AllowVideoFields ? (byte)1 : (byte)0,
            PNdiRecvName = recvName.Pointer
        };

        _instance = Native.NDIlib_recv_create_v3(create);
        if (_instance == nint.Zero)
            throw new InvalidOperationException("Failed to create NDI receiver instance.");
    }

    internal nint Instance => _instance;

    public void Connect(in NdiDiscoveredSource source)
    {
        using var ndiName = Utf8Buffer.From(source.Name);
        using var url = Utf8Buffer.From(source.UrlAddress);

        var nativeSource = new NdiSource
        {
            PNdiName = ndiName.Pointer,
            PUrlAddress = url.Pointer
        };

        Native.NDIlib_recv_connect(_instance, nativeSource);
    }

    public NdiFrameType Capture(
        out NdiVideoFrameV2 video,
        out NdiAudioFrameV3 audio,
        out NdiMetadataFrame metadata,
        uint timeoutMs)
    {
        return Native.NDIlib_recv_capture_v3(_instance, out video, out audio, out metadata, timeoutMs);
    }

    public NdiCaptureScope CaptureScoped(uint timeoutMs)
    {
        var frameType = Capture(out var video, out var audio, out var metadata, timeoutMs);
        return new NdiCaptureScope(this, frameType, video, audio, metadata);
    }

    public void FreeVideo(in NdiVideoFrameV2 frame) => Native.NDIlib_recv_free_video_v2(_instance, frame);

    public void FreeAudio(in NdiAudioFrameV3 frame) => Native.NDIlib_recv_free_audio_v3(_instance, frame);

    public void FreeMetadata(in NdiMetadataFrame frame) => Native.NDIlib_recv_free_metadata(_instance, frame);

    public int GetConnectionCount() => Native.NDIlib_recv_get_no_connections(_instance);

    public void Dispose()
    {
        if (_instance == nint.Zero)
            return;

        Native.NDIlib_recv_destroy(_instance);
        _instance = nint.Zero;
    }

    public sealed class NdiCaptureScope : IDisposable
    {
        private readonly NdiReceiver _receiver;
        private bool _disposed;

        internal NdiCaptureScope(
            NdiReceiver receiver,
            NdiFrameType frameType,
            NdiVideoFrameV2 video,
            NdiAudioFrameV3 audio,
            NdiMetadataFrame metadata)
        {
            _receiver = receiver;
            FrameType = frameType;
            Video = video;
            Audio = audio;
            Metadata = metadata;
        }

        public NdiFrameType FrameType { get; }

        public NdiVideoFrameV2 Video { get; }

        public NdiAudioFrameV3 Audio { get; }

        public NdiMetadataFrame Metadata { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            switch (FrameType)
            {
                case NdiFrameType.Video:
                    _receiver.FreeVideo(Video);
                    break;
                case NdiFrameType.Audio:
                    _receiver.FreeAudio(Audio);
                    break;
                case NdiFrameType.Metadata:
                    _receiver.FreeMetadata(Metadata);
                    break;
            }

            _disposed = true;
        }
    }
}

public sealed class NdiSender : IDisposable
{
    private nint _instance;

    public NdiSender(string? senderName = null, string? groups = null, bool clockVideo = true, bool clockAudio = true)
    {
        using var ndiName = Utf8Buffer.From(senderName);
        using var groupList = Utf8Buffer.From(groups);

        var create = new NdiSendCreate
        {
            PNdiName = ndiName.Pointer,
            PGroups = groupList.Pointer,
            ClockVideo = clockVideo ? (byte)1 : (byte)0,
            ClockAudio = clockAudio ? (byte)1 : (byte)0
        };

        _instance = Native.NDIlib_send_create(create);
        if (_instance == nint.Zero)
            throw new InvalidOperationException("Failed to create NDI sender instance.");
    }

    public void SendVideo(in NdiVideoFrameV2 frame) => Native.NDIlib_send_send_video_v2(_instance, frame);

    public void SendAudio(in NdiAudioFrameV3 frame) => Native.NDIlib_send_send_audio_v3(_instance, frame);

    public void SendMetadata(in NdiMetadataFrame frame) => Native.NDIlib_send_send_metadata(_instance, frame);

    public int GetConnectionCount(uint timeoutMs = 0) => Native.NDIlib_send_get_no_connections(_instance, timeoutMs);

    public void Dispose()
    {
        if (_instance == nint.Zero)
            return;

        Native.NDIlib_send_destroy(_instance);
        _instance = nint.Zero;
    }
}

public sealed class NdiFrameSync : IDisposable
{
    private nint _instance;

    public NdiFrameSync(NdiReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        _instance = Native.NDIlib_framesync_create(receiver.Instance);
        if (_instance == nint.Zero)
            throw new InvalidOperationException("Failed to create NDI frame sync instance.");
    }

    public void CaptureVideo(out NdiVideoFrameV2 frame, NdiFrameFormatType fieldType = NdiFrameFormatType.Progressive)
        => Native.NDIlib_framesync_capture_video(_instance, out frame, fieldType);

    public void FreeVideo(in NdiVideoFrameV2 frame)
        => Native.NDIlib_framesync_free_video(_instance, frame);

    public void CaptureAudio(out NdiAudioFrameV3 frame, int sampleRate, int channels, int samples)
        => Native.NDIlib_framesync_capture_audio_v2(_instance, out frame, sampleRate, channels, samples);

    public void FreeAudio(in NdiAudioFrameV3 frame)
        => Native.NDIlib_framesync_free_audio_v2(_instance, frame);

    public void Dispose()
    {
        if (_instance == nint.Zero)
            return;

        Native.NDIlib_framesync_destroy(_instance);
        _instance = nint.Zero;
    }
}

internal sealed class Utf8Buffer : IDisposable
{
    private nint _pointer;

    private Utf8Buffer(nint pointer)
    {
        _pointer = pointer;
    }

    public nint Pointer => _pointer;

    public static Utf8Buffer From(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return new Utf8Buffer(nint.Zero);

        return new Utf8Buffer(Marshal.StringToCoTaskMemUTF8(value));
    }

    public void Dispose()
    {
        if (_pointer == nint.Zero)
            return;

        Marshal.FreeCoTaskMem(_pointer);
        _pointer = nint.Zero;
    }
}

