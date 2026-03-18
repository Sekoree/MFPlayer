using System.Diagnostics;
using Seko.OwnAudioNET.Video.OpenGL;

namespace Seko.OwnAudioNET.Video.SDL3;

public sealed partial class VideoSDL
{
    private void UpdateRenderFps()
    {
        Interlocked.Increment(ref _renderedFrameCount);
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _lastHudUpdateTime;

        // Refresh once per second using Stopwatch ticks (not TimeSpan ticks).
        if (elapsed >= Stopwatch.Frequency)
        {
            var frames = Interlocked.Read(ref _renderedFrameCount);
            _currentRenderFps = frames * Stopwatch.Frequency / (double)elapsed;
            Interlocked.Exchange(ref _renderedFrameCount, 0);
            _lastHudUpdateTime = now;
            _hudTextDirty = true;
        }
    }

    /// <summary>Returns the shared OpenGL 3.3 core vertex shader source.</summary>
    private static string BuildVertexShader() => VideoGlShaders.VertexShaderCore;

    /// <summary>Returns the shared OpenGL 3.3 core RGBA fragment shader source.</summary>
    private static string BuildFragmentShader() => VideoGlShaders.FragmentShaderCore;

    /// <summary>Returns the shared OpenGL 3.3 core YUV → RGB fragment shader source.</summary>
    private static string BuildYuvFragmentShader() => VideoGlShaders.YuvFragmentShaderCore;
}
