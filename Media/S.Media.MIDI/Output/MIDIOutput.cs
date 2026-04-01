using PMLib;
using PMLib.Types;
using S.Media.Core.Errors;
using S.Media.MIDI.Config;
using S.Media.MIDI.Events;
using S.Media.MIDI.Runtime;
using S.Media.MIDI.Types;

namespace S.Media.MIDI.Output;

public sealed class MIDIOutput : IMIDIDevice
{
    private readonly Lock _gate = new();
    private readonly bool _nativeEnabled;
    private nint _stream;
    private bool _disposed;

    public MIDIOutput(MIDIDeviceInfo device, MIDIReconnectOptions? reconnectOptions = null)
    {
        Device = device;
        ReconnectOptions = (reconnectOptions ?? new MIDIReconnectOptions()).Normalize();
        _nativeEnabled = device.IsNative;
    }

    public MIDIDeviceInfo Device { get; }

    public MIDIReconnectOptions ReconnectOptions { get; }

    public bool IsOpen { get; private set; }

    public event EventHandler<MIDIConnectionStatusEventArgs>? StatusChanged;

    public int Open()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MIDIOutputOpenFailed;
            }

            if (IsOpen)
            {
                return MediaResult.Success;
            }

            PublishStatus(MIDIConnectionStatus.Opening);

            if (!_nativeEnabled)
            {
                IsOpen = true;
                PublishStatus(MIDIConnectionStatus.Open);
                return MediaResult.Success;
            }

            var openCode = TryOpenNative(out _stream);
            if (openCode != MediaResult.Success)
            {
                PublishStatus(MIDIConnectionStatus.ReconnectFailed, openCode);
                return openCode;
            }

            IsOpen = true;
            PublishStatus(MIDIConnectionStatus.Open);
            return MediaResult.Success;
        }
    }

    public int Close()
    {
        nint stream;

        lock (_gate)
        {
            if (!IsOpen)
            {
                return MediaResult.Success;
            }

            stream = _stream;
            _stream = nint.Zero;
            IsOpen = false;
        }

        var closeCode = MediaResult.Success;
        if (_nativeEnabled && stream != nint.Zero)
        {
            closeCode = MIDIPortMidiErrorMapper.MapCloseOutput(PMUtil.Close(stream));
        }

        PublishStatus(MIDIConnectionStatus.Closed, closeCode == MediaResult.Success ? null : closeCode);
        return closeCode;
    }

    public int Send(in MIDIMessage message)
    {
        if (message.Status < 0x80)
        {
            return (int)MediaErrorCode.MIDIInvalidMessage;
        }

        nint stream;
        lock (_gate)
        {
            if (!IsOpen)
            {
                return (int)MediaErrorCode.MIDIOutputNotOpen;
            }

            if (!_nativeEnabled)
            {
                return MediaResult.Success;
            }

            stream = _stream;
        }

        var send = PMUtil.WriteShort(stream, message.Timestamp, message.RawMessage);
        var sendCode = MIDIPortMidiErrorMapper.MapSend(send);
        if (sendCode != (int)MediaErrorCode.MIDIDeviceDisconnected)
        {
            return sendCode;
        }

        var reconnectCode = HandleDisconnected(sendCode);
        if (reconnectCode != MediaResult.Success)
        {
            return reconnectCode;
        }

        lock (_gate)
        {
            if (!IsOpen)
            {
                return (int)MediaErrorCode.MIDIOutputNotOpen;
            }

            stream = _stream;
        }

        var retry = PMUtil.WriteShort(stream, message.Timestamp, message.RawMessage);
        var retryCode = MIDIPortMidiErrorMapper.MapSend(retry);
        return retryCode == (int)MediaErrorCode.MIDIDeviceDisconnected
            ? HandleDisconnected(retryCode)
            : retryCode;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = Close();
        StatusChanged = null;
    }

    private void PublishStatus(MIDIConnectionStatus status, int? errorCode = null)
    {
        StatusChanged?.Invoke(this, new MIDIConnectionStatusEventArgs(status, Device, DateTimeOffset.UtcNow, errorCode));
    }

    private int HandleDisconnected(int errorCode)
    {
        nint stream;
        lock (_gate)
        {
            if (!IsOpen)
            {
                return errorCode;
            }

            stream = _stream;
            _stream = nint.Zero;
            IsOpen = false;
        }

        if (_nativeEnabled && stream != nint.Zero)
        {
            _ = MIDIPortMidiErrorMapper.MapCloseOutput(PMUtil.Close(stream));
        }

        PublishStatus(MIDIConnectionStatus.Disconnected, errorCode);

        if (!_nativeEnabled || ReconnectOptions.ReconnectMode == MIDIReconnectMode.NoRecover)
        {
            PublishStatus(MIDIConnectionStatus.ReconnectFailed, errorCode);
            return errorCode;
        }

        // P3.12: honour the grace period before starting reconnection attempts.
        var grace = ReconnectOptions.DisconnectGracePeriod;
        if (grace > TimeSpan.Zero)
            Thread.Sleep(grace);

        PublishStatus(MIDIConnectionStatus.Reconnecting);
        if (!TryReconnectNative(out var reconnectCode))
        {
            PublishStatus(MIDIConnectionStatus.ReconnectFailed, reconnectCode);
            return reconnectCode;
        }

        PublishStatus(MIDIConnectionStatus.Open);
        return MediaResult.Success;
    }

    private int TryOpenNative(out nint stream)
    {
        var open = PMUtil.OpenOutput(
            out stream,
            Device.DeviceId,
            bufferSize: 256,
            latency: 0);

        return MIDIPortMidiErrorMapper.MapOpenOutput(open);
    }

    private bool TryReconnectNative(out int reconnectCode)
    {
        var timeout = ReconnectOptions.ReconnectTimeout;
        var hasTimeout = timeout > TimeSpan.Zero;
        var deadlineUtc = hasTimeout ? DateTime.UtcNow + timeout : DateTime.MaxValue;

        for (var attempt = 1; attempt <= ReconnectOptions.MaxReconnectAttempts; attempt++)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    reconnectCode = (int)MediaErrorCode.MIDIReconnectFailed;
                    return false;
                }
            }

            reconnectCode = TryOpenNative(out var stream);
            if (reconnectCode == MediaResult.Success)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _ = MIDIPortMidiErrorMapper.MapCloseOutput(PMUtil.Close(stream));
                        reconnectCode = (int)MediaErrorCode.MIDIReconnectFailed;
                        return false;
                    }

                    _stream = stream;
                    IsOpen = true;
                    reconnectCode = MediaResult.Success;
                    return true;
                }
            }

            if (attempt == ReconnectOptions.MaxReconnectAttempts || DateTime.UtcNow >= deadlineUtc)
            {
                reconnectCode = (int)MediaErrorCode.MIDIReconnectFailed;
                return false;
            }

            Thread.Sleep(ReconnectOptions.ReconnectAttemptDelay);
        }

        reconnectCode = (int)MediaErrorCode.MIDIReconnectFailed;
        return false;
    }
}
