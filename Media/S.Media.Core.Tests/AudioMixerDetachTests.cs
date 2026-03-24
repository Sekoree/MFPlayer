using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioMixerDetachTests
{
    [Fact]
    public void RemoveSource_AndClearSources_HaveParity_ForStopFailureOutcome()
    {
        var removeMixer = new AudioMixer();
        var clearMixer = new AudioMixer();
        var removeSource = new FakeAudioSource(stopCode: 77);
        var clearSource = new FakeAudioSource(stopCode: 77);

        removeMixer.AddSource(removeSource);
        clearMixer.AddSource(clearSource);
        removeMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });
        clearMixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });

        var removeCode = removeMixer.RemoveSource(removeSource.SourceId);
        var clearCode = clearMixer.ClearSources();

        Assert.Equal(removeCode, clearCode);
        Assert.Equal(1, removeMixer.SourceCount);
        Assert.Equal(1, clearMixer.SourceCount);
    }

    [Fact]
    public void RemoveSource_AndClearSources_HaveParity_ForDisposeExceptionOutcome()
    {
        var removeMixer = new AudioMixer();
        var clearMixer = new AudioMixer();
        var removeSource = new FakeAudioSource(stopCode: MediaResult.Success, throwOnDispose: true);
        var clearSource = new FakeAudioSource(stopCode: MediaResult.Success, throwOnDispose: true);

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
    public void RemoveSource_ReturnsStopError_AndDoesNotMutateRegistration()
    {
        var mixer = new AudioMixer();
        var source = new FakeAudioSource(stopCode: 1337);

        mixer.AddSource(source);
        mixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });

        var result = mixer.RemoveSource(source.SourceId);

        Assert.Equal(1337, result);
        Assert.Equal(1, mixer.SourceCount);
    }

    [Fact]
    public void ClearSources_ReturnsFirstErrorByRegistrationOrder()
    {
        var mixer = new AudioMixer();
        var first = new FakeAudioSource(stopCode: 7);
        var second = new FakeAudioSource(stopCode: 9);

        mixer.AddSource(first);
        mixer.AddSource(second);
        mixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions { StopOnDetach = true });

        var result = mixer.ClearSources();

        Assert.Equal(7, result);
        Assert.Equal(2, mixer.SourceCount);
    }

    [Fact]
    public void RemoveSource_DisposeOnDetach_DisposesAfterSuccessfulStop()
    {
        var mixer = new AudioMixer();
        var source = new FakeAudioSource(stopCode: MediaResult.Success);

        mixer.AddSource(source);
        mixer.ConfigureSourceDetachOptions(new MixerSourceDetachOptions
        {
            StopOnDetach = true,
            DisposeOnDetach = true,
        });

        var result = mixer.RemoveSource(source.SourceId);

        Assert.Equal(MediaResult.Success, result);
        Assert.Equal(0, mixer.SourceCount);
        Assert.Equal(1, source.DisposeCalls);
    }

    private sealed class FakeAudioSource(int stopCode, bool throwOnDispose = false) : IAudioSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();

        public AudioSourceState State => AudioSourceState.Stopped;

        public int DisposeCalls { get; private set; }

        public int Start() => MediaResult.Success;

        public int Stop() => stopCode;

        public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
        {
            framesRead = 0;
            return MediaResult.Success;
        }

        public int Seek(double positionSeconds) => MediaResult.Success;

        public double PositionSeconds => 0;

        public double DurationSeconds => 0;

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

