namespace S.Media.Core.Video;

public interface IVideoOutput : IDisposable
{
    Guid Id { get; }

    int Start(VideoOutputConfig config);

    int Stop();

    int PushFrame(VideoFrame frame);

    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
