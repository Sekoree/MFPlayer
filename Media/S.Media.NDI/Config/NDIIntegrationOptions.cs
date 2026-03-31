namespace S.Media.NDI.Config;

public sealed class NDIIntegrationOptions
{
    public string? RuntimeRootPath { get; init; }

    public bool UseIncomingVideoTimestamps { get; init; } = true;


    public NDIVideoSendFormat SendFormat { get; init; } = NDIVideoSendFormat.Program;

}
