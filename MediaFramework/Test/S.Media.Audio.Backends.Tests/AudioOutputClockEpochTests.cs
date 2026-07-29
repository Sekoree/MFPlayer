using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Time;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// Plan D: both hardware backends re-anchor <see cref="IPlaybackClock.ElapsedSinceStart"/> to zero at
/// Start and Flush, and end the segment they were reporting on device loss. Each of those is an epoch
/// boundary, and the whole point of the workstream is that the OUTPUT announces it rather than leaving the
/// consumer to infer it from a regression. The device-loss cases need no hardware (the latch has an
/// internal test hook); the Start/Flush cases open the default device where one exists and report a skip
/// reason otherwise, matching <c>AudioBackendConformanceTests</c>.
/// </summary>
public sealed class AudioOutputClockEpochTests(ITestOutputHelper output)
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

    [Fact]
    public void PortAudio_DeviceLoss_TakesANewEpoch()
    {
        if (TryCreatePortAudioOutput() is not { } outputDevice)
            return;

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

    [Fact]
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
            output.WriteLine($"skipped: no usable default miniaudio output device: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        using (outputDevice)
            AssertStartAndFlushEpochs(outputDevice, outputDevice.Flush, outputDevice.Start, outputDevice.Stop);
    }

    [Fact]
    public void PortAudio_StartAndFlush_EachTakeANewEpoch()
    {
        if (TryCreatePortAudioOutput() is not { } outputDevice)
            return;

        using (outputDevice)
        {
            try
            {
                outputDevice.Start();
            }
            catch (Exception ex)
            {
                output.WriteLine($"skipped: default PortAudio output device would not start: {ex.GetType().Name}: {ex.Message}");
                return;
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

    private PortAudioOutput? TryCreatePortAudioOutput()
    {
        try
        {
            return new PortAudioOutput(StandardFormat);
        }
        catch (Exception ex)
        {
            // No usable default output device on this runner (common headless): device-dependent → skip.
            output.WriteLine($"skipped: no usable default PortAudio output device: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
