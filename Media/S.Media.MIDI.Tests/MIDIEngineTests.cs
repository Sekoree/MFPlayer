using S.Media.Core.Errors;
using S.Media.MIDI.Config;
using S.Media.MIDI.Runtime;
using Xunit;

namespace S.Media.MIDI.Tests;

public sealed class MIDIEngineTests
{
    [Fact]
    public void InitializeAndTerminate_AreIdempotent()
    {
        using var engine = new MIDIEngine();

        Assert.Equal(MediaResult.Success, engine.Initialize());
        Assert.True(engine.IsInitialized);

        Assert.Equal(MediaResult.Success, engine.Initialize());
        Assert.Equal(MediaResult.Success, engine.Terminate());
        Assert.Equal(MediaResult.Success, engine.Terminate());
        Assert.False(engine.IsInitialized);
    }

    [Fact]
    public void DefaultDeviceDiscovery_ProvidesAtLeastOneInputAndOutput()
    {
        using var engine = new MIDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());

        Assert.NotEmpty(engine.GetInputs());
        Assert.NotEmpty(engine.GetOutputs());
        Assert.NotNull(engine.GetDefaultInput());
        Assert.NotNull(engine.GetDefaultOutput());
    }

    [Fact]
    public void CreateInput_ReturnsDeviceNotFound_WhenDeviceIsNotInCatalog()
    {
        using var engine = new MIDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());

        var missing = new S.Media.MIDI.Types.MIDIDeviceInfo(int.MinValue, "Missing", IsInput: true, IsOutput: false, IsNative: false);

        var code = engine.CreateInput(missing, out var input);

        Assert.Equal((int)MediaErrorCode.MIDIDeviceNotFound_V2, code);
        Assert.Null(input);
    }

    [Fact]
    public void CreateOutput_ReturnsInvalidConfig_WhenDeviceDirectionIsInputOnly()
    {
        using var engine = new MIDIEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize());

        var inputOnly = engine.GetDefaultInput();
        Assert.NotNull(inputOnly);

        var code = engine.CreateOutput(inputOnly.Value, out var output);

        Assert.Equal((int)MediaErrorCode.MIDIInvalidConfig_V2, code);
        Assert.Null(output);
    }

    [Fact]
    public void CreateInput_UsesEngineReconnectOptions_WithNormalization()
    {
        using var engine = new MIDIEngine();
        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(new MIDIReconnectOptions
            {
                MaxReconnectAttempts = 0,
                ReconnectAttemptDelay = TimeSpan.FromMilliseconds(-2),
                ReconnectTimeout = TimeSpan.FromMilliseconds(-1),
            }));

        var defaultInput = engine.GetDefaultInput();
        Assert.NotNull(defaultInput);

        Assert.Equal(MediaResult.Success, engine.CreateInput(defaultInput.Value, out var input));
        Assert.NotNull(input);
        Assert.Equal(1, input!.ReconnectOptions.MaxReconnectAttempts);
        Assert.True(input.ReconnectOptions.ReconnectAttemptDelay >= TimeSpan.Zero);
        Assert.True(input.ReconnectOptions.ReconnectTimeout >= TimeSpan.Zero);
    }

    [Fact]
    public void CreateOutput_UsesEngineReconnectOptions_WithNormalization()
    {
        using var engine = new MIDIEngine();
        Assert.Equal(
            MediaResult.Success,
            engine.Initialize(new MIDIReconnectOptions
            {
                MaxReconnectAttempts = 0,
                ReconnectAttemptDelay = TimeSpan.FromMilliseconds(-2),
                ReconnectTimeout = TimeSpan.FromMilliseconds(-1),
            }));

        var defaultOutput = engine.GetDefaultOutput();
        Assert.NotNull(defaultOutput);

        Assert.Equal(MediaResult.Success, engine.CreateOutput(defaultOutput.Value, out var output));
        Assert.NotNull(output);
        Assert.Equal(1, output!.ReconnectOptions.MaxReconnectAttempts);
        Assert.True(output.ReconnectOptions.ReconnectAttemptDelay >= TimeSpan.Zero);
        Assert.True(output.ReconnectOptions.ReconnectTimeout >= TimeSpan.Zero);
    }
}
