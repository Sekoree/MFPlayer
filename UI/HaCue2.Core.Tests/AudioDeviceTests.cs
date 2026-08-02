using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using HaCue2.Machine;
using S.Media.Core.Audio;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Matching a show's audio line against the devices this machine has.
/// </summary>
/// <remarks>
/// The rule these all serve: <b>never turn "nobody looked" into "absent"</b>. A red row nobody
/// verified trains operators to ignore the screen, which costs more than the missing information.
/// </remarks>
public sealed class AudioDeviceTests
{
    [Fact]
    public void NoBackendMeansUnknownRatherThanAbsent()
    {
        var devices = new AudioDevices();

        Assert.False(devices.Enumerated);
        Assert.Equal(DeviceAvailability.Unknown, devices.Match(Line("anything")));
    }

    [Fact]
    public void ABackendThatThrowsIsStillUnknown()
    {
        // A driver that will not start is a machine nobody could ask, not a machine with no devices.
        var devices = new AudioDevices(new BrokenBackend());

        Assert.False(devices.Enumerated);
        Assert.Equal(DeviceAvailability.Unknown, devices.Match(Line("Scarlett")));
    }

    [Theory]
    [InlineData("Scarlett 18i20 USB", "Scarlett 18i20")]
    [InlineData("Scarlett 18i20", "Scarlett 18i20 USB")]
    [InlineData("scarlett 18i20 usb", "Scarlett 18i20")]
    public void AHintMatchesEitherWayAndIgnoresCase(string deviceName, string hint)
    {
        // Device names pick up and lose suffixes between driver versions, and the hint is documented
        // as a hint rather than an identity — so a substring in either direction counts.
        var devices = new AudioDevices(new FakeBackend(deviceName));

        Assert.Equal(DeviceAvailability.Present, devices.Match(Line(hint)));
    }

    [Fact]
    public void AHintThatMatchesNothingIsAbsent()
    {
        var devices = new AudioDevices(new FakeBackend("Built-in Audio"));

        Assert.Equal(DeviceAvailability.Absent, devices.Match(Line("Scarlett 18i20")));
    }

    [Fact]
    public void AnEmptyHintMeansTheDefaultDevice()
    {
        Assert.Equal(
            DeviceAvailability.Present,
            new AudioDevices(new FakeBackend("Built-in Audio")).Match(Line("")));

        // ...which is still absent on a machine that enumerated no devices at all.
        Assert.Equal(DeviceAvailability.Absent, new AudioDevices(new FakeBackend()).Match(Line("")));
    }

    [Fact]
    public void OneWorkingBackendIsEnoughEvenBesideABrokenOne()
    {
        // A device the other backend cannot see is still on the machine.
        var devices = new AudioDevices(new BrokenBackend(), new FakeBackend("Scarlett 18i20"));

        Assert.True(devices.Enumerated);
        Assert.Equal(DeviceAvailability.Present, devices.Match(Line("Scarlett")));
    }

    [Fact]
    public void OnlyHostDeviceLinesAreAnsweredAtAll()
    {
        var environment = new MachineEnvironment(new AudioDevices(new FakeBackend("Built-in Audio")));

        // An NDI sender or a record file is reachable for reasons device enumeration cannot see, so
        // the environment does not pretend to know.
        Assert.Equal(
            DeviceAvailability.Unknown,
            environment.AudioLine(new AudioLineDefinition { Kind = AudioLineKind.Ndi, DeviceHint = "wall" }));

        Assert.Equal(DeviceAvailability.Present, environment.AudioLine(Line("Built-in")));
    }

    private static AudioLineDefinition Line(string hint) =>
        new() { Name = "line", Kind = AudioLineKind.PortAudio, DeviceHint = hint };

    private sealed class FakeBackend(params string[] names) : IAudioBackend
    {
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
            [.. names.Select((name, index) => new AudioDeviceInfo(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name, 2, 48_000, index == 0))];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();

        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class BrokenBackend : IAudioBackend
    {
        public string Name => "broken";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
            throw new InvalidOperationException("no driver");

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() =>
            throw new InvalidOperationException("no driver");

        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();

        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }
}
