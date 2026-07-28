using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// Device-loss detection (review finding: a disappearing device silently froze the mastered clock).
/// Hardware-dependent behavior is tested at the state-machine level: the latch decisions are pure
/// internal methods, and the fail-fast contract (Submit throws, WaitForCapacity reports no capacity,
/// IsAdvancing false) is driven through the internal test latch without needing a real device.
/// </summary>
public sealed class AudioOutputDeviceLossTests(ITestOutputHelper output)
{
    private static readonly AudioFormat StandardFormat = new(48_000, 2);

    // ---- miniaudio latch decision (stop-notification driven) -----------------------------------

    [Fact]
    public void MiniAudio_ShouldLatch_OnUnexpectedStopWhileRunning() =>
        Assert.True(MiniAudioOutput.ShouldLatchDeviceLost(
            isRunning: true, intentionalStopPending: false, stoppedAfterFlush: false));

    [Theory]
    [InlineData(false, false, false)] // not running: stop notifications before Start/after Stop are not loss
    [InlineData(true, true, false)]   // deliberate Stop/Flush in flight
    [InlineData(true, false, true)]   // parked after Flush until the next producer call restarts it
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void MiniAudio_ShouldNotLatch_WhenTheStopWasOursOrTheDeviceIsNotRunning(
        bool isRunning, bool intentionalStopPending, bool stoppedAfterFlush) =>
        Assert.False(MiniAudioOutput.ShouldLatchDeviceLost(isRunning, intentionalStopPending, stoppedAfterFlush));

    // ---- PortAudio latch decision (StreamActive-poll driven) -----------------------------------

    [Fact]
    public void PortAudio_ShouldLatch_WhenTheStreamReadsInactiveWhileItShouldBeRunning() =>
        Assert.True(PortAudioOutput.ShouldLatchDeviceLost(
            isRunning: true, stoppedAfterFlush: false, callbackFaulted: false, streamActive: 0));

    [Theory]
    [InlineData(true, false, false, 1)]   // healthy: active
    [InlineData(true, false, false, -1)]  // negative = handle query error/closed, handled by lifecycle paths
    [InlineData(false, false, false, 0)]  // legitimately inactive before Start / after Stop
    [InlineData(true, true, false, 0)]    // legitimately inactive after Flush parked the stream
    [InlineData(true, false, true, 0)]    // callback fault already has its own (richer) fail-fast latch
    public void PortAudio_ShouldNotLatch_WhenInactiveIsLegitimateOrTheStreamIsHealthy(
        bool isRunning, bool stoppedAfterFlush, bool callbackFaulted, int streamActive) =>
        Assert.False(PortAudioOutput.ShouldLatchDeviceLost(isRunning, stoppedAfterFlush, callbackFaulted, streamActive));

    // ---- fail-fast contract once latched (no device required for miniaudio) -------------------

    [Fact]
    public void MiniAudio_LatchedDeviceLoss_FailsSubmitWaitForCapacityAndIsAdvancingFast()
    {
        using var outputDevice = new MiniAudioOutput(StandardFormat);

        // Sanity pre-latch: an un-started output accepts Submit (prefill) and reports capacity.
        outputDevice.Submit(new float[StandardFormat.Channels * 4]);
        Assert.True(outputDevice.WaitForCapacity(64, CancellationToken.None));
        Assert.False(outputDevice.DeviceLost);

        outputDevice.ForceDeviceLostForTest();

        Assert.True(outputDevice.DeviceLost);
        Assert.False(outputDevice.IsAdvancing);
        var ex = Assert.Throws<InvalidOperationException>(() => outputDevice.Submit(new float[StandardFormat.Channels * 4]));
        Assert.Contains("lost", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Must fail immediately, not after the 5s pacing timeout.
        var started = Environment.TickCount64;
        Assert.False(outputDevice.WaitForCapacity(64, CancellationToken.None));
        Assert.True(Environment.TickCount64 - started < 1000, "latched WaitForCapacity must fail fast");
    }

    [Fact]
    public void PortAudio_LatchedDeviceLoss_FailsSubmitWaitForCapacityAndIsAdvancingFast()
    {
        PortAudioOutput outputDevice;
        try
        {
            outputDevice = new PortAudioOutput(StandardFormat);
        }
        catch (Exception ex)
        {
            // No usable default output device on this runner (common headless): device-dependent → skip.
            output.WriteLine($"skipped: no usable default PortAudio output device: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        using (outputDevice)
        {
            Assert.False(outputDevice.DeviceLost);

            outputDevice.ForceDeviceLostForTest();

            Assert.True(outputDevice.DeviceLost);
            Assert.False(outputDevice.IsAdvancing);
            var ex = Assert.Throws<InvalidOperationException>(() => outputDevice.Submit(new float[StandardFormat.Channels * 4]));
            Assert.Contains("lost", ex.Message, StringComparison.OrdinalIgnoreCase);

            var started = Environment.TickCount64;
            Assert.False(outputDevice.WaitForCapacity(64, CancellationToken.None));
            Assert.True(Environment.TickCount64 - started < 1000, "latched WaitForCapacity must fail fast");
        }
    }
}
