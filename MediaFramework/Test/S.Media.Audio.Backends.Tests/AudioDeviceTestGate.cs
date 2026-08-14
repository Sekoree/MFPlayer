using Xunit;

namespace S.Media.Audio.Backends.Tests;

/// <summary>Real PortAudio/ALSA opens are a hardware acceptance tier, not a device-less CI test.
/// Some ALSA devices abort inside PortAudio rather than returning an error, which kills the entire
/// testhost and hides otherwise-completed managed results. Opt in only on a known release rig.</summary>
internal static class AudioDeviceTestGate
{
    public const string EnvironmentVariable = "MFP_RUN_PORTAUDIO_DEVICE_TESTS";

    public static void RequirePortAudio()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal),
            $"real PortAudio device test; set {EnvironmentVariable}=1 on a configured audio release rig");
    }
}
