namespace S.Media.Core.Audio;

public readonly record struct AudioDeviceInfo(
	AudioDeviceId Id,
	string Name,
	string? HostApi = null,
	bool IsDefaultInput = false,
	bool IsDefaultOutput = false,
	/// <summary>
	/// <see langword="true"/> when this entry is a synthetic fallback device used when the
	/// native PortAudio runtime could not be loaded.  Fallback devices allow the engine to
	/// start in a "silent" mode but do not produce real audio output.
	/// </summary>
	bool IsFallback = false);
