using S.Media.Core.Audio;
using S.Media.Session;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// F-11: the acquired-audio-output lifecycle as direct contract tests. The behavioral coverage
/// through the session (fire, hot rebuild, teardown) lives in <see cref="AudioRouteRebuildTests"/>
/// and friends; these pin the OWNERSHIP rules the coordinator exists to state - resolve precedence
/// and the borrowed-versus-owned release - without needing a playing clip.
/// </summary>
public sealed class OutputLeaseCoordinatorTests
{
    [Fact]
    public void AHostLeaseWinsOverTheBackendAndStaysBorrowed()
    {
        var backend = new RecordingAudioBackend();
        var hostOutput = new SinkAudioOutput(new AudioFormat(48_000, 2));
        var released = 0;
        var coordinator = new OutputLeaseCoordinator(
            new AudioOutputDeviceCache(backend),
            backend,
            audioOutputFactory: (_, _) => new ClipAudioOutputLease(
                hostOutput, DisposeOutputOnRuntimeDispose: false, Release: () => released++),
            effectRegistry: null);

        var resolved = coordinator.ResolveAudioOutput("dev0", new AudioFormat(48_000, 2));

        Assert.Same(hostOutput, resolved.Output);
        Assert.False(resolved.DisposeOnRelease);
        Assert.Equal(0, backend.OutputCount); // the backend was never asked

        OutputLeaseCoordinator.Release(resolved);
        Assert.Equal(1, released); // the hook ran; the host keeps its output alive
    }

    [Fact]
    public void ABackendOutputIsOwnedAndDisposedOnRelease()
    {
        var backend = new RecordingAudioBackend();
        var coordinator = new OutputLeaseCoordinator(
            new AudioOutputDeviceCache(backend), backend, audioOutputFactory: null, effectRegistry: null);

        var resolved = coordinator.ResolveAudioOutput("dev0", new AudioFormat(48_000, 2));

        Assert.True(resolved.DisposeOnRelease);
        Assert.Equal(1, backend.OutputCount);
    }

    [Fact]
    public void ARoutelessClipFallsBackToTheDefaultDevice()
    {
        var backend = new RecordingAudioBackend();
        var coordinator = new OutputLeaseCoordinator(
            new AudioOutputDeviceCache(backend), backend, audioOutputFactory: null, effectRegistry: null);

        // RecordingAudioBackend's one device is the default.
        Assert.Equal("dev0", coordinator.ResolveFallbackOutputDeviceId());
    }

    [Fact]
    public void ReleaseRunsTheHookBeforeConsideringDisposal()
    {
        var events = new List<string>();
        var output = new DisposalRecordingOutput(() => events.Add("disposed"));
        OutputLeaseCoordinator.Release(new ClipAudioOutput(
            output, DisposeOnRelease: true, Release: () => events.Add("hook")));

        // Hook first, then dispose - a host must see its release before the sink goes away.
        Assert.Equal(["hook", "disposed"], events);
    }

    private sealed class DisposalRecordingOutput(Action onDispose) : IAudioOutput, IDisposable
    {
        public AudioFormat Format => new(48_000, 2);
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public void Dispose() => onDispose();
    }
}
