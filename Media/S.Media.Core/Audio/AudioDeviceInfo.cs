namespace S.Media.Core.Audio;

public readonly record struct AudioDeviceInfo(
	AudioDeviceId Id,
	string Name,
	string? HostApi = null,
	bool IsDefaultInput = false,
	bool IsDefaultOutput = false);
