using S.Media.Core.Errors;
using S.Media.Core.Video;
using SDL3;

namespace S.Media.OpenGL.SDL3;

// Partial: frame rendering, texture upload, viewport, and platform event pumping.
public sealed partial class SDL3VideoView
{
    private int PresentStandaloneFrameLocked(VideoFrame frame)
    {
        if (_windowHandle == nint.Zero || _glContextHandle == nint.Zero)
        {
            return (int)MediaErrorCode.SDL3EmbedHandleUnavailable;
        }

        if (!SDL.GLMakeCurrent(_windowHandle, _glContextHandle))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        PumpSdlEvents();

        var ensureGl = EnsureGlResourcesLocked();
        if (ensureGl != MediaResult.Success)
        {
            return ensureGl;
        }

        if (SDL.GetWindowSizeInPixels(_windowHandle, out var pixelWidth, out var pixelHeight))
        {
            ApplyViewportForFrame(frame.Width, frame.Height, pixelWidth, pixelHeight);
        }

        int renderCode;
        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
        {
            renderCode = RenderRgbaFrameLocked(frame);
        }
        else
        {
            renderCode = RenderYuvFrameLocked(frame);
        }

        if (renderCode != MediaResult.Success)
        {
            return renderCode;
        }

        if (!SDL.GLSwapWindow(_windowHandle))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        return MediaResult.Success;
    }

    private int RenderRgbaFrameLocked(VideoFrame frame)
    {
        if (!_uploader.Upload(frame))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }


        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(Gl.ColorBufferBit);
        _glUseProgram!(_glProgram);
        _glActiveTexture!(Gl.Texture0);
        _glBindTexture!(Gl.TextureTarget2D, _glTexture);
        _glBindVertexArray!(_glVao);
        _glDrawArrays!(Gl.Triangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        return MediaResult.Success;
    }

    private int RenderYuvFrameLocked(VideoFrame frame)
    {
        if (!TryBuildYuvPlan(frame, out var plan))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (!_uploader.Upload(frame))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }


        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(Gl.ColorBufferBit);
        _glUseProgram!(_glYuvProgram);
        if (_glYuvPixelFormatLocation >= 0)
        {
            _glUniform1I!(_glYuvPixelFormatLocation, plan.ModeId);
        }
        if (_glYuvFullRangeLocation >= 0)
        {
            _glUniform1I!(_glYuvFullRangeLocation, frame.IsFullRange ? 1 : 0); // B6
        }

        _glActiveTexture!(Gl.Texture0);
        _glBindTexture!(Gl.TextureTarget2D, _glTextureY);
        _glActiveTexture!(Gl.Texture1);
        _glBindTexture!(Gl.TextureTarget2D, _glTextureU);
        _glActiveTexture!(Gl.Texture2);
        _glBindTexture!(Gl.TextureTarget2D, plan.IsSemiPlanar ? _glTextureU : _glTextureV);

        _glBindVertexArray!(_glVao);
        _glDrawArrays!(Gl.Triangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        return MediaResult.Success;
    }


    // N6: YuvPlan record and TryBuildYuvPlan moved to YuvPlan.cs (shared with SDL3ShaderPipeline).
    private static bool TryBuildYuvPlan(VideoFrame frame, out YuvPlan plan)
        => YuvPlan.TryBuild(frame, out plan);

    /// <summary>
    /// Pumps pending SDL events. On macOS, this <b>must</b> be called from the main/UI thread
    /// because SDL requires event pumping on the thread that created the application.
    /// On Linux (X11/Wayland), events are pumped automatically by the render loop.
    /// </summary>
    public void PumpPlatformEvents()
    {
        PumpSdlEvents();
    }

    private static void PumpSdlEvents()
    {
        while (SDL.PollEvent(out _))
        {
        }
    }

    private void ApplyViewportForFrame(int frameWidth, int frameHeight, int windowWidth, int windowHeight)
    {
        var safeWindowWidth = Math.Max(1, windowWidth);
        var safeWindowHeight = Math.Max(1, windowHeight);
        if (!_preserveAspectRatio || frameWidth <= 0 || frameHeight <= 0)
        {
            _glViewport!(0, 0, safeWindowWidth, safeWindowHeight);
            return;
        }

        var sourceAspect = (double)frameWidth / frameHeight;
        var targetAspect = (double)safeWindowWidth / safeWindowHeight;

        int viewportWidth;
        int viewportHeight;
        if (targetAspect > sourceAspect)
        {
            viewportHeight = safeWindowHeight;
            viewportWidth = Math.Max(1, (int)Math.Round(viewportHeight * sourceAspect));
        }
        else
        {
            viewportWidth = safeWindowWidth;
            viewportHeight = Math.Max(1, (int)Math.Round(viewportWidth / sourceAspect));
        }

        var viewportX = (safeWindowWidth - viewportWidth) / 2;
        var viewportY = (safeWindowHeight - viewportHeight) / 2;
        _glViewport!(viewportX, viewportY, viewportWidth, viewportHeight);
    }
}

