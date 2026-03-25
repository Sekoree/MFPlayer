using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL;

public sealed class OpenGLVideoEngine : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OpenGLVideoOutput> _outputs = [];
    private readonly Dictionary<Guid, Guid> _childToParent = [];
    private readonly OpenGLDiagnosticsEvents _diagnostics = new();
    private bool _disposed;

    public OpenGLVideoEngine(OpenGLClonePolicyOptions? policyOptions = null)
    {
        PolicyOptions = (policyOptions ?? new OpenGLClonePolicyOptions()).Normalize();
    }

    public OpenGLClonePolicyOptions PolicyOptions { get; }

    public OpenGLDiagnosticsEvents Diagnostics => _diagnostics;

    public Guid? ActiveOutputId { get; private set; }

    public IReadOnlyList<IVideoOutput> Outputs
    {
        get
        {
            lock (_gate)
            {
                return _outputs.Values.Cast<IVideoOutput>().ToArray();
            }
        }
    }

    public int AddOutput(IVideoOutput output)
    {
        if (output is not OpenGLVideoOutput glOutput)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (_outputs.ContainsKey(glOutput.Id))
            {
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
            }

            glOutput.SetDiagnostics(_diagnostics);
            _outputs.Add(glOutput.Id, glOutput);
            ActiveOutputId ??= glOutput.Id;
            return MediaResult.Success;
        }
    }

    public int RemoveOutput(IVideoOutput output)
    {
        return RemoveOutput(output.Id);
    }

    public int RemoveOutput(Guid outputId)
    {
        Guid[] childIds;

        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var output))
            {
                if (_disposed && _outputs.Count == 0)
                {
                    return MediaResult.Success;
                }

                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            childIds = output.CloneOutputIds.ToArray();
        }

        // Parent disposal deterministically destroys all clones in its subtree.
        foreach (var childId in childIds)
        {
            _ = RemoveOutput(childId);
        }

        var cloneGraphChanges = new List<(Guid ParentId, Guid CloneId, OpenGLCloneGraphChangeKind ChangeKind)>();

        lock (_gate)
        {
            if (_disposed && _outputs.Count == 0)
            {
                return MediaResult.Success;
            }

            if (!_outputs.TryGetValue(outputId, out var output))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            // Detach parent->child edges first.
            foreach (var childId in output.CloneOutputIds)
            {
                if (_outputs.TryGetValue(childId, out var child))
                {
                    child.CloneParentOutputId = null;
                }

                cloneGraphChanges.Add((outputId, childId, OpenGLCloneGraphChangeKind.Destroyed));

                _childToParent.Remove(childId);
            }

            // Detach child->parent edge.
            if (_childToParent.Remove(outputId, out var parentId) && _outputs.TryGetValue(parentId, out var parent))
            {
                _ = parent.RemoveClone(outputId);
                cloneGraphChanges.Add((parentId, outputId, OpenGLCloneGraphChangeKind.Destroyed));
            }

            output.Dispose();
            _outputs.Remove(outputId);

            if (ActiveOutputId == outputId)
            {
                ActiveOutputId = _outputs.Keys.FirstOrDefault();
                if (ActiveOutputId == Guid.Empty)
                {
                    ActiveOutputId = null;
                }
            }

        }

        foreach (var change in cloneGraphChanges)
        {
            _diagnostics.PublishCloneGraphChanged(change.ParentId, change.CloneId, change.ChangeKind);
        }

        return MediaResult.Success;
    }

    public int ClearOutputs()
    {
        Guid[] ids;
        lock (_gate)
        {
            ids = _outputs.Keys.ToArray();
        }

        foreach (var id in ids)
        {
            _ = RemoveOutput(id);
        }

        return MediaResult.Success;
    }

    public int SetActiveOutput(Guid outputId)
    {
        lock (_gate)
        {
            if (!_outputs.ContainsKey(outputId))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            ActiveOutputId = outputId;
            return MediaResult.Success;
        }
    }

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        OpenGLVideoOutput? parent;
        OpenGLVideoOutput[] clones;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (!ActiveOutputId.HasValue || !_outputs.TryGetValue(ActiveOutputId.Value, out parent))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            clones = parent.CloneOutputIds
                .Select(id => _outputs.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate is not null)
                .Cast<OpenGLVideoOutput>()
                .ToArray();
        }

        var push = parent.PushFrame(frame, presentationTime);
        if (push != MediaResult.Success)
        {
            return push;
        }

        var committedSurface = parent.Surface;
        foreach (var clone in clones)
        {
            clone.PresentClonedFrame(committedSurface);
        }

        return MediaResult.Success;
    }

    public int CreateCloneOutput(Guid parentOutputId, in OpenGLCloneOptions options, out IVideoOutput? cloneOutput)
    {
        cloneOutput = null;

        var clone = new OpenGLVideoOutput(Guid.NewGuid(), isClone: true);
        var add = AddOutput(clone);
        if (add != MediaResult.Success)
        {
            return add;
        }

        var attach = AttachCloneOutput(parentOutputId, clone.Id);
        if (attach != MediaResult.Success)
        {
            _ = RemoveOutput(clone.Id);
            return attach;
        }

        cloneOutput = clone;
        return MediaResult.Success;
    }

    public int AttachCloneOutput(Guid parentOutputId, Guid cloneOutputId)
    {
        OpenGLVideoOutput parent;
        OpenGLVideoOutput child;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (!_outputs.TryGetValue(parentOutputId, out parent!))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            if (!_outputs.TryGetValue(cloneOutputId, out child!))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            if (PolicyOptions.RejectSelfAttach && parentOutputId == cloneOutputId)
            {
                return (int)MediaErrorCode.OpenGLCloneSelfAttachRejected;
            }

            if (!parent.IsRunning)
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotInitialized;
            }

            if (_childToParent.ContainsKey(cloneOutputId))
            {
                return (int)MediaErrorCode.OpenGLCloneChildAlreadyAttached;
            }

            if (PolicyOptions.RejectCycles && WouldCreateCycle(parentOutputId, cloneOutputId))
            {
                return (int)MediaErrorCode.OpenGLCloneCycleDetected;
            }

            var depthLimit = PolicyOptions.MaxCloneDepth;
            var targetDepth = ComputeDepth(parentOutputId) + 1;
            if (targetDepth > depthLimit)
            {
                return (int)MediaErrorCode.OpenGLCloneMaxDepthExceeded;
            }

            if (parent.Surface.PixelFormat != VideoPixelFormat.Unknown &&
                child.Surface.PixelFormat != VideoPixelFormat.Unknown &&
                parent.Surface.PixelFormat != child.Surface.PixelFormat)
            {
                return (int)MediaErrorCode.OpenGLClonePixelFormatIncompatible;
            }

            var addClone = parent.AddClone(cloneOutputId);
            if (addClone != MediaResult.Success)
            {
                return addClone;
            }

            _childToParent[cloneOutputId] = parentOutputId;
            child.CloneParentOutputId = parentOutputId;
        }

        _diagnostics.PublishCloneGraphChanged(parentOutputId, cloneOutputId, OpenGLCloneGraphChangeKind.Attached);
        return MediaResult.Success;
    }

    public int DetachCloneOutput(Guid parentOutputId, Guid cloneOutputId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            if (!_outputs.TryGetValue(parentOutputId, out var parent))
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            if (!_childToParent.TryGetValue(cloneOutputId, out var attachedParentId) || attachedParentId != parentOutputId)
            {
                return (int)MediaErrorCode.OpenGLCloneNotAttached;
            }

            var removeCode = parent.RemoveClone(cloneOutputId);
            if (removeCode != MediaResult.Success)
            {
                return removeCode;
            }

            _childToParent.Remove(cloneOutputId);
            if (_outputs.TryGetValue(cloneOutputId, out var child))
            {
                child.CloneParentOutputId = null;
            }
        }

        _diagnostics.PublishCloneGraphChanged(parentOutputId, cloneOutputId, OpenGLCloneGraphChangeKind.Detached);
        return MediaResult.Success;
    }

    public void Dispose()
    {
        _ = ClearOutputs();

        lock (_gate)
        {
            _disposed = true;
        }

        _diagnostics.Dispose();
    }

    private bool WouldCreateCycle(Guid parentOutputId, Guid candidateChildId)
    {
        var cursor = parentOutputId;
        while (_childToParent.TryGetValue(cursor, out var upstreamParent))
        {
            if (upstreamParent == candidateChildId)
            {
                return true;
            }

            cursor = upstreamParent;
        }

        return false;
    }

    private int ComputeDepth(Guid outputId)
    {
        var depth = 0;
        var cursor = outputId;
        while (_childToParent.TryGetValue(cursor, out var parent))
        {
            depth++;
            cursor = parent;
        }

        return depth;
    }
}

