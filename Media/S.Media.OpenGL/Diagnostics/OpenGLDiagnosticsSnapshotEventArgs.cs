namespace S.Media.OpenGL.Diagnostics;

public sealed class OpenGLDiagnosticsSnapshotEventArgs : EventArgs
{
    public OpenGLDiagnosticsSnapshotEventArgs(Guid outputId, OpenGLOutputDiagnostics snapshot)
    {
        OutputId = outputId;
        Snapshot = snapshot;
    }

    public Guid OutputId { get; }

    public OpenGLOutputDiagnostics Snapshot { get; }
}

