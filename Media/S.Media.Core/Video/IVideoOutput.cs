namespace S.Media.Core.Video;

public interface IVideoOutput : IDisposable
{
    Guid Id { get; }

    /// <summary>Current output state.</summary>
    VideoOutputState State { get; }

    int Start(VideoOutputConfig config);

    int Stop();

    /// <summary>
    /// Pushes a decoded video frame to this output for immediate or scheduled presentation.
    /// </summary>
    /// <remarks>
    /// <b>Blocking behaviour varies by implementation:</b><br/>
    /// Outputs that perform hardware-paced presentation (e.g. those backed by a blocking
    /// send API) may block for approximately one frame interval. Non-blocking implementations
    /// enqueue the frame and return immediately.<br/>
    /// <br/>
    /// Use <see cref="S.Media.Core.Mixing.AVMixerConfig.PresentationHostPolicy"/> with
    /// <see cref="S.Media.Core.Mixing.VideoDispatchPolicy.BackgroundWorker"/> to route
    /// potentially-blocking outputs to their own worker threads, isolating them from the
    /// mixer's presentation loop.
    /// </remarks>
    int PushFrame(VideoFrame frame);

    /// <summary>
    /// Pushes a decoded video frame with an explicit presentation timestamp.
    /// </summary>
    /// <remarks><inheritdoc cref="PushFrame(VideoFrame)" path="/remarks"/></remarks>
    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
