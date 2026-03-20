using Avalonia.OpenGL;
using Seko.OwnAudioNET.Video.OpenGL;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seko.OwnAudioNET.Video.Avalonia;

public partial class VideoGL
{
    private const int HudPaddingX = 8;
    private const int HudPaddingY = 6;
    private const int HudCharAdvance = 12;
    private const int HudScaleX = 1;
    private const int HudScaleY = 2;
    private const int HudGlyphRows = 5;
    private const int HudLineGap = 4;
    private const int HudMarginX = 8;
    private const int HudMarginY = 8;

    private int _hudProgram;
    private int _hudVao;
    private int _hudVbo;
    private int _hudTextureOverlay;
    private int _hudTextureWidth = 1024;
    private int _hudTextureHeight = 96;
    private bool _hudTextureInitialized;
    private string _lastHudText = string.Empty;
    private string _currentHudText = string.Empty;
    private bool _hudTextDirty = true;
    private byte[]? _hudPixels;
    private int _hudTextureLocation = -1;
    private int _lastHudViewportWidth = -1;
    private int _lastHudViewportHeight = -1;
    private int _lastHudContentWidth = -1;
    private int _lastHudContentHeight = -1;

    private int _hudQueueDepth;
    private double _hudUploadMsPerFrame;
    private double _hudAvDriftMs;
    private bool _hudHardwareDecoding;
    private long _hudDroppedFrames;
    private string _hudPixelFormatInfo = "unknown";
    private double _hudVideoFps;

    public void UpdateFormatInfo(string sourcePixelFormat, string outputPixelFormat, double videoFps)
    {
        var source = sourcePixelFormat ?? string.Empty;
        var output = outputPixelFormat ?? string.Empty;
        var fmt = string.Equals(source, output, StringComparison.OrdinalIgnoreCase)
            ? source
            : $"{source}->{output}";

        lock (_hudLock)
        {
            _hudPixelFormatInfo = fmt;
            _hudVideoFps = videoFps;
            _hudTextDirty = true;
        }
    }

    public void UpdateHudDiagnostics(int queueDepth, double uploadMsPerFrame, double avDriftMs, bool isHardwareDecoding, long droppedFrames)
    {
        lock (_hudLock)
        {
            _hudQueueDepth = queueDepth;
            _hudUploadMsPerFrame = uploadMsPerFrame;
            _hudAvDriftMs = avDriftMs;
            _hudHardwareDecoding = isHardwareDecoding;
            _hudDroppedFrames = droppedFrames;
            _hudTextDirty = true;
        }
    }

    private void InitializeHudRendering(GlInterface gl)
    {
        if (_hudProgram != 0)
            return;

        var vertexShader = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES
            ? """
              #version 300 es
              precision mediump float;
              layout(location = 0) in vec2 aPosition;
              layout(location = 1) in vec2 aTexCoord;
              out vec2 vTexCoord;
              void main()
              {
                  gl_Position = vec4(aPosition, 0.0, 1.0);
                  vTexCoord = aTexCoord;
              }
              """
            : """
              #version 330 core
              layout(location = 0) in vec2 aPosition;
              layout(location = 1) in vec2 aTexCoord;
              out vec2 vTexCoord;
              void main()
              {
                  gl_Position = vec4(aPosition, 0.0, 1.0);
                  vTexCoord = aTexCoord;
              }
              """;

        var fragmentShader = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES
            ? """
              #version 300 es
              precision mediump float;
              in vec2 vTexCoord;
              uniform sampler2D uTexture;
              out vec4 FragColor;
              void main()
              {
                  FragColor = texture(uTexture, vTexCoord);
              }
              """
            : """
              #version 330 core
              in vec2 vTexCoord;
              uniform sampler2D uTexture;
              out vec4 FragColor;
              void main()
              {
                  FragColor = texture(uTexture, vTexCoord);
              }
              """;

        _hudProgram = BuildProgram(gl, vertexShader, fragmentShader);
        if (_hudProgram == 0)
            return;

        if (_getUniformLocation != null)
            _hudTextureLocation = _getUniformLocation(_hudProgram, "uTexture");

        _hudVao = gl.GenVertexArray();
        _hudVbo = gl.GenBuffer();

        gl.BindVertexArray(_hudVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _hudVbo);

        float[] quadVertices =
        [
            -1.0f, 1.0f, 0.0f, 1.0f,
            -0.5f, 1.0f, 1.0f, 1.0f,
            -0.5f, 0.9f, 1.0f, 0.0f,
            -1.0f, 0.9f, 0.0f, 0.0f
        ];

        var handle = GCHandle.Alloc(quadVertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(GlConsts.GL_ARRAY_BUFFER, quadVertices.Length * sizeof(float), handle.AddrOfPinnedObject(), GlConsts.GL_STATIC_DRAW);
        }
        finally
        {
            handle.Free();
        }

        var stride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, stride, nint.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, stride, 2 * sizeof(float));

        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        gl.BindVertexArray(0);

