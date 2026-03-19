using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Describes a routing change for a mixer-managed output.
/// </summary>
public sealed class VideoOutputSourceChangedEventArgs : EventArgs
{
	/// <summary>Initializes a new instance describing an output source transition.</summary>
	public VideoOutputSourceChangedEventArgs(IVideoOutput output, FFVideoSource? oldSource, FFVideoSource? newSource)
	{
		Output = output ?? throw new ArgumentNullException(nameof(output));
		OldSource = oldSource;
		NewSource = newSource;
	}

	/// <summary>The output whose source binding changed.</summary>
	public IVideoOutput Output { get; }

	/// <summary>The previously bound source, or <see langword="null"/>.</summary>
	public FFVideoSource? OldSource { get; }

	/// <summary>The newly bound source, or <see langword="null"/>.</summary>
	public FFVideoSource? NewSource { get; }
}

