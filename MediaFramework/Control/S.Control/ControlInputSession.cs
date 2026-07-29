namespace S.Control;

/// <summary>
/// Device-input ownership layer. Opens the configured MIDI inputs and OSC listeners and republishes
/// everything that physically arrives, independent of whether a mapping engine
/// (<see cref="ControlSystemRuntimeSession"/>) is armed - its lifetime is "devices are configured and
/// enabled", not "the mapping engine is armed". The mapping engine attaches its dispatcher on arm
/// (<see cref="AttachDispatcher"/>) and detaches on disarm, so disarming never closes ports other
/// consumers (per-cue triggers, MIDI learn) still need.
/// </summary>
/// <remarks>
/// <para><strong>Ref-counted open.</strong> <see cref="StartAsync"/>/<see cref="StopAsync"/> are
/// ref-counted: the configuration owner holds one reference, the mapping engine takes another while
/// armed. Devices open on the 0→1 edge and close on the 1→0 edge, so arming cannot double-open a port
/// and disarming cannot close one the owner still holds.</para>
/// <para><strong>Monitor currency.</strong> Device I/O records go to the base sink first, then to every
/// attached sink (the armed session's monitor buffer), and only then to <see cref="InputObserved"/> -
/// the monitor must never miss what an observer already saw.</para>
/// </remarks>
public sealed class ControlInputSession : IAsyncDisposable, IDisposable
{
    private readonly ControlSystemConfig _config;
    private readonly InputFanoutSink _sink;
    private readonly InputDispatcherRouter _router;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _openRefs;
    private bool _disposed;

    /// <param name="midiSessionFactory">Builds the MIDI device-session runner over this session's monitor
    /// sink. Null means no MIDI port I/O (OSC-only hosts, and tests that must not touch PortMIDI).</param>
    public ControlInputSession(
        ControlSystemConfig config,
        IControlMonitorSink? monitor = null,
        Func<IControlMonitorSink, IControlMIDIDeviceSessionRunner>? midiSessionFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sink = new InputFanoutSink(this, monitor ?? NullControlMonitorSink.Instance);
        _router = new InputDispatcherRouter();
        OSCListeners = new ControlOSCListenerManager(config, _router, _sink);
        MIDIDevices = new ControlMIDIDeviceManager(config, _router, _sink);
        MIDISessions = midiSessionFactory?.Invoke(_sink);
    }

    /// <summary>Every MIDI/OSC record that physically arrived on a configured port or listener
    /// (<see cref="ControlMonitorDirection.Input"/> plus <see cref="ControlMonitorDirection.Dropped"/> -
    /// "no matching device" still means the message reached us). Raised on the I/O threads (PortMIDI
    /// poll / UDP receive); subscribers must marshal themselves and must not block.</summary>
    public event Action<ControlMonitorRecord>? InputObserved;

    public ControlOSCListenerManager OSCListeners { get; }

    public ControlMIDIDeviceManager MIDIDevices { get; }

    public IControlMIDIDeviceSessionRunner? MIDISessions { get; }

    /// <summary>MIDI output sender backed by the same device sessions, when the runner provides one.</summary>
    public IControlMIDISender? MIDISender => MIDISessions as IControlMIDISender;

    /// <summary>True while the ports/listeners are open (at least one consumer holds a start reference).</summary>
    public bool IsOpen => Volatile.Read(ref _openRefs) > 0;

    /// <summary>Consumers currently holding the devices open (owner + armed mapping engine).</summary>
    public int OpenReferenceCount => Volatile.Read(ref _openRefs);

    /// <summary>True when the config has anything this session would own: an enabled MIDI device with an
    /// input or output binding, or an enabled OSC listener.</summary>
    public static bool HasConfiguredDevices(ControlSystemConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.OSCListeners.Any(l => l.IsEnabled)
               || config.Devices.Any(d =>
                   d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled && HasMIDIBinding(d.Binding));
    }

    /// <summary>Opens the devices on the first reference; later calls only add a reference.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_openRefs > 0)
            {
                Volatile.Write(ref _openRefs, _openRefs + 1);
                return;
            }

            try
            {
                await OSCListeners.StartAsync(cancellationToken).ConfigureAwait(false);
                MIDISessions?.Start(MIDIDevices, cancellationToken);
            }
            catch
            {
                // Partial open (e.g. a bound port with another listener still failing): close what came
                // up so the next attempt starts from a clean state instead of leaking a socket.
                MIDISessions?.Stop();
                await OSCListeners.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _openRefs, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases one reference; closes the devices only when the last one goes.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_openRefs == 0)
                return;

            Volatile.Write(ref _openRefs, _openRefs - 1);
            if (_openRefs > 0)
                return;

