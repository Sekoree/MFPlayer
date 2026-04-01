using PMLib;
using PMLib.Types;
using S.Media.Core.Errors;
using S.Media.MIDI.Config;
using S.Media.MIDI.Events;
using S.Media.MIDI.Runtime;
using S.Media.MIDI.Types;

namespace S.Media.MIDI.Input;

public sealed class MIDIInput : IMIDIDevice
{
    private readonly Lock _gate = new();
    private readonly bool _nativeEnabled;
    private Thread? _pollThread;
    private bool _polling;
    private nint _stream;
    private bool _disposed;

    public MIDIInput(MIDIDeviceInfo device, MIDIReconnectOptions reconnectOptions)
    {
        Device = device;
        ReconnectOptions = reconnectOptions.Normalize();
        _nativeEnabled = device.IsNative;
    }

    public MIDIDeviceInfo Device { get; }

    public MIDIReconnectOptions ReconnectOptions { get; }

    public bool IsOpen { get; private set; }

    public event EventHandler<MIDIMessageEventArgs>? MessageReceived;

    public event EventHandler<MIDIConnectionStatusEventArgs>? StatusChanged;

    public int Open()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MIDIInputOpenFailed;
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

            var open = PMUtil.OpenInput(
                out _stream,
                Device.DeviceId,
                bufferSize: 256);

            var openCode = MIDIPortMidiErrorMapper.MapOpenInput(open);
            if (openCode != MediaResult.Success)
            {
                PublishStatus(MIDIConnectionStatus.ReconnectFailed, openCode);
                return openCode;
            }

            IsOpen = true;
            _polling = true;
            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = $"S.Media.MIDI.Input.{Device.DeviceId}",
            };
            _pollThread.Start();
            PublishStatus(MIDIConnectionStatus.Open);
            return MediaResult.Success;
        }
    }

    public int Close()
    {
        Thread? thread;
        nint stream;

        lock (_gate)
        {
            if (!IsOpen)
            {
                return MediaResult.Success;
            }

            _polling = false;
            thread = _pollThread;
            _pollThread = null;
            stream = _stream;
            _stream = nint.Zero;
            IsOpen = false;
        }

        thread?.Join(TimeSpan.FromSeconds(1));

        var closeCode = MediaResult.Success;
        if (_nativeEnabled && stream != nint.Zero)
        {
            closeCode = MIDIPortMidiErrorMapper.MapCloseInput(PMUtil.Close(stream));
        }

        PublishStatus(MIDIConnectionStatus.Closed, closeCode == MediaResult.Success ? null : closeCode);
        return closeCode;
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
        MessageReceived = null;
        StatusChanged = null;
    }

    private void PollLoop()
    {
        var buffer = new PmEvent[64];

        while (true)
        {
            nint stream;
            lock (_gate)
            {
                if (!_polling)
                {
                    return;
                }

                stream = _stream;
            }

            if (stream == nint.Zero)
            {
                return;
            }

            var count = PMUtil.Read(stream, buffer, buffer.Length);
            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var message = MIDIMessage.FromPmEvent(buffer[i]);
                    try
                    {
                        MessageReceived?.Invoke(this, new MIDIMessageEventArgs(message, Device, DateTimeOffset.UtcNow, buffer[i].Timestamp));
                    }
                    catch
                    {
                        // P2.7: Swallow handler exceptions so a misbehaving subscriber
                        // does not kill the poll thread and silently disconnect the device.
                    }
                }
            }

            if (count == (int)PmError.BufferOverflow)
            {
                HandleDisconnected((int)MediaErrorCode.MIDIDeviceDisconnected);
                return;
            }

            if (count < 0)
            {
                var mapped = MIDIPortMidiErrorMapper.MapRead((PmError)count);
                HandleDisconnected(mapped);
                return;
            }

            Thread.Sleep(1);
        }
    }

    private void HandleDisconnected(int errorCode)
    {
        nint stream;
        lock (_gate)
        {
            if (!IsOpen)
            {
                return;
            }

            _polling = false;
            _pollThread = null;
            stream = _stream;
            _stream = nint.Zero;
            IsOpen = false;
        }

        if (_nativeEnabled && stream != nint.Zero)
        {
            _ = MIDIPortMidiErrorMapper.MapCloseInput(PMUtil.Close(stream));
        }

        PublishStatus(MIDIConnectionStatus.Disconnected, errorCode);

        if (!_nativeEnabled || ReconnectOptions.ReconnectMode == MIDIReconnectMode.NoRecover)
        {
            PublishStatus(MIDIConnectionStatus.ReconnectFailed, errorCode);
            return;
        }

        // P3.12: honour the grace period before starting reconnection attempts.
        var grace = ReconnectOptions.DisconnectGracePeriod;
        if (grace > TimeSpan.Zero)
            Thread.Sleep(grace);

        PublishStatus(MIDIConnectionStatus.Reconnecting);
        if (!TryReconnectNative(out var reconnectCode))
        {
            PublishStatus(MIDIConnectionStatus.ReconnectFailed, reconnectCode);
            return;
        }

        PublishStatus(MIDIConnectionStatus.Open);
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

            var open = PMUtil.OpenInput(
                out var stream,
                Device.DeviceId,
                bufferSize: 256);

            reconnectCode = MIDIPortMidiErrorMapper.MapOpenInput(open);
            if (reconnectCode == MediaResult.Success)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _ = MIDIPortMidiErrorMapper.MapCloseInput(PMUtil.Close(stream));
                        reconnectCode = (int)MediaErrorCode.MIDIReconnectFailed;
                        return false;
                    }

                    _stream = stream;
                    IsOpen = true;
                    _polling = true;
                    _pollThread = new Thread(PollLoop)
                    {
                        IsBackground = true,
                        Name = $"S.Media.MIDI.Input.{Device.DeviceId}",
                    };
                    _pollThread.Start();
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

    private void PublishStatus(MIDIConnectionStatus status, int? errorCode = null)
    {
        StatusChanged?.Invoke(this, new MIDIConnectionStatusEventArgs(status, Device, DateTimeOffset.UtcNow, errorCode));
    }
}
