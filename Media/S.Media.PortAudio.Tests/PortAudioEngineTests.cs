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

