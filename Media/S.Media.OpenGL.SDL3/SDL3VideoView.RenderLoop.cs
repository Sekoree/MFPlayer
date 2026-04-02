using S.Media.Core.Errors;
using S.Media.Core.Video;
using SDL3;

namespace S.Media.OpenGL.SDL3;

// Partial: background render thread loop, frame dispatch, and swap-chain presentation.
public sealed partial class SDL3VideoView
{
    private void RenderLoop()
    {
        // Capture the handles once; they are immutable while the render thread lives
        // (Stop() joins before DestroyStandaloneWindowLocked() can run).
        nint wnd, ctx;
        lock (_gate) { wnd = _windowHandle; ctx = _glContextHandle; }

        if (wnd == nint.Zero || ctx == nint.Zero) return;

        if (!SDL.GLMakeCurrent(wnd, ctx))
        {
            Console.Error.WriteLine("[SDL3VideoView] RenderLoop: GLMakeCurrent failed – " + SDL.GetError());
            return;
        }

        // Apply hardware VSync based on the output config set at Start().
        // VSync mode → SwapInterval(1); all other modes → SwapInterval(0) so the
        // software timing in OpenGLVideoOutput is the sole pacing mechanism.
        var useHwVSync = _renderConfig.PresentationMode == VideoOutputPresentationMode.VSync;
        _ = SDL.GLSetSwapInterval(useHwVSync ? 1 : 0);

        // Initialise GL resources and the shader pipeline on this thread.
        lock (_gate)
        {
            var glInit = EnsureGlResourcesLocked();
            if (glInit != MediaResult.Success)
            {
                SDL.GLMakeCurrent(wnd, nint.Zero);
                return;
            }

            if (!_pipelineReady)
            {
                var pipeInit = _shaderPipeline.EnsureInitialized();
                if (pipeInit == MediaResult.Success)
                    _pipelineReady = true;
                // Non-fatal if pipeline fails – standalone rendering still works.
            }
        }

        while (!_renderStopRequested)
        {
            // SDL events must be pumped on the main thread on macOS.
            // On Linux (X11/Wayland) pumping on the render thread is safe.
            if (!OperatingSystem.IsMacOS())
                PumpSdlEvents();

            (VideoFrame Frame, TimeSpan Pts) item;
            bool hasItem;
            lock (_renderQueueLock) { hasItem = _renderQueue.TryDequeue(out item); }

            if (!hasItem)
            {
                _renderQueueReady.Wait(TimeSpan.FromMilliseconds(2));
                _renderQueueReady.Reset();
                continue;
            }

            using (item.Frame)
            {
                RenderFrameOnRenderThread(item.Frame, item.Pts, wnd);
            }
        }

        // Release the GL context before returning so it can be destroyed on the main thread.
        SDL.GLMakeCurrent(wnd, nint.Zero);
    }

    private void RenderFrameOnRenderThread(VideoFrame frame, TimeSpan pts, nint wnd)
    {
        var push = Output.PushFrame(frame, pts);
        if (push != MediaResult.Success) return;

        var generation = Output.LastPresentedFrameGeneration;
        if (generation == _lastPresentedGeneration) return;
        _lastPresentedGeneration = generation;

        if (SDL.GetWindowSizeInPixels(wnd, out var pw, out var ph))
            ApplyViewportForFrame(frame.Width, frame.Height, pw, ph);

        var uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        int renderCode;
        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
            renderCode = RenderRgbaFrameLocked(frame);
        else
            renderCode = RenderYuvFrameLocked(frame);
        var uploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

        if (renderCode != MediaResult.Success) return;

        var swapStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!SDL.GLSwapWindow(wnd))
        {
            Console.Error.WriteLine("[SDL3VideoView] GLSwapWindow failed – " + SDL.GetError());
            return;
        }
        var presentMs = System.Diagnostics.Stopwatch.GetElapsedTime(swapStart).TotalMilliseconds;

        Output.UpdateTimings(uploadMs, presentMs);
    }
}

