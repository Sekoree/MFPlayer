using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

public sealed partial class VideoMixer
{
    public bool AddOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        // Align with AudioMixer's single-engine model: one primary output sink per mixer.
        if (_outputs.Count > 0 && !_outputs.ContainsKey(output.Id))
            return false;

        if (!_outputs.TryAdd(output.Id, output))
            return false;

        try
        {
            output.DetachSource();
        }
        catch
        {
            // Best effort reset when mixer takes ownership of routing.
        }

        ApplyOutputPresentationSyncMode(output);

        return true;
    }

    public bool RemoveOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        if (!_outputs.TryRemove(output.Id, out var registeredOutput))
            return false;

        UnbindOutputInternal(registeredOutput, raiseEvent: true);

        return true;
    }

    public IVideoOutput[] GetOutputs()
    {
        ThrowIfDisposed();
        return _outputs.Values.ToArray();
    }

    public void ClearOutputs()
    {
        ThrowIfDisposed();

        foreach (var output in _outputs.Values.ToArray())
            RemoveOutput(output);
    }

    public bool BindOutputToSource(IVideoOutput output, VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);

        if (!_outputs.TryGetValue(output.Id, out var registeredOutput))
            return false;

        if (!_sources.TryGetValue(source.Id, out var registeredSource))
            return false;

        VideoStreamSource? oldSource;
        lock (_syncLock)
        {
            oldSource = GetSourceForOutputLocked(output.Id);
            if (ReferenceEquals(oldSource, registeredSource))
                return true;
        }

        ApplyOutputPresentationSyncMode(registeredOutput);

        try
        {
            if (!registeredOutput.AttachSource(registeredSource))
                return false;
        }
        catch
        {
            return false;
        }

        lock (_syncLock)
        {
            oldSource = GetSourceForOutputLocked(output.Id);
            if (ReferenceEquals(oldSource, registeredSource))
                return true;

            RemoveBindingLocked(output.Id, oldSource?.Id);
            AddBindingLocked(output.Id, registeredSource.Id);
        }

        TryPushCurrentFrameToOutput(registeredOutput, registeredSource);

        RaiseOutputSourceChanged(registeredOutput, oldSource, registeredSource);
        return true;
    }

    public bool UnbindOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return UnbindOutputInternal(output, raiseEvent: true);
    }

    public IVideoOutput[] GetOutputsForSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return GetOutputsForSourceInternal(source);
    }

    public VideoStreamSource? GetSourceForOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        lock (_syncLock)
            return GetSourceForOutputLocked(output.Id);
    }

    private IVideoOutput[] GetOutputsForSourceInternal(VideoStreamSource source)
    {
        lock (_syncLock)
        {
            if (!_sourceOutputBindings.TryGetValue(source.Id, out var outputIds) || outputIds.Count == 0)
                return [];

            return outputIds
                .Select(outputId => _outputs.TryGetValue(outputId, out var output) ? output : null)
                .Where(output => output != null)
                .Cast<IVideoOutput>()
                .ToArray();
        }
    }

    private bool UnbindOutputInternal(IVideoOutput output, bool raiseEvent)
    {
        VideoStreamSource? oldSource;
        lock (_syncLock)
            oldSource = GetSourceForOutputLocked(output.Id);

        try
        {
            output.DetachSource();
        }
        catch
        {
            if (oldSource == null)
                return false;
        }

        if (oldSource == null)
            return false;

        lock (_syncLock)
            RemoveBindingLocked(output.Id, oldSource.Id);

        if (raiseEvent)
            RaiseOutputSourceChanged(output, oldSource, null);

        return true;
    }

    private VideoStreamSource? GetSourceForOutputLocked(Guid outputId)
    {
        if (!_outputSourceBindings.TryGetValue(outputId, out var sourceId))
            return null;

        return _sources.TryGetValue(sourceId, out var source) ? source : null;
    }

    private void AddBindingLocked(Guid outputId, Guid sourceId)
    {
        _outputSourceBindings[outputId] = sourceId;

        if (!_sourceOutputBindings.TryGetValue(sourceId, out var outputIds))
        {
            outputIds = [];
            _sourceOutputBindings[sourceId] = outputIds;
        }

        outputIds.Add(outputId);
    }

    private void RemoveBindingLocked(Guid outputId, Guid? sourceId)
    {
        _outputSourceBindings.Remove(outputId);

        if (!sourceId.HasValue)
            return;

        if (!_sourceOutputBindings.TryGetValue(sourceId.Value, out var outputIds))
            return;

        outputIds.Remove(outputId);
        if (outputIds.Count == 0)
            _sourceOutputBindings.Remove(sourceId.Value);
    }

    private void ApplyOutputPresentationSyncMode(IVideoOutput output)
    {
        if (output is IVideoPresentationSyncAwareOutput syncAwareOutput)
            syncAwareOutput.PresentationSyncMode = Config.PresentationSyncMode;
    }

    private void TryPushCurrentFrameToOutput(IVideoOutput output, VideoStreamSource source)
    {
        try
        {
            var masterTimestamp = Math.Max(0, _engine.Position);
            if (!source.TryGetFrameAtTime(masterTimestamp, out var frame))
                return;

            using (frame)
                output.PushFrame(frame, masterTimestamp);
        }
        catch
        {
            // Best effort snapshot push only. Regular frame-ready callbacks continue playback.
        }
    }


    private void RaiseOutputSourceChanged(IVideoOutput output, VideoStreamSource? oldSource, VideoStreamSource? newSource)
    {
        if (ReferenceEquals(oldSource, newSource))
            return;

        OutputSourceChanged?.Invoke(this, new VideoOutputSourceChangedEventArgs(output, oldSource, newSource));
    }
}

