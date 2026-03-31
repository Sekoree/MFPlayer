namespace S.Media.OpenGL.Diagnostics;

public sealed class OpenGLDiagnosticsSnapshotEventArgs : EventArgs
{
    public OpenGLDiagnosticsSnapshotEventArgs(Guid outputId, VideoOutputDiagnosticsSnapshot snapshot)
    {
        OutputId = outputId;
        Snapshot = snapshot;
    }

    public Guid OutputId { get; }

    /// <summary>Unified diagnostics snapshot for this output.</summary>
    public VideoOutputDiagnosticsSnapshot Snapshot { get; }
}
