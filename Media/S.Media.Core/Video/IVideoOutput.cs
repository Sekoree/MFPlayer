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
    /// <para>
    /// <b>Blocking behaviour varies by implementation:</b><br/>
    /// — <c>NDIVideoOutput</c> with <c>ClockVideo = true</c>: blocks for approximately one frame
    ///   interval to pace the NDI stream. Do not call from a latency-sensitive thread.<br/>
    /// — <c>SDL3VideoView</c> and <c>AvaloniaVideoOutput</c>: non-blocking; returns immediately.
    /// </para>
    /// <para>
    /// The mixer's <c>VideoPresentLoop</c> accounts for this variance via
    /// <see cref="AVMixerConfig.PresentationHostPolicy"/>: use
    /// <see cref="VideoDispatchPolicy.BackgroundWorker"/> to offload blocking outputs
    /// to their own worker threads.
    /// </para>
    /// </summary>
    int PushFrame(VideoFrame frame);

    /// <summary>
    /// Pushes a decoded video frame with an explicit presentation timestamp.
    /// <inheritdoc cref="PushFrame(VideoFrame)" path="/remarks"/>
    /// </summary>
    int PushFrame(VideoFrame frame, TimeSpan presentationTime);
}
