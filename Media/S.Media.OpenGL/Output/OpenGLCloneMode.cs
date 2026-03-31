namespace S.Media.OpenGL.Output;

/// <summary>
/// Specifies the rendering strategy used when a clone output presents a frame
/// that originated at a parent output.
/// </summary>
/// <remarks>
/// <b>Current implementation status:</b><br/>
/// Only <see cref="CopyFallback"/> is active. <see cref="SharedTexture"/> and
/// <see cref="SharedFboBlit"/> are reserved for a future shared-GL-context path
/// (see Issue B2 in <c>S.Media.OpenGL.md</c>). Setting any value other than
/// <see cref="CopyFallback"/> is accepted without error but behaves identically
/// to <see cref="CopyFallback"/> at runtime.
/// </remarks>
public enum OpenGLCloneMode
{
    /// <summary>
    /// The parent's decoded frame data is copied to the clone's texture.
    /// This is the only actively implemented path.
    /// </summary>
    CopyFallback = 2,

    /// <summary>
    /// Share the parent's GL texture handle with the clone context.
    /// Requires a shared GL context — <b>not yet implemented</b>.
    /// </summary>
    [Obsolete("SharedTexture is not yet implemented. CopyFallback is the only active clone path. See Issue B2 in S.Media.OpenGL.md.")]
    SharedTexture = 0,

    /// <summary>
    /// Blit the parent's framebuffer object into the clone's FBO.
    /// Requires a shared GL context — <b>not yet implemented</b>.
    /// </summary>
    [Obsolete("SharedFboBlit is not yet implemented. CopyFallback is the only active clone path. See Issue B2 in S.Media.OpenGL.md.")]
    SharedFboBlit = 1,
}
