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
    public void CreateOutput_RequireAudioPath_WhenSetOnOutputOptions()
    {
        using var engine = new NDIEngine();
        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(
                new NDIIntegrationOptions(),
                new NDILimitsOptions(),
                new NDIDiagnosticsOptions()));

        // RequireAudioPathOnStart now lives only on NDIOutputOptions (Issue 4.1).
        var code = engine.CreateOutput("ndi-out",
            new NDIOutputOptions { EnableAudio = false, RequireAudioPathOnStart = true },
            out var output);

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
            VideoJitterBufferFrames = 4,
            AudioJitterBufferMs = 90,
        }.Normalize();

        var diagnostics = new NDIDiagnosticsOptions
        {
            DiagnosticsTickInterval = TimeSpan.FromMilliseconds(4),
        };

        var options = new NDISourceOptions
        {
            QueueOverflowPolicy = NDIQueueOverflowPolicy.RejectIncoming,
            VideoFallbackMode = NDIVideoFallbackMode.PresentLastFrameOnRepeatedTimestamp,
            DiagnosticsTickInterval = TimeSpan.FromMilliseconds(1),
            VideoJitterBufferFrames = 6,
            AudioJitterBufferMs = 120,
        }.Normalize();

        Assert.Equal(NDIQueueOverflowPolicy.RejectIncoming, options.QueueOverflowPolicy);
        Assert.Equal(NDIVideoFallbackMode.PresentLastFrameOnRepeatedTimestamp, options.VideoFallbackMode);
        Assert.Equal(TimeSpan.FromMilliseconds(16), options.DiagnosticsTickInterval);
        Assert.Equal(6, options.VideoJitterBufferFrames);
        Assert.Equal(120, options.AudioJitterBufferMs);
    }

    [Fact]
    public void SourceOptions_Validate_ReturnsInvalidTick_ForNegativeOverride()
    {
        var options = new NDISourceOptions
        {
            DiagnosticsTickInterval = TimeSpan.FromMilliseconds(-1),
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
        Assert.Equal((int)MediaErrorCode.VideoFrameDisposed, output.PushFrame(badFrame));

        var audioSink = (IAudioSink)output;
        var audioFrame = new AudioFrame(
            Samples: new ReadOnlyMemory<float>(new float[8]),
            FrameCount: 4,
            SourceChannelCount: 2,
            Layout: AudioFrameLayout.Interleaved,
            SampleRate: 48_000,
            PresentationTime: TimeSpan.Zero);
        Assert.Equal(MediaResult.Success, audioSink.PushFrame(in audioFrame));

        Assert.Equal(MediaResult.Success, output.Stop());
        Assert.Equal((int)MediaErrorCode.NDIOutputPushAudioFailed, audioSink.PushFrame(in audioFrame));

        Assert.Equal(MediaResult.Success, engine.GetDiagnosticsSnapshot(out var snapshot));
        Assert.True(snapshot.VideoOutput.VideoPushSuccesses >= 1);
        Assert.True(snapshot.VideoOutput.VideoPushFailures >= 1);
        Assert.True(snapshot.VideoOutput.AudioPushSuccesses >= 1);
        Assert.True(snapshot.VideoOutput.AudioPushFailures >= 1);
        Assert.True(snapshot.VideoOutput.LastPushMs >= 0);
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

    [Fact]
    public void NDISourceOptions_LowLatency_HasExpectedValues()
    {
        var opts = NDISourceOptions.LowLatency;
        Assert.Equal(1, opts.VideoJitterBufferFrames);
        Assert.Equal(20, opts.AudioJitterBufferMs);
        Assert.Equal(TimeSpan.FromMilliseconds(50), opts.DiagnosticsTickInterval);
        Assert.Equal(MediaResult.Success, opts.Validate());
    }

    [Fact]
    public void NDISourceOptions_Balanced_MatchesDefaults()
    {
        var opts = NDISourceOptions.Balanced;
        var defaults = new NDISourceOptions();
        Assert.Equal(defaults.VideoJitterBufferFrames, opts.VideoJitterBufferFrames);
        Assert.Equal(defaults.AudioJitterBufferMs, opts.AudioJitterBufferMs);
        Assert.Equal(defaults.DiagnosticsTickInterval, opts.DiagnosticsTickInterval);
    }

    [Fact]
    public void NDISourceOptions_Safe_HasExpectedValues()
    {
        var opts = NDISourceOptions.Safe;
        Assert.Equal(6, opts.VideoJitterBufferFrames);
        Assert.Equal(150, opts.AudioJitterBufferMs);
        Assert.Equal(TimeSpan.FromMilliseconds(200), opts.DiagnosticsTickInterval);
        Assert.Equal(MediaResult.Success, opts.Validate());
    }

    [Fact]
    public void NDILimitsOptions_LowLatency_HasExpectedValues()
    {
        var opts = NDILimitsOptions.LowLatency;
        Assert.Equal(1, opts.VideoJitterBufferFrames);
        Assert.Equal(20, opts.AudioJitterBufferMs);
        Assert.Equal(4, opts.MaxPendingAudioFrames);
        Assert.Equal(4, opts.MaxPendingVideoFrames);
    }

    [Fact]
    public void NDILimitsOptions_Balanced_MatchesDefaults()
    {
        var opts = NDILimitsOptions.Balanced;
        var defaults = new NDILimitsOptions();
        Assert.Equal(defaults.VideoJitterBufferFrames, opts.VideoJitterBufferFrames);
        Assert.Equal(defaults.AudioJitterBufferMs, opts.AudioJitterBufferMs);
        Assert.Equal(defaults.MaxPendingAudioFrames, opts.MaxPendingAudioFrames);
        Assert.Equal(defaults.MaxPendingVideoFrames, opts.MaxPendingVideoFrames);
    }

    [Fact]
    public void NDILimitsOptions_Safe_HasExpectedValues()
    {
        var opts = NDILimitsOptions.Safe;
        Assert.Equal(6, opts.VideoJitterBufferFrames);
        Assert.Equal(150, opts.AudioJitterBufferMs);
        Assert.Equal(16, opts.MaxPendingAudioFrames);
        Assert.Equal(16, opts.MaxPendingVideoFrames);
    }

    [Fact]
    public void NDIDiagnosticsOptions_Default_MatchesConstructor()
    {
        var opts = NDIDiagnosticsOptions.Default;
        var defaults = new NDIDiagnosticsOptions();
        Assert.Equal(defaults.EnableDedicatedDiagnosticsThread, opts.EnableDedicatedDiagnosticsThread);
        Assert.Equal(defaults.DiagnosticsTickInterval, opts.DiagnosticsTickInterval);
    }

    [Fact]
    public void NDIEngine_ParameterlessInitialize_Succeeds()
    {
        using var engine = new NDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());
        Assert.True(engine.IsInitialized);
        Assert.Equal(MediaResult.Success, engine.Terminate());
    }

    [Fact]
    public void CreateAudioSource_NullReceiver_ThrowsArgumentNullException()
    {
        using var engine = new NDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());

        Assert.Throws<ArgumentNullException>(() =>
            engine.CreateAudioSource(null!, new NDISourceOptions(), out _));
    }

    [Fact]
    public void CreateVideoSource_NullReceiver_ThrowsArgumentNullException()
    {
        using var engine = new NDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());

        Assert.Throws<ArgumentNullException>(() =>
            engine.CreateVideoSource(null!, new NDISourceOptions(), out _));
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
