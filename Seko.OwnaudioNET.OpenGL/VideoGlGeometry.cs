namespace Seko.OwnaudioNET.OpenGL;

/// <summary>
/// Shared geometry data and texture-state tracking for the full-screen video quad.
/// </summary>
public static class VideoGlGeometry
{
    /// <summary>
    /// Six vertices covering the full NDC clip space.
    /// Each vertex is (posX, posY, texU, texV), forming two triangles
    /// that together make a full-screen quad.
    /// </summary>
    public static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f
    ];
}

/// <summary>
/// Tracks the last allocated parameters of an OpenGL 2D texture so that
/// <c>glTexImage2D</c> is only called when dimensions or format change,
/// and <c>glTexSubImage2D</c> is used for fast in-place updates thereafter.
/// </summary>
public struct TextureUploadState
{
    public bool IsInitialized;
    public int  Width, Height, InternalFormat, Format, Type;
}

