using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoMixerDetachTests
{
    [Fact]
    public void RemoveSource_AndClearSources_HaveParity_ForStopFailureOutcome()
    {
        var removeMixer = new VideoMixer();
        var clearMixer = new VideoMixer();
        var removeSource = new FakeVideoSource(stopCode: 55);
        var clearSource = new FakeVideoSource(stopCode: 55);

        removeMixer.AddSource(removeSource);
        clearMixer.AddSource(clearSource);
        removeMixer.SetActiveSource(removeSource);
        clearMixer.SetActiveSource(clearSource);
        removeMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });
        clearMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });

        var removeCode = removeMixer.RemoveSource(removeSource.SourceId);
        var clearCode = clearMixer.ClearSources();

        Assert.Equal(removeCode, clearCode);
        Assert.Equal(1, removeMixer.SourceCount);
        Assert.Equal(1, clearMixer.SourceCount);
        Assert.Equal(removeSource.SourceId, removeMixer.ActiveSource?.SourceId);
        Assert.Equal(clearSource.SourceId, clearMixer.ActiveSource?.SourceId);
    }

    [Fact]
    public void RemoveSource_AndClearSources_HaveParity_ForDisposeExceptionOutcome()
    {
        var removeMixer = new VideoMixer();
        var clearMixer = new VideoMixer();
        var removeSource = new FakeVideoSource(stopCode: MediaResult.Success, throwOnDispose: true);
        var clearSource = new FakeVideoSource(stopCode: MediaResult.Success, throwOnDispose: true);

        removeMixer.AddSource(removeSource);
        clearMixer.AddSource(clearSource);
        removeMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { DisposeOnDetach = true });
        clearMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { DisposeOnDetach = true });

        var removeCode = removeMixer.RemoveSource(removeSource.SourceId);
        var clearCode = clearMixer.ClearSources();

        Assert.Equal((int)MediaErrorCode.MixerDetachStepFailed, removeCode);
        Assert.Equal(removeCode, clearCode);
        Assert.Equal(1, removeMixer.SourceCount);
        Assert.Equal(1, clearMixer.SourceCount);
    }

    [Fact]
    public void RemoveSource_StopFailure_DoesNotMutateSourcesOrActiveSource()
    {
        var mixer = new VideoMixer();
        var source = new FakeVideoSource(stopCode: 42);

        mixer.AddSource(source);
        mixer.SetActiveSource(source);
        mixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });

        var result = mixer.RemoveSource(source.SourceId);

        Assert.Equal(42, result);
        Assert.Equal(1, mixer.SourceCount);
        Assert.Equal(source.SourceId, mixer.ActiveSource?.SourceId);
    }

    [Fact]
    public void ClearSources_DisposeOnDetach_DisposesAllOnSuccess()
    {
        var mixer = new VideoMixer();
        var first = new FakeVideoSource(stopCode: MediaResult.Success);
        var second = new FakeVideoSource(stopCode: MediaResult.Success);

        mixer.AddSource(first);
        mixer.AddSource(second);
        mixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions
        {
            StopOnDetach = true,
            DisposeOnDetach = true,
        });

        var result = mixer.ClearSources();

        Assert.Equal(MediaResult.Success, result);
        Assert.Equal(0, mixer.SourceCount);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    private sealed class FakeVideoSource(int stopCode, bool throwOnDispose = false) : IVideoSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();

        public VideoSourceState State => VideoSourceState.Stopped;

        public int DisposeCalls { get; private set; }

        public int Start() => MediaResult.Success;

        public int Stop() => stopCode;

        public int ReadFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(
                width: 2,
                height: 2,
                pixelFormat: VideoPixelFormat.Rgba32,
                pixelFormatData: new Rgba32PixelFormatData(),
                presentationTime: TimeSpan.Zero,
                isKeyFrame: true,
                plane0: new byte[16],
                plane0Stride: 8);
            return MediaResult.Success;
        }

        public int Seek(double positionSeconds) => MediaResult.Success;

        public int SeekToFrame(long frameIndex) => MediaResult.Success;

        public int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount)
        {
            currentFrameIndex = 0;
            totalFrameCount = 0;
            return MediaResult.Success;
        }

        public double PositionSeconds => 0;

        public double DurationSeconds => 0;

        public long CurrentFrameIndex => 0;

        public long? CurrentDecodeFrameIndex => null;

        public long? TotalFrameCount => null;

        public bool IsSeekable => true;

        public void Dispose()
        {
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose fail");
            }

            DisposeCalls++;
        }
    }
}

