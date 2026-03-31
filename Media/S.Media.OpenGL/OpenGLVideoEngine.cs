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
    private readonly Dictionary<Guid, List<Guid>> _childrenByParent = [];
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

    /// <summary>Returns the IDs of all clone outputs whose parent is <paramref name="parentId"/>.</summary>
    public IReadOnlyList<Guid> GetCloneIds(Guid parentId)
    {
        lock (_gate)
        {
            return _childrenByParent.TryGetValue(parentId, out var ids) ? ids.ToArray() : [];
        }
    }

    /// <summary>Returns the parent output ID of <paramref name="outputId"/>, or <see langword="null"/> if not a clone.</summary>
    public Guid? GetParentId(Guid outputId)
    {
        lock (_gate)
        {
            return _childToParent.TryGetValue(outputId, out var p) ? p : null;
        }
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="outputId"/> is currently attached as a clone.</summary>
    public bool IsClone(Guid outputId)
    {
        lock (_gate)
        {
            return _childToParent.ContainsKey(outputId);
        }
    }

    public int AddOutput(OpenGLVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (_outputs.ContainsKey(output.Id))
            {
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
            }

            output.SetDiagnostics(_diagnostics);
            _outputs.Add(output.Id, output);
            ActiveOutputId ??= output.Id;
            return MediaResult.Success;
        }
    }

    public int RemoveOutput(IVideoOutput? output)
    {
        if (output is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return RemoveOutput(output.Id);
    }

    public int RemoveOutput(Guid outputId)
    {
        Guid[] childIds;

        lock (_gate)
        {
            if (!_outputs.ContainsKey(outputId))
            {
                if (_disposed && _outputs.Count == 0)
                    return MediaResult.Success;
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;
            }

            childIds = _childrenByParent.TryGetValue(outputId, out var kids) ? kids.ToArray() : [];
        }

        foreach (var childId in childIds)
            _ = RemoveOutput(childId);

        var cloneGraphChanges = new List<(Guid ParentId, Guid CloneId, OpenGLCloneGraphChangeKind ChangeKind)>();

        lock (_gate)
        {
            if (_disposed && _outputs.Count == 0)
                return MediaResult.Success;

            if (!_outputs.TryGetValue(outputId, out var output))
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;

            // Detach remaining parent->child edges (children removed recursively above).
            if (_childrenByParent.TryGetValue(outputId, out var remaining))
            {
                foreach (var childId in remaining.ToArray())
                {
                    if (_outputs.TryGetValue(childId, out var child))
                        child.SetCloneParent(null);
                    cloneGraphChanges.Add((outputId, childId, OpenGLCloneGraphChangeKind.Destroyed));
                    _childToParent.Remove(childId);
                }
                _childrenByParent.Remove(outputId);
            }

            // Detach child->parent edge.
            if (_childToParent.Remove(outputId, out var parentId) && _outputs.ContainsKey(parentId))
            {
                if (_childrenByParent.TryGetValue(parentId, out var parentKids))
                    parentKids.Remove(outputId);
                cloneGraphChanges.Add((parentId, outputId, OpenGLCloneGraphChangeKind.Destroyed));
            }

            output.Dispose();
            _outputs.Remove(outputId);

            if (ActiveOutputId == outputId)
            {
                ActiveOutputId = _outputs.Keys.FirstOrDefault();
                if (ActiveOutputId == Guid.Empty)
                    ActiveOutputId = null;
            }
        }

        foreach (var change in cloneGraphChanges)
            _diagnostics.PublishCloneGraphChanged(change.ParentId, change.CloneId, change.ChangeKind);

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
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

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
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;

            if (!ActiveOutputId.HasValue || !_outputs.TryGetValue(ActiveOutputId.Value, out parent))
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;

            var cloneIds = _childrenByParent.TryGetValue(ActiveOutputId.Value, out var ids) ? ids : [];
            clones = cloneIds
                .Select(id => _outputs.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(c => c is not null)
                .Cast<OpenGLVideoOutput>()
                .ToArray();
        }

        var push = parent.PushFrame(frame, presentationTime);
        if (push != MediaResult.Success)
            return push;

        var committedSurface = parent.Surface;
        foreach (var clone in clones)
            clone.PresentClonedFrame(committedSurface);

        return MediaResult.Success;
    }

    public int CreateCloneOutput(Guid parentOutputId, in OpenGLCloneOptions options, out IVideoOutput? cloneOutput)
    {
        cloneOutput = null;

        var clone = new OpenGLVideoOutput(Guid.NewGuid());
        var add = AddOutput(clone);
        if (add != MediaResult.Success)
            return add;

        var attach = AttachCloneOutputCore(parentOutputId, clone.Id, options);
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
        return AttachCloneOutputCore(parentOutputId, cloneOutputId, options: null);
    }

    /// <summary>
    /// Attaches <paramref name="cloneOutputId"/> as a clone of <paramref name="parentOutputId"/>
    /// using the supplied <paramref name="options"/> to control depth limits, pixel-format
    /// policy, and HUD mode.
    /// </summary>
    public int AttachCloneOutput(Guid parentOutputId, Guid cloneOutputId, OpenGLCloneOptions options)
    {
        return AttachCloneOutputCore(parentOutputId, cloneOutputId, options);
    }

    private int AttachCloneOutputCore(Guid parentOutputId, Guid cloneOutputId, OpenGLCloneOptions? options)
    {
        OpenGLVideoOutput parent;
        OpenGLVideoOutput child;

        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;

            if (!_outputs.TryGetValue(parentOutputId, out parent!))
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;

            if (!_outputs.TryGetValue(cloneOutputId, out child!))
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;

            if (PolicyOptions.RejectSelfAttach && parentOutputId == cloneOutputId)
                return (int)MediaErrorCode.OpenGLCloneSelfAttachRejected;

            if (!PolicyOptions.AllowAttachWhileRunning && parent.IsRunning)
                return (int)MediaErrorCode.OpenGLCloneAttachFailed;

            if (_childToParent.ContainsKey(cloneOutputId))
                return (int)MediaErrorCode.OpenGLCloneChildAlreadyAttached;

            if (PolicyOptions.RejectCycles && WouldCreateCycle(parentOutputId, cloneOutputId))
                return (int)MediaErrorCode.OpenGLCloneCycleDetected;

            var depthLimit = options?.MaxCloneDepth is > 0 ? options.MaxCloneDepth.Value : PolicyOptions.MaxCloneDepth;
            var targetDepth = ComputeDepth(parentOutputId) + 1;
            if (targetDepth > depthLimit)
                return (int)MediaErrorCode.OpenGLCloneMaxDepthExceeded;

            var pixelPolicy = options?.PixelFormatPolicy ?? PolicyOptions.DefaultPixelFormatPolicy;
            if (pixelPolicy == OpenGLClonePixelFormatPolicy.RequireCompatibleFastPath &&
                parent.Surface.PixelFormat != VideoPixelFormat.Unknown &&
                child.Surface.PixelFormat != VideoPixelFormat.Unknown &&
                parent.Surface.PixelFormat != child.Surface.PixelFormat)
                return (int)MediaErrorCode.OpenGLClonePixelFormatIncompatible;

            // Register in both maps.
            if (!_childrenByParent.TryGetValue(parentOutputId, out var siblings))
                _childrenByParent[parentOutputId] = siblings = [];

            if (siblings.Contains(cloneOutputId))
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;

            siblings.Add(cloneOutputId);
            _childToParent[cloneOutputId] = parentOutputId;
            child.SetCloneParent(parentOutputId);
        }

        _diagnostics.PublishCloneGraphChanged(parentOutputId, cloneOutputId, OpenGLCloneGraphChangeKind.Attached);
        return MediaResult.Success;
    }

    public int DetachCloneOutput(Guid parentOutputId, Guid cloneOutputId)
    {
        lock (_gate)
        {
            if (_disposed)
                return MediaResult.Success;

            if (!_outputs.ContainsKey(parentOutputId))
                return (int)MediaErrorCode.OpenGLCloneParentNotFound;

            if (!_childToParent.TryGetValue(cloneOutputId, out var attachedParentId) || attachedParentId != parentOutputId)
                return (int)MediaErrorCode.OpenGLCloneNotAttached;

            if (_childrenByParent.TryGetValue(parentOutputId, out var kids))
                kids.Remove(cloneOutputId);

            _childToParent.Remove(cloneOutputId);

            if (_outputs.TryGetValue(cloneOutputId, out var child))
                child.SetCloneParent(null);
        }

        _diagnostics.PublishCloneGraphChanged(parentOutputId, cloneOutputId, OpenGLCloneGraphChangeKind.Detached);
        return MediaResult.Success;
    }

    public void Dispose()
    {
        Guid[] ids;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;                   // gate: AddOutput now returns Disposed immediately
            ids = _outputs.Keys.ToArray();
        }

        foreach (var id in ids)
            _ = RemoveOutput(id);

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
