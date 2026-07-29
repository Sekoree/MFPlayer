using System.Net;
using System.Net.Sockets;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// The workstream-B device-input ownership layer: MIDI ports and OSC listeners belong to a
/// <see cref="ControlInputSession"/> whose lifetime is "devices are configured and enabled", and the
/// mapping engine only takes a reference on it while armed. Everything here is about the ref-counted
/// open and its failure/teardown edges - the parts that decide whether a port a per-cue trigger still
/// needs stays open, and whether a failed or raced teardown leaks one.
/// </summary>
public sealed class ControlInputSessionTests
{
    [Fact]
    public async Task StartStop_RefCounts_SoASecondConsumerKeepsThePortsOpen()
    {
        var runner = new RecordingMIDISessionRunner();
        await using var session = new ControlInputSession(MidiOnlyConfig(), midiSessionFactory: _ => runner);

        Assert.False(session.IsOpen);
        Assert.Equal(0, session.OpenReferenceCount);

        await session.StartAsync(); // owner (devices configured)
        Assert.True(session.IsOpen);
        Assert.Equal(1, session.OpenReferenceCount);
        Assert.Equal(1, runner.StartCount);

        await session.StartAsync(); // mapping engine arms
        Assert.Equal(2, session.OpenReferenceCount);
        Assert.Equal(1, runner.StartCount); // devices open on the 0→1 edge only - no double-open

        await session.StopAsync(); // disarm
        Assert.Equal(1, session.OpenReferenceCount);
        Assert.True(session.IsOpen);
        Assert.Equal(0, runner.StopCount); // the B guard: disarming must NOT close the owner's ports

        await session.StopAsync(); // owner releases
        Assert.Equal(0, session.OpenReferenceCount);
        Assert.False(session.IsOpen);
        Assert.Equal(1, runner.StopCount);
    }

    [Fact]
    public async Task Release_IsTheSynchronousFormOfTheSameRefCount()
    {
        var runner = new RecordingMIDISessionRunner();
        await using var session = new ControlInputSession(MidiOnlyConfig(), midiSessionFactory: _ => runner);

        await session.StartAsync();
        await session.StartAsync();

        session.Release();
        Assert.True(session.IsOpen);
        Assert.Equal(0, runner.StopCount);

        session.Release();
        Assert.False(session.IsOpen);
        Assert.Equal(1, runner.StopCount);

        session.Release(); // unbalanced release must not underflow into a second close
        Assert.Equal(0, session.OpenReferenceCount);
        Assert.Equal(1, runner.StopCount);
    }

    [Fact]
    public async Task DisarmingTheMappingEngine_LeavesTheSharedDevicesOpenForTriggerConsumers()
    {
        // The end-to-end form of the guard above: the workspace owns the session, an armed
        // ControlSystemRuntimeSession borrows it, and disarming gives its reference back without
        // touching the ports the per-cue trigger service is still listening on.
        var config = MidiOnlyConfig();
        var runner = new RecordingMIDISessionRunner();
        await using var session = new ControlInputSession(config, midiSessionFactory: _ => runner);
        await session.StartAsync();

        var runtime = new ControlSystemRuntimeSession(
            config with { IsArmed = true },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>()),
            new NullOSCSender(),
            inputSession: session);
        await runtime.StartAsync();
        Assert.Equal(2, session.OpenReferenceCount);

        await runtime.StopAsync();
        await runtime.DisposeAsync();

