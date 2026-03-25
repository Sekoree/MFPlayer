using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Output;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioOutputTests
{
    [Fact]
    public void PushFrame_ValidatesRouteMapAndRequiresRunningState()
    {
        var output = CreateOutput();
        var frame = new AudioFrame(new float[8], FrameCount: 4, SourceChannelCount: 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        Assert.Equal((int)MediaErrorCode.PortAudioPushFailed, output.PushFrame(in frame, [0, 1]));

        var start = output.Start(new AudioOutputConfig());
        Assert.True(start is MediaResult.Success or (int)MediaErrorCode.PortAudioStreamOpenFailed or (int)MediaErrorCode.PortAudioStreamStartFailed);
        if (start != MediaResult.Success)
        {
            Assert.Equal((int)MediaErrorCode.PortAudioPushFailed, output.PushFrame(in frame, [0, 1]));
            return;
        }

        Assert.Equal(MediaResult.Success, output.PushFrame(in frame, [0, 1]));
        Assert.Equal((int)MediaErrorCode.AudioRouteMapMissing, output.PushFrame(in frame, ReadOnlySpan<int>.Empty));
        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, output.PushFrame(in frame, [0, 1], sourceChannelCount: 0));
        Assert.Equal((int)MediaErrorCode.AudioChannelCountMismatch, output.PushFrame(in frame, [0, 1], sourceChannelCount: 1));
        Assert.Equal((int)MediaErrorCode.AudioRouteMapInvalid, output.PushFrame(in frame, [-2, 1]));
        Assert.Equal((int)MediaErrorCode.AudioRouteMapInvalid, output.PushFrame(in frame, [0, 4], sourceChannelCount: 2));
    }

    [Fact]
    public void SetOutputDeviceByName_RaisesDeviceChanged_AfterCommittedChange()
    {
        var output = CreateOutput();
        var transitions = new List<(string Previous, string Current)>();
        output.AudioDeviceChanged += (_, e) => transitions.Add((e.PreviousDevice.Name, e.CurrentDevice.Name));

        var code = output.SetOutputDeviceByName("Monitor Output");

        Assert.Equal(MediaResult.Success, code);
        Assert.Single(transitions);
        Assert.Equal(("Default Output", "Monitor Output"), transitions[0]);
        Assert.Equal("Monitor Output", output.Device.Name);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var output = CreateOutput();

        Assert.Equal(MediaResult.Success, output.Stop());
        var start = output.Start(new AudioOutputConfig());
        Assert.True(start is MediaResult.Success or (int)MediaErrorCode.PortAudioStreamOpenFailed or (int)MediaErrorCode.PortAudioStreamStartFailed);
        Assert.Equal(MediaResult.Success, output.Stop());
        Assert.Equal(MediaResult.Success, output.Stop());
    }

    [Fact]
    public void SetOutputDeviceByIndex_MinusOne_UsesDefaultOutputDevice()
    {
        var devices =
            new List<AudioDeviceInfo>
            {
                new(new AudioDeviceId("default-output"), "Default Output", IsDefaultOutput: true),
                new(new AudioDeviceId("monitor-output"), "Monitor Output"),
            };

        var output = new PortAudioOutput(devices[1], () => devices, new AudioEngineConfig(), () => devices[0]);

        var code = output.SetOutputDeviceByIndex(-1);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal("Default Output", output.Device.Name);
    }

    [Fact]
    public void Dispose_ActsAsEventFence_ForAudioDeviceChanged()
    {
        var output = CreateOutput();
        var events = 0;
        output.AudioDeviceChanged += (_, _) => events++;

        Assert.Equal(MediaResult.Success, output.SetOutputDeviceByName("Monitor Output"));
        Assert.Equal(1, events);

        output.Dispose();

        var code = output.SetOutputDeviceByName("Default Output");
        Assert.Equal((int)MediaErrorCode.PortAudioDeviceSwitchFailed, code);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Concurrent_DeviceSwitch_And_Dispose_DoesNotLeakEventsAfterDispose()
    {
        var output = CreateOutput();
        var events = 0;
        var disposed = 0;

        output.AudioDeviceChanged += (_, _) =>
        {
            if (Volatile.Read(ref disposed) == 1)
            {
                throw new InvalidOperationException("Event should not be raised after dispose completion.");
            }

            Interlocked.Increment(ref events);
        };

        var switcher = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                _ = output.SetOutputDeviceByIndex(i % 2);
            }
        });

        var disposer = Task.Run(async () =>
        {
            await Task.Delay(5);
            output.Dispose();
            Volatile.Write(ref disposed, 1);
        });

        await Task.WhenAll(switcher, disposer).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(events >= 0);
    }

    private static PortAudioOutput CreateOutput()
    {
        var devices =
            new List<AudioDeviceInfo>
            {
                new(new AudioDeviceId("default-output"), "Default Output"),
                new(new AudioDeviceId("monitor-output"), "Monitor Output"),
            };

        return new PortAudioOutput(devices[0], () => devices, new AudioEngineConfig(), () => devices[0]);
    }
}

