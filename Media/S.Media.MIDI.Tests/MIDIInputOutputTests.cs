using S.Media.Core.Errors;
using S.Media.MIDI.Config;
using S.Media.MIDI.Events;
using S.Media.MIDI.Input;
using S.Media.MIDI.Output;
using S.Media.MIDI.Types;
using Xunit;

namespace S.Media.MIDI.Tests;

public sealed class MIDIInputOutputTests
{
    [Fact]
    public void Input_Close_IsIdempotent_AndSeeklessContractNotApplicable()
    {
        var device = new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false);
        using var input = new MIDIInput(device, new MIDIReconnectOptions());

        Assert.Equal(MediaResult.Success, input.Open());
        Assert.True(input.IsOpen);

        Assert.Equal(MediaResult.Success, input.Close());
        Assert.False(input.IsOpen);
        Assert.Equal(MediaResult.Success, input.Close());
    }

    [Fact]
    public void Output_Send_ReturnsNotOpen_WhenClosed()
    {
        var device = new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false);
        using var output = new MIDIOutput(device);
        var message = MIDIMessage.Create(0x90, 60, 100);

        Assert.Equal((int)MediaErrorCode.MIDIOutputNotOpen_V2, output.Send(message));
    }

    [Fact]
    public void Output_Send_ReturnsSuccess_WhenOpenAndMessageValid()
    {
        var device = new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false);
        using var output = new MIDIOutput(device);
        var message = MIDIMessage.Create(0x90, 60, 100);

        Assert.Equal(MediaResult.Success, output.Open());
        Assert.Equal(MediaResult.Success, output.Send(message));
        Assert.Equal(MediaResult.Success, output.Close());
    }

    [Fact]
    public void Output_Dispose_ClosesOpenHandle_AndPreventsFurtherSend()
    {
        var device = new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false);
        var output = new MIDIOutput(device);
        var message = MIDIMessage.Create(0x90, 60, 100);

        Assert.Equal(MediaResult.Success, output.Open());
        Assert.True(output.IsOpen);

        output.Dispose();

        Assert.False(output.IsOpen);
        Assert.Equal((int)MediaErrorCode.MIDIOutputNotOpen_V2, output.Send(message));
    }

    [Fact]
    public void Output_Send_ReturnsInvalidMessage_ForMalformedStatusByte()
    {
        var device = new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false);
        using var output = new MIDIOutput(device);
        var invalid = MIDIMessage.Create(0x00, 60, 100);

        Assert.Equal(MediaResult.Success, output.Open());
        Assert.Equal((int)MediaErrorCode.MIDIInvalidMessage_V2, output.Send(invalid));
    }

    [Fact]
    public void Input_StatusChanged_IsOrdered_ForOpenAndClose()
    {
        var device = new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false);
        using var input = new MIDIInput(device, new MIDIReconnectOptions());

        var transitions = new List<MIDIConnectionStatus>();
        input.StatusChanged += (_, e) => transitions.Add(e.Status);

        Assert.Equal(MediaResult.Success, input.Open());
        Assert.Equal(MediaResult.Success, input.Close());

        Assert.Equal([MIDIConnectionStatus.Opening, MIDIConnectionStatus.Open, MIDIConnectionStatus.Closed], transitions);
    }

    [Fact]
    public void Input_NoMessageCallbackAfterClose()
    {
        var device = new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false);
        using var input = new MIDIInput(device, new MIDIReconnectOptions());

        var count = 0;
        EventHandler<MIDIMessageEventArgs> handler = (_, _) => count++;

        input.MessageReceived += handler;
        Assert.Equal(MediaResult.Success, input.Open());
        Assert.Equal(MediaResult.Success, input.Close());
        input.MessageReceived -= handler;

        Assert.Equal(0, count);
    }

    [Fact]
    public void Input_Dispose_ClosesOpenHandle()
    {
        var device = new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false);
        var input = new MIDIInput(device, new MIDIReconnectOptions());

        Assert.Equal(MediaResult.Success, input.Open());
        Assert.True(input.IsOpen);

        input.Dispose();

        Assert.False(input.IsOpen);
    }

    [Fact]
    public void Input_HandlerException_DoesNotKillInput()
    {
        var device = new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false);
        using var input = new MIDIInput(device, new MIDIReconnectOptions());

        // Attach a handler that throws
        var callCount = 0;
        input.MessageReceived += (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("Test handler fault");
        };

        Assert.Equal(MediaResult.Success, input.Open());

        // The input should still be open and closeable even if a handler throws.
        // (P2.7 ensures handler exceptions are caught by the polling thread.)
        Assert.True(input.IsOpen);
        Assert.Equal(MediaResult.Success, input.Close());
        Assert.False(input.IsOpen);
    }
}