        Assert.True(session.IsOpen);
        Assert.Equal(1, session.OpenReferenceCount);
        Assert.Equal(0, runner.StopCount);
    }

    [Fact]
    public async Task StartAsync_RollsBackAPartialOpen_AndTheRetrySucceeds()
    {
        // A bound listener with a later consumer failing (MIDI port already in use, device unplugged)
        // must not leave the socket up: the next attempt has to start from a clean state.
        var port = FreeUdpPort();
        var listenerId = Guid.NewGuid();
        var config = ListenerConfig(listenerId, port);
        var runner = new RecordingMIDISessionRunner { ThrowOnStart = true };
        await using var session = new ControlInputSession(config, midiSessionFactory: _ => runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.False(session.IsOpen);
        Assert.Equal(0, session.OpenReferenceCount);
        Assert.Equal(1, runner.StopCount); // the rollback stopped what did come up
        Assert.Equal(ControlSessionState.Stopped, session.OSCListeners.ListenerHealth[listenerId].State);
        Assert.True(CanBindUdpPort(port), "the partially-opened listener socket was leaked");

        runner.ThrowOnStart = false;
        await session.StartAsync();
        Assert.True(session.IsOpen);
        Assert.Equal(ControlSessionState.Running, session.OSCListeners.ListenerHealth[listenerId].State);
    }

    [Fact]
    public async Task DisposeAsync_ClosesTheDevices_AndAStartThatRacesItCannotReopenThem()
    {
        // The disposal paths take the same gate as Start/Stop/Release. Without that, a StartAsync that
        // got past the listener open while a concurrent DisposeAsync landed wrote _openRefs = 1 AFTER
        // dispose zeroed it, leaving IsOpen true on a closed session - and the workspace's
        // "retry only while !IsOpen" sync would then never retry it.
        var runner = new RecordingMIDISessionRunner();
        var session = new ControlInputSession(MidiOnlyConfig(), midiSessionFactory: _ => runner);
        await session.StartAsync();

        await session.DisposeAsync();

        Assert.False(session.IsOpen);
        Assert.Equal(0, session.OpenReferenceCount);
        Assert.Equal(1, runner.StopCount);
        Assert.True(runner.Disposed);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
        Assert.False(session.IsOpen);

        await session.DisposeAsync(); // idempotent
        session.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentWithStopAsync_TearsDownExactlyOnce()
    {
        // LoadConfig-while-armed: the armed session's StopAsync runs on a background thread while the
        // workspace disposes the shared session on the UI thread. Both used to reach
        // ControlOSCListenerManager's per-listener state unsynchronized.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = FreeUdpPort();
            var runner = new RecordingMIDISessionRunner();
            var session = new ControlInputSession(ListenerConfig(Guid.NewGuid(), port), midiSessionFactory: _ => runner);
            // ONE reference, so the racing StopAsync performs the real close rather than just a decrement.
            await session.StartAsync();

            using var go = new ManualResetEventSlim(false);
            var stop = Task.Run(async () =>
            {
                go.Wait();
                await session.StopAsync();
            });
            var dispose = Task.Run(async () =>
            {
                go.Wait();
                await session.DisposeAsync();
            });
            go.Set();
            await Task.WhenAll(stop, dispose);

            Assert.False(session.IsOpen);
            Assert.True(CanBindUdpPort(port), "a raced stop/dispose left the listener socket bound");
        }
    }

    [Fact]
    public async Task InputObserved_RepublishesInputAndDroppedRecords_AfterEveryMonitorSink()
    {
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var config = ListenerConfig(listenerId, FreeUdpPort(), OSCDevice(deviceId, listenerId));
        var baseMonitor = new ControlMonitorBuffer(maxRecords: 20);
        var attachedMonitor = new ControlMonitorBuffer(maxRecords: 20);
        await using var session = new ControlInputSession(config, baseMonitor);
        var observed = new List<ControlMonitorRecord>();
        var monitorDepthAtObservation = new List<int>();
        session.InputObserved += r =>
        {
            observed.Add(r);
            monitorDepthAtObservation.Add(attachedMonitor.Records.Count);
        };
        using var monitorLease = session.AttachMonitor(attachedMonitor);

        await session.OSCListeners.DispatchMessageAsync(listenerId, Context("127.0.0.1", 10023, "/ch/01/mix/fader"));
        await session.OSCListeners.DispatchMessageAsync(listenerId, Context("10.0.0.9", 10023, "/ch/01/mix/fader"));

        Assert.Equal(2, observed.Count);
        Assert.Equal(ControlMonitorDirection.Input, observed[0].Direction);
        // "No matching OSC device" still means the message physically arrived - a cue trigger needs it.
        Assert.Equal(ControlMonitorDirection.Dropped, observed[1].Direction);
        Assert.Equal(2, baseMonitor.Records.Count);
        // Monitor currency: the attached sink already had the record when the observer saw it.
        Assert.Equal([1, 2], monitorDepthAtObservation);
    }

    [Fact]
    public async Task AttachDispatcher_RoutesOnlyWhileTheLeaseLives()
    {
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var config = ListenerConfig(listenerId, FreeUdpPort(), OSCDevice(deviceId, listenerId));
        await using var session = new ControlInputSession(config);
        var dispatcher = new CountingDispatcher();

        // Disarmed: the router swallows the event (and the input still reached the monitor/event).
        await session.OSCListeners.DispatchMessageAsync(listenerId, Context("127.0.0.1", 10023, "/x"));
        Assert.Equal(0, dispatcher.Dispatches);

        var lease = session.AttachDispatcher(dispatcher);
        await session.OSCListeners.DispatchMessageAsync(listenerId, Context("127.0.0.1", 10023, "/x"));
        Assert.Equal(1, dispatcher.Dispatches);

        lease.Dispose();
        await session.OSCListeners.DispatchMessageAsync(listenerId, Context("127.0.0.1", 10023, "/x"));
        Assert.Equal(1, dispatcher.Dispatches);
    }

    // --- helpers -----------------------------------------------------------

    private static ControlSystemConfig MidiOnlyConfig() => new()
    {
        Devices =
        [
            new ControlDeviceInstanceConfig
            {
                Id = Guid.NewGuid(),
                Name = "X-Touch MINI",
                Protocol = ControlDeviceProtocol.MIDI,
                IsEnabled = true,
                Binding = new ControlDeviceBindingConfig { MIDIInputDeviceId = 1, MIDIInputDeviceName = "X-Touch MINI" },
            },
        ],
    };

    private static ControlSystemConfig ListenerConfig(Guid listenerId, int port, params ControlDeviceInstanceConfig[] devices) => new()
    {
        OSCListeners = [new ControlOSCListenerConfig { Id = listenerId, IsEnabled = true, LocalPort = port }],
        Devices = [.. devices],
    };

    private static ControlDeviceInstanceConfig OSCDevice(Guid id, Guid listenerId) => new()
    {
        Id = id,
        Name = "X32",
        Protocol = ControlDeviceProtocol.OSC,
        IsEnabled = true,
        Binding = new ControlDeviceBindingConfig
        {
            Alias = "x32",
            OSCHost = "127.0.0.1",
            OSCPort = 10023,
            OSCListenerId = listenerId,
        },
    };

    private static OSCMessageContext Context(string host, int port, string address) =>
        new(
            new OSCMessage(address, []),
            new IPEndPoint(IPAddress.Parse(host), port),
            BundleTimeTag: null,
            DateTimeOffset.UtcNow);

    /// <summary>An ephemeral port the OS just handed back - good enough for a bind/rebind assertion.</summary>
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static bool CanBindUdpPort(int port)
    {
        try
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed class RecordingMIDISessionRunner : IControlMIDIDeviceSessionRunner, IDisposable
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnStart { get; set; }

        public void Start(ControlMIDIDeviceManager deviceManager, CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (ThrowOnStart)
                throw new InvalidOperationException("MIDI port in use");
        }

        public void Stop() => StopCount++;

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingDispatcher : IControlScriptDispatcher
    {
        private static readonly ControlScriptRuntimeSessionResult Empty = new([], [], [], [], []);

        public int Dispatches { get; private set; }

        public Guid? ActiveLayerId => null;

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
            ControlEvent evt, CancellationToken cancellationToken = default)
        {
            Dispatches++;
            return new ValueTask<ControlScriptRuntimeSessionResult>(Empty);
        }

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
            Guid deviceInstanceId, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
            Guid deviceInstanceId, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
            Guid layerId, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
            Guid layerId, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> SetActiveLayerAsync(
            Guid? layerId, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
            Guid? scriptId = null, Guid? triggerId = null, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
            Guid deviceInstanceId, ControlSessionHealth health, CancellationToken cancellationToken = default) => new(Empty);

        public ValueTask<ControlScriptRuntimeSessionResult> TickPeriodicAsync(
            DateTimeOffset utcNow, CancellationToken cancellationToken = default) => new(Empty);
    }

    private sealed class NullOSCSender : IControlOSCSender
    {
        public ValueTask SendAsync(
            string host, int port, string address, IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