        _hudTextureOverlay = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _hudTextureOverlay);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, VideoGlConstants.Nearest);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, VideoGlConstants.Nearest);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, VideoGlConstants.TextureWrapS, VideoGlConstants.ClampToEdge);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, VideoGlConstants.TextureWrapT, VideoGlConstants.ClampToEdge);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, VideoGlConstants.Rgba8, _hudTextureWidth, _hudTextureHeight, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, nint.Zero);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        _hudPixels = new byte[_hudTextureWidth * _hudTextureHeight * 4];
        _hudTextureInitialized = true;
    }

    private void ReleaseHudResources(GlInterface gl)
    {
        if (_hudProgram != 0)
        {
            gl.DeleteProgram(_hudProgram);
            _hudProgram = 0;
        }

        if (_hudVao != 0)
        {
            gl.DeleteVertexArray(_hudVao);
            _hudVao = 0;
        }

        if (_hudVbo != 0)
        {
            gl.DeleteBuffer(_hudVbo);
            _hudVbo = 0;
        }

        if (_hudTextureOverlay != 0)
        {
            gl.DeleteTexture(_hudTextureOverlay);
            _hudTextureOverlay = 0;
        }

        _hudTextureInitialized = false;
        _hudPixels = null;
        _hudTextDirty = true;
        _lastHudText = string.Empty;
        _currentHudText = string.Empty;
    }

    private void RenderHudOverlay(GlInterface gl, int surfaceWidth, int surfaceHeight)
    {
        if (!EnableHudOverlay || !_hudTextureInitialized || _hudProgram == 0 || _hudVao == 0)
            return;

        string hudText;
        lock (_hudLock)
        {
            if (_hudTextDirty)
            {
                _currentHudText = BuildHudText();
                _hudTextDirty = false;
            }

            hudText = _currentHudText;
        }

        MeasureHudText(hudText, out var contentWidthPx, out var contentHeightPx);
        UpdateHudGeometry(gl, surfaceWidth, surfaceHeight, contentWidthPx, contentHeightPx);

        if (!string.Equals(hudText, _lastHudText, StringComparison.Ordinal))
        {
            _lastHudText = hudText;
            UpdateHudTexture(gl, hudText);
        }

        gl.UseProgram(_hudProgram);
        if (_uniform1i != null && _hudTextureLocation >= 0)
            _uniform1i(_hudTextureLocation, 0);

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _hudTextureOverlay);

        _glEnable?.Invoke(VideoGlConstants.Blend);
        _glBlendFunc?.Invoke(VideoGlConstants.SrcAlpha, VideoGlConstants.OneMinusSrcAlpha);

        gl.BindVertexArray(_hudVao);
        gl.DrawArrays(VideoGlConstants.TriangleFan, 0, 4);
        gl.BindVertexArray(0);

        _glDisable?.Invoke(VideoGlConstants.Blend);
        gl.UseProgram(0);
    }

    private void UpdateRenderFpsSample()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var lastTicks = Interlocked.Read(ref _lastRenderFpsSampleTicks);
        if (lastTicks == 0)
        {
            Interlocked.Exchange(ref _lastRenderFpsSampleTicks, nowTicks);
            Interlocked.Exchange(ref _lastRenderCountSample, Interlocked.Read(ref _diagRenderCount));
            return;
        }

        var elapsed = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 0.5)
            return;

        var renderCount = Interlocked.Read(ref _diagRenderCount);
        var lastRenderCount = Interlocked.Read(ref _lastRenderCountSample);
        var delta = renderCount - lastRenderCount;
        if (delta >= 0)
            Interlocked.Exchange(ref _renderFps, delta / elapsed);

        Interlocked.Exchange(ref _lastRenderCountSample, renderCount);
        Interlocked.Exchange(ref _lastRenderFpsSampleTicks, nowTicks);
        lock (_hudLock)
            _hudTextDirty = true;
    }

    private void UpdateHudTexture(GlInterface gl, string text)
    {
        if (_hudPixels == null || _hudPixels.Length != _hudTextureWidth * _hudTextureHeight * 4)
            _hudPixels = new byte[_hudTextureWidth * _hudTextureHeight * 4];

        RenderTextToBuffer(text, _hudPixels, _hudTextureWidth, _hudTextureHeight);

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _hudTextureOverlay);
        if (_texSubImage2D != null)
        {
            unsafe
            {
                fixed (byte* ptr = _hudPixels)
                    _texSubImage2D(GlConsts.GL_TEXTURE_2D, 0, 0, 0, _hudTextureWidth, _hudTextureHeight, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
            }
        }
        else
        {
            unsafe
            {
                fixed (byte* ptr = _hudPixels)
                    gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, VideoGlConstants.Rgba8, _hudTextureWidth, _hudTextureHeight, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
            }
        }

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);
    }

    private void UpdateHudGeometry(GlInterface gl, int viewportWidth, int viewportHeight, int contentWidthPx, int contentHeightPx)
    {
        if (viewportWidth == _lastHudViewportWidth &&
            viewportHeight == _lastHudViewportHeight &&
            contentWidthPx == _lastHudContentWidth &&
            contentHeightPx == _lastHudContentHeight)
        {
            return;
        }

        _lastHudViewportWidth = viewportWidth;
        _lastHudViewportHeight = viewportHeight;
        _lastHudContentWidth = contentWidthPx;
        _lastHudContentHeight = contentHeightPx;

        var visibleWidthPx = Math.Clamp(contentWidthPx, 1, Math.Max(1, viewportWidth - HudMarginX * 2));
        var visibleHeightPx = Math.Clamp(contentHeightPx, 1, Math.Max(1, viewportHeight - HudMarginY * 2));

        var left = -1.0f + 2.0f * HudMarginX / viewportWidth;
        var right = -1.0f + 2.0f * (HudMarginX + visibleWidthPx) / viewportWidth;
        var top = 1.0f - 2.0f * HudMarginY / viewportHeight;
        var bottom = 1.0f - 2.0f * (HudMarginY + visibleHeightPx) / viewportHeight;

        var uRight = visibleWidthPx / (float)_hudTextureWidth;
        var vBottom = 1.0f - visibleHeightPx / (float)_hudTextureHeight;

        float[] quadVertices =
        [
            left, top, 0.0f, 1.0f,
            right, top, uRight, 1.0f,
            right, bottom, uRight, vBottom,
            left, bottom, 0.0f, vBottom
        ];

        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _hudVbo);
        var handle = GCHandle.Alloc(quadVertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(GlConsts.GL_ARRAY_BUFFER, quadVertices.Length * sizeof(float), handle.AddrOfPinnedObject(), GlConsts.GL_STATIC_DRAW);
        }
        finally
        {
            handle.Free();
        }

        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
    }

    private string BuildHudText()
    {
        var fmt = NormalizeHudPixelFormat(_hudPixelFormatInfo);

        var gpu = _hudHardwareDecoding ? 1 : 0;
        return $"R:{RenderFps:F1} V:{_hudVideoFps:F1} {fmt}\nQ:{_hudQueueDepth} U:{_hudUploadMsPerFrame:F2} AV:{_hudAvDriftMs:F1} GPU:{gpu} D:{_hudDroppedFrames}";
    }

    private static string NormalizeHudPixelFormat(string value)
    {
        return value
            .ToUpperInvariant()
            .Replace("->", "/", StringComparison.Ordinal)
            .Replace("→", "/", StringComparison.Ordinal);
    }

    private static void MeasureHudText(string text, out int contentWidthPx, out int contentHeightPx)
    {
        var lines = text.Split('\n');
        var maxLineLength = 0;
        foreach (var line in lines)
            maxLineLength = Math.Max(maxLineLength, line.Length);

        var glyphHeight = HudGlyphRows * HudScaleY;
        contentWidthPx = HudPaddingX * 2 + maxLineLength * HudCharAdvance;
        contentHeightPx = HudPaddingY * 2 + lines.Length * glyphHeight + Math.Max(0, lines.Length - 1) * HudLineGap;
    }

    private static void RenderTextToBuffer(string text, byte[] pixels, int width, int height)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 180;
        }

        var lines = text.ToUpperInvariant().Split('\n');
        var glyphHeight = HudGlyphRows * HudScaleY;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var x = HudPaddingX;
            var y = HudPaddingY + lineIndex * (glyphHeight + HudLineGap);

            foreach (var ch in line)
            {
                if (ch == ' ')
                {
                    x += HudCharAdvance;
                    continue;
                }

                if (x + HudCharAdvance > width)
                    break;

                DrawCharacter(pixels, width, height, ch, x, y);
                x += HudCharAdvance;
            }
        }
    }

    private static void DrawCharacter(byte[] pixels, int width, int height, char ch, int x, int y)
    {
        var patterns = GetCharacterPattern(ch);
        if (patterns == null)
            return;

        for (var row = 0; row < patterns.Length; row++)
        {
            var pattern = patterns[row];
            for (var col = 0; col < 8; col++)
            {
                if ((pattern & (1 << (7 - col))) == 0)
                    continue;

                for (var sy = 0; sy < HudScaleY; sy++)
                {
                    for (var sx = 0; sx < HudScaleX; sx++)
                    {
                        var px = x + col * HudScaleX + sx;
                        var py = y + row * HudScaleY + sy;
                        if (px < 0 || px >= width || py < 0 || py >= height)
                            continue;

                        var flippedPy = height - 1 - py;
                        var idx = (flippedPy * width + px) * 4;
                        pixels[idx] = 0;
                        pixels[idx + 1] = 220;
                        pixels[idx + 2] = 0;
                        pixels[idx + 3] = 255;
                    }
                }
            }
        }
    }

    private static byte[]? GetCharacterPattern(char ch)
    {
        return ch switch
        {
            '0' => [0x7C, 0x82, 0x82, 0x82, 0x7C],
            '1' => [0x30, 0x30, 0x30, 0x30, 0x78],
            '2' => [0x7C, 0x02, 0x7C, 0x80, 0xFE],
            '3' => [0xFE, 0x02, 0x7C, 0x02, 0xFE],
            '4' => [0x82, 0x82, 0xFE, 0x02, 0x02],
            '5' => [0xFE, 0x80, 0xFE, 0x02, 0xFE],
            '6' => [0x7C, 0x80, 0xFE, 0x82, 0x7C],
            '7' => [0xFE, 0x02, 0x04, 0x08, 0x10],
            '8' => [0x7C, 0x82, 0x7C, 0x82, 0x7C],
            '9' => [0x7C, 0x82, 0x7E, 0x02, 0x7C],
            'A' => [0x38, 0x44, 0x82, 0xFE, 0x82],
            'B' => [0xFC, 0x82, 0xFC, 0x82, 0xFC],
            'C' => [0x7C, 0x80, 0x80, 0x80, 0x7C],
            'D' => [0xFC, 0x82, 0x82, 0x82, 0xFC],
            'E' => [0xFE, 0x80, 0xFE, 0x80, 0xFE],
            'F' => [0xFE, 0x80, 0xFE, 0x80, 0x80],
            'G' => [0x7C, 0x80, 0x8E, 0x82, 0x7C],
            'I' => [0x7C, 0x10, 0x10, 0x10, 0x7C],
            'L' => [0x80, 0x80, 0x80, 0x80, 0xFE],
            'N' => [0x82, 0xC2, 0xA2, 0x92, 0x8A],
            'O' => [0x7C, 0x82, 0x82, 0x82, 0x7C],
            'P' => [0xFC, 0x82, 0xFC, 0x80, 0x80],
            'Q' => [0x7C, 0x82, 0x82, 0x8A, 0x7E],
            'R' => [0xFC, 0x82, 0xFC, 0x84, 0x82],
            'S' => [0x7C, 0x80, 0x7C, 0x02, 0x7C],
            'U' => [0x82, 0x82, 0x82, 0x82, 0x7C],
            'V' => [0x82, 0x82, 0x82, 0x44, 0x38],
            ':' => [0x00, 0x30, 0x00, 0x30, 0x00],
            '.' => [0x00, 0x00, 0x00, 0x00, 0x30],
            '-' => [0x00, 0x00, 0x7E, 0x00, 0x00],
            '/' => [0x02, 0x04, 0x08, 0x10, 0x20],
            _ => null
        };
    }
}

