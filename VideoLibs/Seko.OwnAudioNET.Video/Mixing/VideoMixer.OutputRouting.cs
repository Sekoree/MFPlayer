using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

public sealed partial class VideoMixer
{
    public bool SetActiveSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_sources.TryGetValue(source.Id, out var registeredSource))
            return false;

        SetActiveSourceInternal(registeredSource, raiseEvent: true);
        return true;
    }

    private void SetActiveSourceInternal(VideoStreamSource? source, bool raiseEvent)
    {
        VideoStreamSource? oldSource;
        lock (_syncLock)
        {
            oldSource = ActiveSource;

            if (oldSource != null)
                oldSource.FrameReadyFast -= _activeSourceFrameHandler;

            _activeSourceId = source?.Id;

            if (source != null)
                source.FrameReadyFast += _activeSourceFrameHandler;
        }

        if (source != null)
            TryPushCurrentFrameToEngine(source);

        if (raiseEvent)
            RaiseActiveSourceChanged(oldSource, source);
    }

    private void TryPushCurrentFrameToEngine(VideoStreamSource source)
    {
        try
        {
            var masterTimestamp = Math.Max(0, _transport.Position);
            if (!source.TryGetFrameAtTime(masterTimestamp, out var frame))
                return;

            using (frame)
                _engine.PushFrame(frame, masterTimestamp);
        }
        catch
        {
            // Best effort snapshot push only. Regular frame-ready callbacks continue playback.
        }
    }

    private void RaiseActiveSourceChanged(VideoStreamSource? oldSource, VideoStreamSource? newSource)
    {
        ActiveSourceChanged?.Invoke(this, new VideoActiveSourceChangedEventArgs(oldSource, newSource));
    }
}

