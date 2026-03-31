namespace S.Media.NDI.Input;

/// <summary>
/// Internal contract shared by all coordinator implementations that demux NDI audio/video
/// from an <see cref="NDILib.NDIReceiver"/> into typed, pool-backed value objects.
/// </summary>
/// <remarks>
/// Two implementations exist:
/// <list type="bullet">
///   <item><see cref="NDICaptureCoordinator"/> — manual polling via <c>NDIlib_recv_capture_v3</c>
///     with an explicit jitter queue and audio ring buffer.</item>
///   <item><see cref="NDIFrameSyncCoordinator"/> — SDK-managed time-base corrector via
///     <c>NDIlib_framesync_*</c>. Preferred for live-playback and mixing scenarios.</item>
/// </list>
/// </remarks>
internal interface INDICaptureCoordinator : IDisposable
{
    /// <summary>
    /// Attempts to read the next video frame from the coordinator's pipeline.
    /// </summary>
    /// <param name="timeoutMs">Maximum milliseconds to wait for a frame.</param>
    /// <param name="frame">On success, the captured frame. The caller owns the pooled buffer.</param>
    /// <returns><see langword="true"/> when a frame was produced; <see langword="false"/> when none is available.</returns>
    bool TryReadVideo(uint timeoutMs, out CapturedVideoFrame frame);

    /// <summary>
    /// Attempts to read the next audio block from the coordinator's pipeline.
    /// </summary>
    /// <param name="timeoutMs">Maximum milliseconds to wait for audio.</param>
    /// <param name="frame">On success, the captured audio block. The caller owns the pooled buffer.</param>
    /// <returns><see langword="true"/> when audio was produced; <see langword="false"/> when none is available.</returns>
    bool TryReadAudio(uint timeoutMs, out CapturedAudioBlock frame);
}

