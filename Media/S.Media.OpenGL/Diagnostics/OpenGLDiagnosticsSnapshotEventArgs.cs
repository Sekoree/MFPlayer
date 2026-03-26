namespace S.Media.OpenGL.Diagnostics;

public sealed class OpenGLDiagnosticsSnapshotEventArgs : EventArgs
{
    public OpenGLDiagnosticsSnapshotEventArgs(Guid outputId, OpenGLOutputDebugInfo snapshot)
    {
        OutputId = outputId;
        Snapshot = snapshot;
    }

    public Guid OutputId { get; }

    public OpenGLOutputDebugInfo Snapshot { get; }
}

