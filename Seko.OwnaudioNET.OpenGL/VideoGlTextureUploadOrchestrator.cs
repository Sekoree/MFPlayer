using System.Runtime.InteropServices;

namespace Seko.OwnaudioNET.OpenGL;

/// <summary>
/// Shared texture upload orchestration for video frame planes.
/// Handles texture reallocation tracking and chooses between
/// <c>TexSubImage2D</c> and full <c>TexImage2D</c> updates.
/// </summary>
public static class VideoGlTextureUploadOrchestrator
{
    /// <summary>Backend callback for <c>glTexImage2D</c>.</summary>
    public delegate void TexImage2DCallback(
        int internalFormat,
        int width,
        int height,
        int format,
        int type,
        nint pixels);

    /// <summary>Backend callback for <c>glTexSubImage2D</c>.</summary>
    public delegate void TexSubImage2DCallback(
        int width,
        int height,
        int format,
        int type,
        nint pixels);

    /// <summary>
    /// Uploads packed pixel data into the currently bound GL texture.
    /// Reallocates storage only when dimensions or format changed.
    /// </summary>
    public static unsafe void UploadTexture2D(
        ref TextureUploadState state,
        int width,
        int height,
        int internalFormat,
        int format,
        int type,
        byte[] data,
        TexImage2DCallback texImage2D,
        TexSubImage2DCallback? texSubImage2D)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            var pixels = (nint)ptr;
            var reallocate = !state.IsInitialized
                             || state.Width != width
                             || state.Height != height
                             || state.InternalFormat != internalFormat
                             || state.Format != format
                             || state.Type != type;

            if (reallocate)
            {
                texImage2D(internalFormat, width, height, format, type, nint.Zero);
                state = new TextureUploadState
                {
                    IsInitialized = true,
                    Width = width,
                    Height = height,
                    InternalFormat = internalFormat,
                    Format = format,
                    Type = type
                };
            }

            if (texSubImage2D != null)
                texSubImage2D(width, height, format, type, pixels);
            else
                texImage2D(internalFormat, width, height, format, type, pixels);
        }
    }
}