            MIDISessions?.Stop();
            await OSCListeners.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous reference release for sync disposal paths. Returns immediately while other
    /// consumers still hold the devices open; only the last reference pays for the close.</summary>
    public void Release()
    {
        if (_disposed)
            return;

        _gate.Wait();
        try
        {
            if (_openRefs == 0)
                return;

            Volatile.Write(ref _openRefs, _openRefs - 1);
            if (_openRefs > 0)
                return;

            MIDISessions?.Stop();
            OSCListeners.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Routes device input into a mapping engine for as long as the returned lease lives. One
    /// engine at a time (arming is exclusive); disposing the lease leaves the devices open.</summary>
    public IDisposable AttachDispatcher(IControlScriptDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _router.Attach(dispatcher);
    }

    /// <summary>Mirrors device I/O records into an additional sink (the armed session's monitor buffer)
    /// for as long as the returned lease lives.</summary>
    public IDisposable AttachMonitor(IControlMonitorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sink.Attach(sink);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // _gate is deliberately left undisposed: a consumer whose teardown races this disposal (project
        // switch while an armed session is still stopping) must not fault on a disposed semaphore.
        _disposed = true;
        Volatile.Write(ref _openRefs, 0);
        MIDISessions?.Stop();
        await OSCListeners.DisposeAsync().ConfigureAwait(false);
        DisposeMIDISessions();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Volatile.Write(ref _openRefs, 0);
        MIDISessions?.Stop();
        OSCListeners.Dispose();
        DisposeMIDISessions();
    }

    private void DisposeMIDISessions()
    {
        if (MIDISessions is IDisposable disposable)
            disposable.Dispose();
    }

    private void RaiseInput(ControlMonitorRecord record)
    {
        if (record.Direction is not (ControlMonitorDirection.Input or ControlMonitorDirection.Dropped))
            return;

        try
        {
            InputObserved?.Invoke(record);
        }
        catch
        {
            // Observation is best-effort; a subscriber fault must never poison the I/O threads.
        }
    }

    private static bool HasMIDIBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIInputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName)
        || binding.MIDIOutputDeviceId.HasValue
        || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName);

    /// <summary>Dispatcher seam the device managers hold for the session's whole life. Without an attached
    /// mapping engine (disarmed) it swallows the event and returns an empty result - the input still
    /// reached the monitor sink and <see cref="InputObserved"/>.</summary>
    private sealed class InputDispatcherRouter : IControlScriptDispatcher
    {
        private static readonly ControlScriptRuntimeSessionResult EmptyResult = new([], [], [], [], []);

        private IControlScriptDispatcher? _attached;

        public Guid? ActiveLayerId => Volatile.Read(ref _attached)?.ActiveLayerId;

        public IDisposable Attach(IControlScriptDispatcher dispatcher)
        {
            Volatile.Write(ref _attached, dispatcher);
            return new Lease(this, dispatcher);
        }

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
            ControlEvent evt,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchControlEventAsync(evt, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
            Guid deviceInstanceId,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchDeviceEnabledAsync(deviceInstanceId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
            Guid deviceInstanceId,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchDeviceDisabledAsync(deviceInstanceId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
            Guid layerId,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchLayerEnabledAsync(layerId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
            Guid layerId,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchLayerDisabledAsync(layerId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> SetActiveLayerAsync(
            Guid? layerId,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.SetActiveLayerAsync(layerId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
            Guid? scriptId = null,
            Guid? triggerId = null,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.DispatchManualAsync(scriptId, triggerId, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
            Guid deviceInstanceId,
            ControlSessionHealth health,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.ReportDeviceHealthAsync(deviceInstanceId, health, cancellationToken)
                : Empty();

        public ValueTask<ControlScriptRuntimeSessionResult> TickPeriodicAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Volatile.Read(ref _attached) is { } d
                ? d.TickPeriodicAsync(utcNow, cancellationToken)
                : Empty();

        private static ValueTask<ControlScriptRuntimeSessionResult> Empty() => new(EmptyResult);

        private sealed class Lease(InputDispatcherRouter owner, IControlScriptDispatcher dispatcher) : IDisposable
        {
            public void Dispose() =>
                Interlocked.CompareExchange(ref owner._attached, null, dispatcher);
        }
    }

    /// <summary>Monitor sink the device managers record into: base sink, then attached sinks, then the
    /// public input event.</summary>
    private sealed class InputFanoutSink(ControlInputSession owner, IControlMonitorSink baseSink) : IControlMonitorSink
    {
        private readonly Lock _gate = new();
        private IControlMonitorSink[] _attached = [];

        public IDisposable Attach(IControlMonitorSink sink)
        {
            lock (_gate)
            {
                _attached = [.. _attached, sink];
            }

            return new Lease(this, sink);
        }

        public void Record(ControlMonitorRecord record)
        {
            baseSink.Record(record);
            foreach (var sink in Volatile.Read(ref _attached))
            {
                try
                {
                    sink.Record(record);
                }
                catch
                {
                    // A faulted observer sink must not stop the remaining sinks or the input event.
                }
            }

            owner.RaiseInput(record);
        }

        private void Detach(IControlMonitorSink sink)
        {
            lock (_gate)
            {
                _attached = [.. _attached.Where(s => !ReferenceEquals(s, sink))];
            }
        }

        private sealed class Lease(InputFanoutSink owner, IControlMonitorSink sink) : IDisposable
        {
            public void Dispose() => owner.Detach(sink);
        }
    }
}
