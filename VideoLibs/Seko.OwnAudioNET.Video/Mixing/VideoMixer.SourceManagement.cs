using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

public sealed partial class VideoMixer
{
    public bool AddSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_sources.TryAdd(source.Id, source))
            return false;

        if (_engine.AddVideoSource(source))
            return true;

        _sources.TryRemove(source.Id, out _);
        return false;
    }

    public bool RemoveSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        if (!_sources.TryRemove(source.Id, out var registeredSource))
            return false;

        foreach (var output in GetOutputsForSourceInternal(registeredSource))
            UnbindOutputInternal(output, raiseEvent: true);

        _engine.RemoveVideoSource(registeredSource);
        return true;
    }

    public VideoStreamSource[] GetSources()
    {
        ThrowIfDisposed();
        return _sources.Values.ToArray();
    }

    public void ClearSources()
    {
        ThrowIfDisposed();

        foreach (var source in _sources.Values.ToArray())
            RemoveSource(source);
    }
}

