using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Time;
using Xunit;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// Plan D: both hardware backends re-anchor <see cref="IPlaybackClock.ElapsedSinceStart"/> to zero at
/// Start and Flush, and end the segment they were reporting on device loss. Each of those is an epoch
/// boundary, and the whole point of the workstream is that the OUTPUT announces it rather than leaving the
/// consumer to infer it from a regression. The device-loss cases need no hardware (the latch has an
/// internal test hook); the Start/Flush cases open the default device where one exists and SKIP with a
/// reason otherwise (<c>Xunit.SkippableFact</c>) - a device-gated case that merely <c>return</c>ed
/// reported "Passed" with zero assertions, which is indistinguishable from a case that actually verified the
/// contract. On this box all of them run against real hardware.
/// </summary>
public sealed class AudioOutputClockEpochTests
{
    private static readonly AudioFormat StandardFormat = new(48_000, 2);

    // ---- device loss (no hardware needed) -------------------------------------------------------

    [Fact]
    public void MiniAudio_DeviceLoss_TakesANewEpoch()
    {
        using var outputDevice = new MiniAudioOutput(StandardFormat);
        var before = outputDevice.Read();
        Assert.NotEqual(PlaybackEpoch.Single, before.EpochId);

        outputDevice.ForceDeviceLostForTest();

        var after = outputDevice.Read();
        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.False(after.IsAdvancing);
    }

    [SkippableFact]
    public void PortAudio_DeviceLoss_TakesANewEpoch()
    {
        var outputDevice = CreatePortAudioOutputOrSkip();

        using (outputDevice)
        {
            var before = outputDevice.Read();
            Assert.NotEqual(PlaybackEpoch.Single, before.EpochId);

            outputDevice.ForceDeviceLostForTest();

            var after = outputDevice.Read();
            Assert.NotEqual(before.EpochId, after.EpochId);
            Assert.False(after.IsAdvancing);
        }
    }

    // ---- Start / Flush (device-dependent) -------------------------------------------------------

    [SkippableFact]
    public void MiniAudio_StartAndFlush_EachTakeANewEpoch()
    {
        MiniAudioOutput outputDevice;
        try
        {
            outputDevice = new MiniAudioOutput(StandardFormat);
            outputDevice.Start();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"no usable default miniaudio output device: {ex.GetType().Name}: {ex.Message}");
            throw; // unreachable - Skip.If(true, ...) throws SkipException
        }

        using (outputDevice)
            AssertStartAndFlushEpochs(outputDevice, outputDevice.Flush, outputDevice.Start, outputDevice.Stop);
    }

    [SkippableFact]
    public void PortAudio_StartAndFlush_EachTakeANewEpoch()
    {
        var outputDevice = CreatePortAudioOutputOrSkip();

        using (outputDevice)
        {
            try
            {
                outputDevice.Start();
            }
            catch (Exception ex)
            {
                Skip.If(true, $"default PortAudio output device would not start: {ex.GetType().Name}: {ex.Message}");
                throw; // unreachable - Skip.If(true, ...) throws SkipException
            }

            AssertStartAndFlushEpochs(outputDevice, outputDevice.Flush, outputDevice.Start, outputDevice.Stop);
        }
    }

    /// <summary>The shared contract: a started output reports a real epoch, Flush moves to a new one with a
    /// zeroed clock, and a Stop/Start cycle moves to yet another - each read coherently through
    /// <see cref="IPlaybackClock.Read"/>.</summary>
    private static void AssertStartAndFlushEpochs(IPlaybackClock clock, Action flush, Action start, Action stop)
    {
        var started = clock.Read();
        Assert.NotEqual(PlaybackEpoch.Single, started.EpochId);
        Assert.Equal(started.EpochId, clock.EpochId);

        flush();
        var flushed = clock.Read();
        Assert.NotEqual(started.EpochId, flushed.EpochId);
        Assert.Equal(TimeSpan.Zero, flushed.Elapsed);

        stop();
        start();
        var restarted = clock.Read();
        Assert.NotEqual(flushed.EpochId, restarted.EpochId);
        Assert.NotEqual(started.EpochId, restarted.EpochId);
    }

    /// <summary>Opens the default PortAudio output, or SKIPS the calling test. Never returns null: a headless
    /// runner with no device must show up as a skip, not as a green case that asserted nothing.</summary>
    private static PortAudioOutput CreatePortAudioOutputOrSkip()
    {
        try
        {
            return new PortAudioOutput(StandardFormat);
        }
        catch (Exception ex)
        {
            // No usable default output device on this runner (common headless): device-dependent → skip.
            Skip.If(true, $"no usable default PortAudio output device: {ex.GetType().Name}: {ex.Message}");
            throw; // unreachable - Skip.If(true, ...) throws SkipException
        }
    }
}
