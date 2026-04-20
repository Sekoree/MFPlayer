namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Dual-media endpoint (e.g. NDIAVSink). Registered once in the graph;
/// receives both audio buffers and video frames.
/// </summary>
public interface IAVEndpoint : IAudioEndpoint, IVideoEndpoint
{
}

