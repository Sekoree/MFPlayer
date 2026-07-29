using Avalonia.Headless;
using HaPlay.ViewModels;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// The always-on device-input session the Control workspace owns (workstream B): its ports follow the
/// CONFIG, not the mapping engine's arm state. These cover the two things that decide whether an
/// operator's edit actually reaches the live devices - the identity that triggers a rebuild, and the
/// teardown ordering that must never let a queued sync re-open ports behind a shutdown.
/// <para>Everything runs through <see cref="ControlWorkspaceViewModel.DeviceInputSessionFactory"/>, the
/// headless seam, with a fake MIDI runner: no PortMIDI, no sockets.</para>
/// </summary>
public sealed class ControlWorkspaceDeviceInputTests
{
    private static Task DispatchUiAsync(Func<Task> body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ControlWorkspaceDeviceInputTests).Assembly)
            .DispatchAsync(body, CancellationToken.None);

    [Fact]
    public Task EditingAnOSCDeviceHostOrPort_RebuildsTheInputSession() => DispatchUiAsync(async () =>
    {
        // The regression: the signature covered MIDI devices and enabled listeners only, so re-arming
        // after an OSC host/port edit reused the session's PRE-EDIT config snapshot - the listener manager
        // matches incoming messages on exactly those fields, so scripts fired for the wrong device (or not
        // at all) until HaPlay restarted, even though the UI says "Re-arm to apply".
        var harness = new Harness();
        await using var vm = harness.CreateWorkspace();
        var deviceId = Guid.NewGuid();

        vm.LoadConfig(ConfigWith(OSCDevice(deviceId, "192.168.2.76", 10023)));
        Assert.Single(harness.Created);

        vm.LoadConfig(ConfigWith(OSCDevice(deviceId, "192.168.2.99", 10023)));
        Assert.Equal(2, harness.Created.Count);

        vm.LoadConfig(ConfigWith(OSCDevice(deviceId, "192.168.2.99", 10024)));
        Assert.Equal(3, harness.Created.Count);

        var listenerId = Guid.NewGuid();
        vm.LoadConfig(ConfigWith(OSCDevice(deviceId, "192.168.2.99", 10024, listenerId)));
        Assert.Equal(4, harness.Created.Count);

        // The live session must be holding the LAST edit, not the first snapshot.
        var live = Assert.Single(harness.Created[3].Devices, d => d.Protocol == ControlDeviceProtocol.OSC);
        Assert.Equal("192.168.2.99", live.Binding.OSCHost);
        Assert.Equal(10024, live.Binding.OSCPort);
        Assert.Equal(listenerId, live.Binding.OSCListenerId);
    });

    [Fact]
    public Task TogglingIncludeRawBytes_RebuildsTheInputSession() => DispatchUiAsync(async () =>
    {
        // Both device managers stamp raw wire bytes into monitor records from the config they captured.
        var harness = new Harness();
        await using var vm = harness.CreateWorkspace();
        var config = ConfigWith(OSCDevice(Guid.NewGuid(), "192.168.2.76", 10023));

        vm.LoadConfig(config with { Monitor = new ControlMonitorOptions { IncludeRawBytes = true } });
        Assert.Single(harness.Created);

        vm.LoadConfig(config with { Monitor = new ControlMonitorOptions { IncludeRawBytes = false } });
        Assert.Equal(2, harness.Created.Count);
        Assert.False(harness.Created[1].Monitor.IncludeRawBytes);
    });

    [Fact]
    public Task AnEditTheDevicesDoNotSeeKeepsTheLivePortsOpen() => DispatchUiAsync(async () =>
    {
        // The other half of the contract: the signature exists so unrelated edits do NOT cycle live
        // hardware. Scripts and layers change nothing the input session behaves on.
        var harness = new Harness();
        await using var vm = harness.CreateWorkspace();
        var config = ConfigWith(OSCDevice(Guid.NewGuid(), "192.168.2.76", 10023));

        vm.LoadConfig(config);
        var session = Assert.Single(harness.Sessions);
        Assert.True(session.IsOpen);

        vm.LoadConfig(config with
        {
            Layers = [new ControlLayerConfig { Id = Guid.NewGuid(), Name = "Layer B" }],
        });

        Assert.Single(harness.Sessions);
        Assert.True(session.IsOpen, "an unrelated config edit closed the live device ports");
    });

    [Fact]
    public Task ASyncQueuedBehindTheWorkspaceTeardown_DoesNotReopenTheDevices() => DispatchUiAsync(async () =>
    {
        // The leak: _inputShutdown was only checked in the synchronous wrapper, never inside the sync body
        // after it acquired the sync gate. A sync already queued when DisposeAsync ran therefore resumed
        // AFTER the teardown and created + started a brand-new session nothing would ever dispose - MIDI
        // ports and UDP sockets held for the rest of the process, and on a project switch the previous
        // project's devices coming back up.
        //
        // The queue is built deterministically: the first session's teardown parks (its own dispose gate is
        // held by a stop blocked in the fake MIDI runner), so the in-flight sync holds the sync gate while
        // DisposeAsync and then a second sync line up behind it.
        var harness = new Harness();
        var vm = harness.CreateWorkspace();
        vm.LoadConfig(ConfigWith(OSCDevice(Guid.NewGuid(), "192.168.2.76", 10023)));
        var first = Assert.Single(harness.Sessions);

        harness.BlockStop = true;
        var blockedStop = Task.Run(() => first.StopAsync());
        Assert.True(harness.WaitUntilStopBlocked(TimeSpan.FromSeconds(5)), "the fake runner never blocked");

        // In-flight sync: takes the sync gate, then parks tearing `first` down.
        vm.LoadConfig(ConfigWith(OSCDevice(Guid.NewGuid(), "10.0.0.5", 10023)));
        // Queued behind it: the workspace teardown...
        var teardown = vm.DisposeAsync();
        // ...and then one more config-driven sync.
        vm.LoadConfig(ConfigWith(OSCDevice(Guid.NewGuid(), "10.0.0.6", 10023)));

        harness.ReleaseStop();
        await blockedStop;
        await teardown;
        Assert.True(await harness.WaitUntilQuiet(TimeSpan.FromSeconds(5)), "a queued sync never finished");

        Assert.Single(harness.Created);
        Assert.All(harness.Sessions, s => Assert.False(s.IsOpen, "a session was left open after the teardown"));
    });

    // --- helpers -----------------------------------------------------------

    private static ControlSystemConfig ConfigWith(ControlDeviceInstanceConfig oscDevice) => new()
    {
        // An enabled MIDI device is what makes this config "has devices"; the fake runner keeps it off
        // PortMIDI, and no OSC listener is enabled so nothing binds a socket.
        Devices =
        [
            new ControlDeviceInstanceConfig
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "X-Touch MINI",
                Protocol = ControlDeviceProtocol.MIDI,
                IsEnabled = true,
                Binding = new ControlDeviceBindingConfig { MIDIInputDeviceId = 1, MIDIInputDeviceName = "X-Touch MINI" },
            },
            oscDevice,
        ],
    };

    private static ControlDeviceInstanceConfig OSCDevice(Guid id, string host, int port, Guid? listenerId = null) => new()
    {
        Id = id,
        Name = "X32",
        ProfileId = "behringer.x32.osc",
        Protocol = ControlDeviceProtocol.OSC,
        IsEnabled = true,
        Binding = new ControlDeviceBindingConfig
        {
            Alias = "x32",
            OSCHost = host,
            OSCPort = port,
            OSCListenerId = listenerId,
        },
    };

    private sealed class Harness
    {
        private readonly ManualResetEventSlim _stopBlocked = new(false);
        private readonly ManualResetEventSlim _stopRelease = new(false);

        public List<ControlSystemConfig> Created { get; } = [];

        public List<ControlInputSession> Sessions { get; } = [];

        public bool BlockStop { get; set; }

        public ControlWorkspaceViewModel CreateWorkspace()
        {
            var vm = new ControlWorkspaceViewModel
            {
                MIDICatalogProvider = () => null,
            };
            vm.DeviceInputSessionFactory = config =>
            {
                Created.Add(config);
                var session = new ControlInputSession(config, midiSessionFactory: _ => new BlockingMIDIRunner(this));
                Sessions.Add(session);
                return session;
            };
            return vm;
        }

        public bool WaitUntilStopBlocked(TimeSpan timeout) => _stopBlocked.Wait(timeout);

        public void ReleaseStop() => _stopRelease.Set();

        /// <summary>Pumps the dispatcher until no more sessions appear - the queued syncs have all run.</summary>
        public async Task<bool> WaitUntilQuiet(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var seen = Created.Count;
            var stableRounds = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                if (Created.Count != seen)
                {
                    seen = Created.Count;
                    stableRounds = 0;
                    continue;
                }

                if (++stableRounds >= 5)
                    return true;
            }

            return false;
        }

        internal void EnterStop()
        {
            if (!BlockStop)
                return;
            _stopBlocked.Set();
            _stopRelease.Wait();
        }
    }

    /// <summary>Fake MIDI device-session runner. Its <c>Stop</c> is called under the input session's own
    /// gate, which is exactly where the test needs a teardown to park.</summary>
    private sealed class BlockingMIDIRunner(Harness harness) : IControlMIDIDeviceSessionRunner, IDisposable
    {
        public void Start(ControlMIDIDeviceManager deviceManager, CancellationToken cancellationToken = default) { }

        public void Stop() => harness.EnterStop();

        public void Dispose() { }
    }
}
