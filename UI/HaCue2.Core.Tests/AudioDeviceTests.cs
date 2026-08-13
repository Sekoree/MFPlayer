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
        // as a hint rather than an identity - so a substring in either direction counts.
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
        new() { Name = "line", Kind = AudioLineKind.LocalAudio, DeviceHint = hint };

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

    // ── the device picker's data (host APIs) ──────────────────────────────────────────────────

    private sealed class HostBackend : IAudioBackend
    {
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("0", "Scarlett 2i2 USB: Audio (hw:3,0)", 2, 48_000, false, "ALSA"),
            new("1", "default", 128, 48_000, true, "ALSA"),
            new("2", "Scarlett 2i2 3rd Gen Pro", 2, 48_000, false, "JACK"),
        ];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

        public IAudioOutput CreateOutput(
            string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();

        public IAudioSource CreateInput(
            string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void HostApisAreListedInTheOrderTheyWereEnumerated() =>
        // The picker's first control. Distinct, because a family with a dozen devices is still one
        // choice, and ordered, because the operator reads them as a list.
        Assert.Equal(["ALSA", "JACK"], new AudioDevices(new HostBackend()).HostApis);

    [Fact]
    public void OneHostApisDevicesAreWhatNarrowingMeans()
    {
        var devices = new AudioDevices(new HostBackend());

        Assert.Equal(
            ["Scarlett 2i2 USB: Audio (hw:3,0)", "default"],
            devices.OutputsFor("ALSA").Select(device => device.Name));

        // The SAME interface under another driver, with another name. Picking the wrong one is how a
        // show ends up on the wrong driver, which is the whole reason the filter exists.
        Assert.Equal(["Scarlett 2i2 3rd Gen Pro"], devices.OutputsFor("JACK").Select(d => d.Name));
    }

    [Fact]
    public void AnEmptyFilterIsEveryDevice() =>
        Assert.Equal(3, new AudioDevices(new HostBackend()).OutputsFor("").Count);

    [Fact]
    public void ABackendWithNoHostApiConceptOffersNoFilter() =>
        // miniaudio has no such notion, and a picker must read that as "do not offer the control"
        // rather than as "no devices".
        Assert.Empty(new AudioDevices(new FakeBackend()).HostApis);

    // ── resolving a hint to whatever the backend calls a device ───────────────────────────────

    [Fact]
    public void AHintIsResolvedToTheBACKENDsOwnDeviceId()
    {
        var devices = new HostBackend().EnumerateOutputDevices();

        // PortAudio's id is a global device INDEX. Handing it the name instead made it refuse the
        // line outright - every configured device failed to open, the bay ended up with no clock
        // master, and the first cue threw.
        Assert.Equal("2", AudioDevices.DeviceIdFor(devices, "Scarlett 2i2 3rd Gen Pro"));
        Assert.Equal("1", AudioDevices.DeviceIdFor(devices, "default"));
    }

    [Fact]
    public void AnExactNameWinsOverASubstringMatch()
    {
        var devices = new FakeBackend("Scarlett", "Scarlett 2i2 3rd Gen Pro").EnumerateOutputDevices();

        // Both contain each other, so substring matching alone would hand back whichever came first.
        // The one the operator picked from the list is the one they meant.
        Assert.Equal("1", AudioDevices.DeviceIdFor(devices, "Scarlett 2i2 3rd Gen Pro"));
        Assert.Equal("0", AudioDevices.DeviceIdFor(devices, "Scarlett"));
    }

    [Fact]
    public void AnEmptyHintIsTheBackendsOwnDefault() =>
        // Null, not the first device: "no hint" means "whatever this machine calls default", and
        // picking one ourselves would silently disagree with the operator's own sound settings.
        Assert.Null(AudioDevices.DeviceIdFor(new HostBackend().EnumerateOutputDevices(), ""));

    [Fact]
    public void AHintNothingMatchesFallsBackToTheDefaultRatherThanRefusing() =>
        // The status pass reports it ABSENT, which is where an operator should learn about it. Refusing
        // to open anything would take the rest of the rig down with one missing interface.
        Assert.Null(AudioDevices.DeviceIdFor(
            new HostBackend().EnumerateOutputDevices(), "an interface nobody has"));

    [Fact]
    public void WhatTheStatusPassCallsPresentIsWhatCanBeOpened()
    {
        var devices = new AudioDevices(new HostBackend());
        var line = new AudioLineDefinition { Name = "Main", DeviceHint = "Scarlett 2i2" };

        // Two matching rules - one for reporting presence, one for opening - is a green row over a
        // silent output. They are the same rule.
        Assert.Equal(DeviceAvailability.Present, devices.Match(line));
        Assert.NotNull(AudioDevices.DeviceIdFor(devices.Outputs, line.DeviceHint));
    }
}

