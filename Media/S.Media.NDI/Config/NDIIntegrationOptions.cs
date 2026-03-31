namespace S.Media.NDI.Config;

public sealed class NDIIntegrationOptions
{
    public string? RuntimeRootPath { get; init; }

    public bool UseIncomingVideoTimestamps { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, every output created by this engine must have
    /// <see cref="NDIOutputOptions.EnableAudio"/> set to <see langword="true"/>.
    /// <see cref="NDIEngine.CreateOutput"/> returns
    /// <see cref="MediaErrorCode.NDIOutputAudioStreamDisabled"/> if the condition is violated.
    /// </summary>
    public bool RequireAudioPathOnStart { get; init; }

    public NDIVideoSendFormat SendFormat { get; init; } = NDIVideoSendFormat.Program;

}
