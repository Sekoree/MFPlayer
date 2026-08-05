namespace S.Media.Core.Audio;

/// <summary>
/// Optional backend capability for enumerating inputs and outputs under one native discovery lifetime.
/// </summary>
/// <remarks>
/// Some APIs, notably PortAudio on ALSA, perform expensive and noisy host probing on initialization.
/// Consumers should prefer this snapshot when available instead of independently reopening the same
/// native catalog for output and input lists.
/// </remarks>
public interface IAudioDeviceSnapshotProvider
{
    (IReadOnlyList<AudioDeviceInfo> Outputs, IReadOnlyList<AudioDeviceInfo> Inputs) EnumerateDevices();
}
