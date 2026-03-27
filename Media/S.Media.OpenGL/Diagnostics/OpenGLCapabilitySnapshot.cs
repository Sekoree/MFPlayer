namespace S.Media.OpenGL.Diagnostics;

public readonly record struct OpenGLCapabilitySnapshot(
    bool SupportsTextureSharing,
    bool SupportsFboBlit,
    int MaxTextureSize,
    bool SupportsPersistentMappedBuffers);
