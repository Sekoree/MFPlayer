using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Engine;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioEngineTests
{
    [Fact]
    public void Lifecycle_StopAndTerminate_AreIdempotent()
    {
        using var engine = new PortAudioEngine();

        Assert.False(engine.IsInitialized);
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.True(engine.IsInitialized);
        Assert.Equal(MediaResult.Success, engine.Start());
        Assert.Equal(MediaResult.Success, engine.Stop());
        Assert.True(engine.IsInitialized);
        Assert.Equal(MediaResult.Success, engine.Stop());

        Assert.Equal(MediaResult.Success, engine.Terminate());
        Assert.False(engine.IsInitialized);
        Assert.Equal(MediaResult.Success, engine.Terminate());
    }

    [Fact]
    public void CreateOutput_ReturnsNotInitialized_WhenEngineNotInitialized()
    {
        using var engine = new PortAudioEngine();

        var code = engine.CreateOutput(new AudioDeviceId("default-output"), out var output);

        Assert.Equal((int)MediaErrorCode.PortAudioNotInitialized, code);
        Assert.Null(output);
    }

    [Fact]
    public void CreateOutputByName_TracksCreatedOutput()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var deviceName = engine.GetOutputDevices().First().Name;

        var code = engine.CreateOutputByName(deviceName, out var output);

        Assert.Equal(MediaResult.Success, code);
        Assert.NotNull(output);
        Assert.Single(engine.Outputs);
        Assert.Equal(deviceName, output!.Device.Name);
    }

    [Fact]
    public void GetDefaultDevices_ReturnsDeterministicValues_AfterInitialize()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var defaultOutput = engine.GetDefaultOutputDevice();
        var defaultInput = engine.GetDefaultInputDevice();

        Assert.NotNull(defaultOutput);
        Assert.NotNull(defaultInput);
        Assert.Contains(engine.GetOutputDevices(), device => device.Id == defaultOutput.Value.Id);
        Assert.Contains(engine.GetInputDevices(), device => device.Id == defaultInput.Value.Id);
    }

    [Fact]
    public void GetOutputDevices_DefaultOutputIsAlwaysFirst_WhenDefaultExists()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var outputs = engine.GetOutputDevices();
        var defaultOutput = engine.GetDefaultOutputDevice();

        Assert.NotEmpty(outputs);
        Assert.NotNull(defaultOutput);
        Assert.Equal(defaultOutput.Value.Id, outputs[0].Id);
    }

    [Fact]
    public void Initialize_WithoutPreferredHostApi_UsesDefaultHostApiSelection()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var hostApis = engine.GetHostApis();
        Assert.NotEmpty(hostApis);
        Assert.Contains(hostApis, api => api.IsDefault);
    }

    [Fact]
    public void Initialize_ReturnsError_WhenPreferredHostApiIsUnknown()
    {
        using var engine = new PortAudioEngine();

        var code = engine.Initialize(new AudioEngineConfig { PreferredHostApi = "definitely-not-a-real-host-api" });

        // (7.7) The error code is PortAudioInvalidConfig when native is present but the API name
        // is unknown, or PortAudioInitializeFailed when the native library itself is absent.
        Assert.True(
            code is (int)MediaErrorCode.PortAudioInvalidConfig
                 or (int)MediaErrorCode.PortAudioInitializeFailed,
            $"Unexpected error code: {code}");
    }

    [Fact]
    public void Initialize_ReturnsInitializeFailed_WhenCalledTwice()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        // (7.2) A second Initialize() without a prior Terminate() must be rejected.
        var code = engine.Initialize(new AudioEngineConfig());

        Assert.Equal((int)MediaErrorCode.PortAudioInitializeFailed, code);
    }

    [Fact]
    public void Initialize_ReturnsInvalidConfig_ForUpperBoundViolation()
    {
        using var engine = new PortAudioEngine();

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            engine.Initialize(new AudioEngineConfig { SampleRate = 999_999 }));
    }

    [Fact]
    public void CreateOutputByIndex_MinusOne_UsesDefaultOutputDevice()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var defaultOutput = engine.GetDefaultOutputDevice();
        Assert.NotNull(defaultOutput);

        var code = engine.CreateOutputByIndex(-1, out var output);

        Assert.Equal(MediaResult.Success, code);
        Assert.NotNull(output);
        Assert.Equal(defaultOutput.Value.Id, output!.Device.Id);
    }

    [Fact]
    public void RemoveOutput_StopsAndDisposesOutput_AndRemovesFromList()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var deviceId = engine.GetOutputDevices()[0].Id;
        var code = engine.CreateOutput(deviceId, out var output);
        Assert.Equal(MediaResult.Success, code);
        Assert.NotNull(output);
        Assert.Single(engine.Outputs);

        var removeCode = engine.RemoveOutput(output!);
        Assert.Equal(MediaResult.Success, removeCode);
        Assert.Empty(engine.Outputs);
    }

    [Fact]
    public void RemoveOutput_ReturnsDeviceNotFound_ForUntrackedOutput()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        // Create another engine just to get a foreign output.
        using var other = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, other.Initialize(new AudioEngineConfig()));
        var foreignDeviceId = other.GetOutputDevices()[0].Id;
        Assert.Equal(MediaResult.Success, other.CreateOutput(foreignDeviceId, out var foreignOutput));

        var code = engine.RemoveOutput(foreignOutput!);
        Assert.Equal((int)MediaErrorCode.PortAudioDeviceNotFound, code);
    }

    [Fact]
    public void OutputDisposedDirectly_IsRemovedFromEngineOutputsList()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        var deviceId = engine.GetOutputDevices()[0].Id;
        Assert.Equal(MediaResult.Success, engine.CreateOutput(deviceId, out var output));
        Assert.Single(engine.Outputs);

        // (7.5) Directly disposing the output must remove it from the engine's Outputs list.
        output!.Dispose();

        Assert.Empty(engine.Outputs);
    }

    [Fact]
    public void RefreshDevices_ReturnsSuccess_WhenNativeIsInitialized()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        // (7.6) If native PortAudio is available, RefreshDevices returns Success.
        // If it is not available (e.g. CI without audio), the engine falls back to phantom
        // devices and RefreshDevices is expected to return NotInitialized.
        var code = engine.RefreshDevices();
        Assert.True(
            code is MediaResult.Success or (int)MediaErrorCode.PortAudioNotInitialized,
            $"Unexpected RefreshDevices code: {code}");
    }

    [Fact]
    public void FallbackDevices_AreFlaggedAsIsFallback_BeforeNativeInit()
    {
        // (6.6) Before Initialize(), all devices are phantom/fallback.
        using var engine = new PortAudioEngine();

        var outputs = engine.GetOutputDevices();
        Assert.NotEmpty(outputs);
        Assert.All(outputs, d => Assert.True(d.IsFallback));
    }

    [Fact]
    public void Stop_StopsAllActiveOutputs()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());

        var deviceId = engine.GetOutputDevices()[0].Id;
        Assert.Equal(MediaResult.Success,
            engine.CreateOutput(deviceId, out var output));
        Assert.NotNull(output);
        _ = output!.Start(new AudioOutputConfig());  // may or may not start native stream

        // (7.4) engine.Stop() must stop all tracked outputs.
        Assert.Equal(MediaResult.Success, engine.Stop());
        Assert.Equal(AudioOutputState.Stopped, output.State);
    }

    [Fact]
    public void StateChanged_IsRaisedInOrder_ForInitializeStartStopTerminate()
    {
        using var engine = new PortAudioEngine();
        var transitions = new List<(AudioEngineState Previous, AudioEngineState Current)>();
        engine.StateChanged += (_, e) => transitions.Add((e.PreviousState, e.CurrentState));

        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());
        Assert.Equal(MediaResult.Success, engine.Stop());
        Assert.Equal(MediaResult.Success, engine.Terminate());

        Assert.Equal(
            [
                (AudioEngineState.Uninitialized, AudioEngineState.Initialized),
                (AudioEngineState.Initialized, AudioEngineState.Running),
                (AudioEngineState.Running, AudioEngineState.Initialized),
                (AudioEngineState.Initialized, AudioEngineState.Terminated),
            ],
            transitions);
    }

    [Fact]
    public void Dispose_ActsAsEventFence_ForStateChanged()
    {
        var engine = new PortAudioEngine();
        var events = 0;
        engine.StateChanged += (_, _) => events++;

        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(1, events);

        engine.Dispose();
        var eventsAfterDispose = events;

        var startCode = engine.Start();
        Assert.Equal((int)MediaErrorCode.PortAudioNotInitialized, startCode);
        Assert.Equal(eventsAfterDispose, events);
    }

    [Fact]
    public async Task Concurrent_StartStopTerminate_IsDeterministicAndNoThrow()
    {
        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));

        var startStop = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                _ = engine.Start();
                _ = engine.Stop();
            }
        });

        var terminator = Task.Run(async () =>
        {
            await Task.Delay(5);
            _ = engine.Terminate();
        });

        await Task.WhenAll(startStop, terminator).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AudioEngineState.Terminated, engine.State);
    }
}
