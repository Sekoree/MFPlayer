namespace S.Media.OpenGL.Diagnostics;

public sealed class OpenGLCloneGraphChangedEventArgs : EventArgs
{
    public OpenGLCloneGraphChangedEventArgs(Guid parentOutputId, Guid cloneOutputId, OpenGLCloneGraphChangeKind changeKind)
    {
        ParentOutputId = parentOutputId;
        CloneOutputId = cloneOutputId;
        ChangeKind = changeKind;
    }

    public Guid ParentOutputId { get; }

    public Guid CloneOutputId { get; }

    public OpenGLCloneGraphChangeKind ChangeKind { get; }
}

