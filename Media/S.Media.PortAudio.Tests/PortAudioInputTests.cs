using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Engine;
using S.Media.PortAudio.Input;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioInputTests
{
    [Fact]
    public void LiveInput_HasNanDuration_AndIsNonSeekable()
    {
        using var input = new PortAudioInput();

        Assert.True(double.IsNaN(input.DurationSeconds));
        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, input.Seek(1.0));
    }

    [Fact]
    public void ReadSamples_ZeroFillsRemainingDestination_WhenNotEnoughWritableFrames()
    {
        using var input = new PortAudioInput();
        var startCode = input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2 });

        // If native PortAudio hardware is unavailable in this environment, Start() now correctly
        // returns an error instead of silently falling back to synthetic sawtooth data (fix 6.3/6.5).
        // (10.4) PortAudioInitializeFailed is no longer returned here — the correct code is
        // PortAudioStreamOpenFailed for all DLL / entry-point failures from TryStartNativeStream.
        if (startCode != MediaResult.Success)
        {
            Assert.True(
                startCode is (int)MediaErrorCode.PortAudioStreamOpenFailed
                    or (int)MediaErrorCode.PortAudioStreamStartFailed,
                $"Unexpected start error code: {startCode}");
            return;
        }

        var destination = new float[10];
        // 4 frames × 2 ch = 8 samples written; last 2 samples must be zero-filled.
        var code = input.ReadSamples(destination, requestedFrameCount: 4, out var framesRead);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(4, framesRead);
        Assert.Equal(0f, destination[8]);
        Assert.Equal(0f, destination[9]);
    }

    [Fact]
    public void ReadSamples_ReturnsInputReadFailed_WhenNotRunning()
    {
        using var input = new PortAudioInput();

        var buffer = new float[8];
        var code = input.ReadSamples(buffer, 4, out _);

        Assert.Equal((int)MediaErrorCode.PortAudioInputReadFailed, code);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        using var input = new PortAudioInput();

        Assert.Equal(MediaResult.Success, input.Stop());

        var startCode = input.Start();
        // Start may succeed (hardware available) or fail (no hardware in CI).
        // (10.4) Only PortAudioStreamOpenFailed or PortAudioStreamStartFailed are valid now.
        Assert.True(
            startCode is MediaResult.Success
                or (int)MediaErrorCode.PortAudioStreamOpenFailed
                or (int)MediaErrorCode.PortAudioStreamStartFailed,
            $"Unexpected start code: {startCode}");

        Assert.Equal(MediaResult.Success, input.Stop());
        Assert.Equal(MediaResult.Success, input.Stop());
    }

    [Fact]
    public void Start_ReturnsInvalidConfig_ForZeroSampleRate()
    {
        using var input = new PortAudioInput();

        var code = input.Start(new AudioInputConfig { SampleRate = 0, ChannelCount = 2 });

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig, code);
    }

    [Fact]
    public void Start_ReturnsInvalidConfig_WhenExceedingUpperBounds()
    {
        using var input = new PortAudioInput();

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 999_999, ChannelCount = 2 }));

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 200 }));

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2, FramesPerBuffer = 100_000 }));
    }

    [Fact]
    public void SetInputDeviceByName_RaisesDeviceChanged_OnSuccess()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var inputs = engine.GetInputDevices();
        if (inputs.Count < 2) return; // need at least 2 devices to switch

        Assert.Equal(MediaResult.Success, engine.CreateInputByIndex(0, out var input));
        var transitions = new List<string>();
        input!.AudioDeviceChanged += (_, e) => transitions.Add(e.CurrentDevice.Name);

        var code = input.SetInputDeviceByName(inputs[1].Name);

        Assert.Equal(MediaResult.Success, code);
        Assert.Single(transitions);
        Assert.Equal(inputs[1].Name, input.Device.Name);
    }

    [Fact]
    public void SetInputDevice_ReturnsDeviceNotFound_ForUnknownId()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.CreateInputByIndex(-1, out var input));

        var code = input!.SetInputDevice(new AudioDeviceId("pa:99999"));

        Assert.Equal((int)MediaErrorCode.PortAudioDeviceNotFound, code);
    }

    [Fact]
    public void SetInputDeviceByIndex_MinusOne_UsesDefaultInputDevice()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var inputs = engine.GetInputDevices();
        if (inputs.Count < 2) return;

        Assert.Equal(MediaResult.Success, engine.CreateInputByIndex(1, out var input));
        var defaultInput = engine.GetDefaultInputDevice()!.Value;

        var code = input!.SetInputDeviceByIndex(-1);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(defaultInput.Id, input.Device.Id);
    }
}
