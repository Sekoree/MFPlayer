using S.Media.Core.Errors;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Runtime;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDIEngineAndOptionsTests
{
    [Fact]
    public void InitializeAndTerminate_AreIdempotent()
    {
        using var engine = new NDIEngine();

        Assert.Equal(MediaResult.Success, engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions()));
        Assert.True(engine.IsInitialized);

        Assert.Equal(MediaResult.Success, engine.Terminate());
        Assert.Equal(MediaResult.Success, engine.Terminate());
        Assert.False(engine.IsInitialized);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_ReturnsUnavailable_WhenNotInitialized()
    {
        using var engine = new NDIEngine();

        var code = engine.GetDiagnosticsSnapshot(out _);

        Assert.Equal((int)MediaErrorCode.NDIDiagnosticsSnapshotUnavailable, code);
    }

    [Fact]
    public void CreateOutput_UsesIntegrationRequireAudioPath_WhenEnabledGlobally()
    {
        using var engine = new NDIEngine();
        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(
                new NDIIntegrationOptions { RequireAudioPathOnStart = true },
                new NDILimitsOptions(),
                new NDIDiagnosticsOptions()));

        var code = engine.CreateOutput("ndi-out", new NDIOutputOptions { EnableAudio = false }, out var output);

        Assert.Equal((int)MediaErrorCode.NDIOutputAudioStreamDisabled, code);
        Assert.Null(output);
    }

    [Fact]
    public void SourceOptions_OverridePrecedenceAndTickClamp_AreDeterministic()
    {
        var limits = new NDILimitsOptions
        {
            QueueOverflowPolicy = NDIQueueOverflowPolicy.DropOldest,
            VideoFallbackMode = NDIVideoFallbackMode.NoFrame,
        }.Normalize();

        var diagnostics = new NDIDiagnosticsOptions
        {
            DiagnosticsTickInterval = TimeSpan.FromMilliseconds(4),
        };

        var options = new NDISourceOptions
        {
            QueueOverflowPolicyOverride = NDIQueueOverflowPolicy.RejectIncoming,
            VideoFallbackModeOverride = NDIVideoFallbackMode.PresentLastFrameOnRepeatedTimestamp,
            DiagnosticsTickIntervalOverride = TimeSpan.FromMilliseconds(1),
        }.Normalize();

        Assert.Equal(NDIQueueOverflowPolicy.RejectIncoming, options.ResolveQueueOverflowPolicy(limits));
        Assert.Equal(NDIVideoFallbackMode.PresentLastFrameOnRepeatedTimestamp, options.ResolveVideoFallbackMode(limits));
        Assert.Equal(TimeSpan.FromMilliseconds(16), options.ResolveDiagnosticsTick(diagnostics));
    }

    [Fact]
    public void SourceOptions_Validate_ReturnsInvalidTick_ForNegativeOverride()
    {
        var options = new NDISourceOptions
        {
            DiagnosticsTickIntervalOverride = TimeSpan.FromMilliseconds(-1),
        };

        Assert.Equal((int)MediaErrorCode.NDIInvalidDiagnosticsTickOverride, options.Validate());
    }

    [Fact]
    public void DiagnosticsThread_PublishesSnapshots_OnDedicatedThread()
    {
        using var engine = new NDIEngine();
        var eventGate = new ManualResetEventSlim(false);
        var currentThreadId = Environment.CurrentManagedThreadId;
        var callbackThreadId = 0;
        var callbackCount = 0;

        engine.DiagnosticsUpdated += (_, _) =>
        {
            callbackThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref callbackCount);
            eventGate.Set();
        };

        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(
                new NDIIntegrationOptions(),
                new NDILimitsOptions(),
                new NDIDiagnosticsOptions
                {
                    EnableDedicatedDiagnosticsThread = true,
                    PublishSnapshotsOnRequestOnly = false,
                    DiagnosticsTickInterval = TimeSpan.FromMilliseconds(16),
                }));

        Assert.True(eventGate.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(callbackCount >= 1);
        Assert.NotEqual(currentThreadId, callbackThreadId);
    }

    [Fact]
    public void Terminate_IsDiagnosticsPublicationFence()
    {
        using var engine = new NDIEngine();
        var firstEventGate = new ManualResetEventSlim(false);
        var callbackCount = 0;

        engine.DiagnosticsUpdated += (_, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            firstEventGate.Set();
        };

        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(
                new NDIIntegrationOptions(),
                new NDILimitsOptions(),
                new NDIDiagnosticsOptions
                {
                    EnableDedicatedDiagnosticsThread = true,
                    PublishSnapshotsOnRequestOnly = false,
                    DiagnosticsTickInterval = TimeSpan.FromMilliseconds(16),
                }));

        Assert.True(firstEventGate.Wait(TimeSpan.FromSeconds(2)));
        var beforeTerminate = Volatile.Read(ref callbackCount);

        Assert.Equal(MediaResult.Success, engine.Terminate());

        Thread.Sleep(100);
        Assert.Equal(beforeTerminate, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void GetDiagnosticsSnapshot_AggregatesOutputPushCounters()
    {
        using var engine = new NDIEngine();
        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(new NDIIntegrationOptions(), new NDILimitsOptions(), new NDIDiagnosticsOptions()));

        Assert.Equal(
            MediaResult.Success,
            engine.CreateOutput("ndi-output", new NDIOutputOptions { EnableAudio = true }, out var output));
        Assert.NotNull(output);

        Assert.Equal(MediaResult.Success, output!.Start(new VideoOutputConfig()));

        using var goodFrame = CreateVideoFrame();
        Assert.Equal(MediaResult.Success, output.PushFrame(goodFrame));

        using var badFrame = CreateVideoFrame();
        badFrame.Dispose();
        Assert.Equal((int)MediaErrorCode.NDIOutputPushVideoFailed, output.PushFrame(badFrame));

        var audioFrame = new AudioFrame(
            Samples: new ReadOnlyMemory<float>(new float[8]),
            FrameCount: 4,
            SourceChannelCount: 2,
            Layout: AudioFrameLayout.Interleaved,
            SampleRate: 48_000,
            PresentationTime: TimeSpan.Zero);
        Assert.Equal(MediaResult.Success, output.PushAudio(audioFrame, TimeSpan.Zero));

        Assert.Equal(MediaResult.Success, output.Stop());
        Assert.Equal((int)MediaErrorCode.NDIOutputPushAudioFailed, output.PushAudio(audioFrame, TimeSpan.Zero));

        Assert.Equal(MediaResult.Success, engine.GetDiagnosticsSnapshot(out var snapshot));
        Assert.True(snapshot.Video.VideoPushSuccesses >= 1);
        Assert.True(snapshot.Video.VideoPushFailures >= 1);
        Assert.True(snapshot.Video.AudioPushSuccesses >= 1);
        Assert.True(snapshot.Video.AudioPushFailures >= 1);
        Assert.True(snapshot.Video.LastPushMs >= 0);
    }

    [Fact]
    public void RequestOnlyDiagnosticsMode_DoesNotPublishBackgroundEvents()
    {
        using var engine = new NDIEngine();
        var callbackCount = 0;
        engine.DiagnosticsUpdated += (_, _) => Interlocked.Increment(ref callbackCount);

        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(
                new NDIIntegrationOptions(),
                new NDILimitsOptions(),
                new NDIDiagnosticsOptions
                {
                    EnableDedicatedDiagnosticsThread = true,
                    PublishSnapshotsOnRequestOnly = true,
                    DiagnosticsTickInterval = TimeSpan.FromMilliseconds(16),
                }));

        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    private static VideoFrame CreateVideoFrame()
    {
        var rgba = new byte[2 * 2 * 4];
        return new VideoFrame(
            width: 2,
            height: 2,
            pixelFormat: VideoPixelFormat.Rgba32,
            pixelFormatData: new Rgba32PixelFormatData(),
            presentationTime: TimeSpan.Zero,
            isKeyFrame: true,
            plane0: rgba,
            plane0Stride: 8);
    }
}

