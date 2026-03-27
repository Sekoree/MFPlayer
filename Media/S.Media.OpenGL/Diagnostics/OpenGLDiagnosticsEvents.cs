using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Diagnostics;

public sealed class OpenGLDiagnosticsEvents : IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;

    public event EventHandler<OpenGLSurfaceMetadata>? SurfaceChanged;

    public event EventHandler<OpenGLCloneGraphChangedEventArgs>? CloneGraphChanged;

    public event EventHandler<OpenGLDiagnosticsSnapshotEventArgs>? DiagnosticsUpdated;

    public void PublishSurfaceChanged(OpenGLSurfaceMetadata surface)
    {
        EventHandler<OpenGLSurfaceMetadata>? handler;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handler = SurfaceChanged;
        }

        handler?.Invoke(this, surface);
    }

    public void PublishCloneGraphChanged(Guid parentOutputId, Guid cloneOutputId, OpenGLCloneGraphChangeKind changeKind)
    {
        EventHandler<OpenGLCloneGraphChangedEventArgs>? handler;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handler = CloneGraphChanged;
        }

        handler?.Invoke(this, new OpenGLCloneGraphChangedEventArgs(parentOutputId, cloneOutputId, changeKind));
    }

    public void PublishDiagnosticsUpdated(Guid outputId, OpenGLOutputDebugInfo snapshot)
    {
        EventHandler<OpenGLDiagnosticsSnapshotEventArgs>? handler;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handler = DiagnosticsUpdated;
        }

        handler?.Invoke(this, new OpenGLDiagnosticsSnapshotEventArgs(outputId, snapshot));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SurfaceChanged = null;
            CloneGraphChanged = null;
            DiagnosticsUpdated = null;
        }
    }
}
