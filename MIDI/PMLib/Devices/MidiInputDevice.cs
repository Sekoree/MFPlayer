using PMLib.MessageTypes;
using PMLib.Types;

namespace PMLib.Devices;

/// <summary>
/// A PortMidi input device that polls for incoming MIDI messages on a background thread
/// and raises <see cref="MessageReceived"/> and <see cref="SysExReceived"/> events.
/// </summary>
public class MidiInputDevice : MidiDevice
{
    private Thread?   _pollThread;
    private volatile bool _polling;

    // Partial SysEx accumulator
    private List<byte>? _sysExBuffer;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Number of input events buffered by PortMidi before overflow.
    /// Must be set before <see cref="Open"/>. Default: 256.
    /// </summary>
    public int BufferSize { get; set; } = 256;

    /// <summary>
    /// How long the poll thread sleeps between each <c>Pm_Read</c> call, in milliseconds.
    /// Lower values reduce latency at the cost of CPU. Default: 1 ms.
    /// </summary>
    public int PollIntervalMs { get; set; } = 1;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised on the polling thread whenever one or more decoded MIDI messages are received.
    /// <para>
    /// Handlers run on a background thread — marshal to the UI thread if needed.
    /// </para>
    /// </summary>
    public event EventHandler<IMidiMessage>? MessageReceived;

    /// <summary>
    /// Raised on the polling thread when a complete SysEx message (0xF0 … 0xF7) has been
    /// assembled from its PortMidi fragments.
    /// </summary>
    public event EventHandler<SysEx>? SysExReceived;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public MidiInputDevice(int deviceId) : base(deviceId) { }

    /// <summary>
    /// Opens the input stream and starts the background polling thread.
    /// </summary>
    public override PmError Open()
    {
        var err = Native.Pm_OpenInput(
            out Stream, DeviceId,
            inputSysDepInfo: nint.Zero,
            bufferSize: BufferSize,
            timeProc: nint.Zero,
            timeInfo: nint.Zero);

        if (err != PmError.NoError) return err;

        _polling    = true;
        _pollThread = new Thread(PollLoop)
        {
            Name         = $"MidiInput[{DeviceId}]",
            IsBackground = true
        };
        _pollThread.Start();
        return PmError.NoError;
    }

    /// <summary>
    /// Stops the polling thread and closes the stream.
    /// </summary>
    public override PmError Close()
    {
        _polling = false;
        _pollThread?.Join(TimeSpan.FromSeconds(2));
        _pollThread  = null;
        _sysExBuffer = null;
        return base.Close();
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private void PollLoop()
    {
        var buffer = new PmEvent[64];

        while (_polling && Stream != nint.Zero)
        {
            var count = Native.Pm_Read(Stream, buffer, buffer.Length);

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                    ProcessEvent(buffer[i]);
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    private void ProcessEvent(PmEvent ev)
    {
        byte status = PmEvent.GetStatus(ev.Message);

        if (_sysExBuffer != null)
        {
            // Real-time messages can be embedded inside SysEx — fire them independently.
            if (MidiMessageParser.IsRealTime(status))
            {
                var rt = MidiMessageParser.Decode(ev);
                if (rt != null) MessageReceived?.Invoke(this, rt);
                return;
            }
            AccumulateSysEx(ev.Message, startByte: 0);
        }
        else if (status == 0xF0)
        {
            _sysExBuffer = new List<byte>();
            AccumulateSysEx(ev.Message, startByte: 0);
        }
        else
        {
            var msg = MidiMessageParser.Decode(ev);
            if (msg != null) MessageReceived?.Invoke(this, msg);
        }
    }

    /// <summary>
    /// Appends bytes from a raw PmEvent message word into the SysEx accumulator.
    /// Fires <see cref="SysExReceived"/> when EOX (0xF7) is found.
    /// </summary>
    private void AccumulateSysEx(uint message, int startByte)
    {
        for (int b = startByte; b < 4; b++)
        {
            byte by = (byte)((message >> (b * 8)) & 0xFF);

            if (by == 0xF7)
            {
                _sysExBuffer!.Add(0xF7);
                SysExReceived?.Invoke(this, new SysEx([.. _sysExBuffer]));
                _sysExBuffer = null;
                return;
            }

            // A non-real-time status byte inside SysEx means truncated/corrupt message.
            if (b > 0 && (by & 0x80) != 0)
            {
                _sysExBuffer = null;
                return;
            }

            _sysExBuffer!.Add(by);
        }
    }
}