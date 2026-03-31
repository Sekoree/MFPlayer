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
	bool IsFallback = false,
	/// <summary>Maximum number of input channels supported by this device. <see langword="null"/> when unknown.</summary>
	int? MaxInputChannels = null,
	/// <summary>Maximum number of output channels supported by this device. <see langword="null"/> when unknown.</summary>
	int? MaxOutputChannels = null,
	/// <summary>Default sample rate reported by the driver. <see langword="null"/> when unknown.</summary>
	double? DefaultSampleRate = null);
