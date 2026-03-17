using System.Runtime.InteropServices;
using SDL3;

namespace AudioEx;

internal sealed partial class SdlVideoGlRenderer
{
    private const int HudPaddingX = 12;
    private const int HudPaddingY = 8;
    private const int HudCharAdvance = 18;
    private const int HudScaleX = 2;
    private const int HudScaleY = 5;
    private const int HudGlyphRows = 5;
    private const int HudLineGap = 8;
    private const int HudMarginX = 10;
    private const int HudMarginY = 10;

    private int _hudProgram;
    private int _hudVao;
    private int _hudVbo;
    private int _hudTextureOverlay;
    private int _hudTextureWidth = 1024;
    private int _hudTextureHeight = 128;
    private bool _hudTextureInitialized;
    private string _lastHudText = "";
    private string _currentHudText = "";
    private bool _hudTextDirty = true;
    private byte[]? _hudPixels;
    private int _hudProjectionLocation = -1;
    private int _hudTextureLocation = -1;
    private int _lastHudViewportWidth = -1;
    private int _lastHudViewportHeight = -1;
    private int _lastHudContentWidth = -1;
    private int _lastHudContentHeight = -1;
    private static readonly float[] IdentityMatrix4x4 =
    [
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f
    ];

    /// <summary>Enable/disable the on-screen HUD overlay.</summary>
    public bool EnableHudOverlay { get; set; } = true;

    internal void InitializeHudRendering()
    {
        if (_hudProgram != 0)
            return;

        // Create simple ortho program for HUD text
        var vertexShader = """
            #version 330 core
            layout(location = 0) in vec2 aPosition;
            layout(location = 1) in vec2 aTexCoord;
            out vec2 vTexCoord;
            uniform mat4 projection;
            void main()
            {
                gl_Position = projection * vec4(aPosition, 0.0, 1.0);
                vTexCoord = aTexCoord;
            }
            """;

        var fragmentShader = """
            #version 330 core
            in vec2 vTexCoord;
            uniform sampler2D uTexture;
            out vec4 FragColor;
            void main()
            {
                FragColor = texture(uTexture, vTexCoord);
            }
            """;

        var vertex = CompileShader(GlVertexShader, vertexShader, out _);
        var fragment = CompileShader(GlFragmentShader, fragmentShader, out _);

        _hudProgram = _glCreateProgram!();
        _glAttachShader!(_hudProgram, vertex);
        _glAttachShader!(_hudProgram, fragment);
        _glBindAttribLocation!(_hudProgram, 0, "aPosition");
        _glBindAttribLocation!(_hudProgram, 1, "aTexCoord");
        _glLinkProgram!(_hudProgram);

        _glDeleteShader!(vertex);
        _glDeleteShader!(fragment);

        _hudProjectionLocation = _glGetUniformLocation!(_hudProgram, "projection");
        _hudTextureLocation = _glGetUniformLocation!(_hudProgram, "uTexture");

        // Create VAO/VBO for a full-screen quad in NDC space
        _glGenVertexArrays!(1, out _hudVao);
        _glGenBuffers!(1, out _hudVbo);

        _glBindVertexArray!(_hudVao);
        _glBindBuffer!(GlArrayBuffer, _hudVbo);

        // Start with a minimal quad; actual size is updated dynamically from the
        // current HUD text in RenderHudOverlay.
        float[] quadVertices =
        [
            // Position XY (NDC), TexCoord UV
            -1.0f,  1.0f, 0.0f, 1.0f,
            -0.5f,  1.0f, 1.0f, 1.0f,
            -0.5f,  0.9f, 1.0f, 0.0f,
            -1.0f,  0.9f, 0.0f, 0.0f
        ];

        var handle = GCHandle.Alloc(quadVertices, GCHandleType.Pinned);
        try
        {
            _glBufferData!(GlArrayBuffer, quadVertices.Length * sizeof(float), handle.AddrOfPinnedObject(), GlStaticDraw);
        }
        finally
        {
            handle.Free();
        }

        var stride = 4 * sizeof(float);
        _glEnableVertexAttribArray!(0);
        _glVertexAttribPointer!(0, 2, GlFloat, 0, stride, nint.Zero);
        _glEnableVertexAttribArray!(1);
        _glVertexAttribPointer!(1, 2, GlFloat, 0, stride, 2 * sizeof(float));

        _glBindBuffer!(GlArrayBuffer, 0);
        _glBindVertexArray!(0);

        // Create overlay texture
        _glGenTextures!(1, out _hudTextureOverlay);
        _glBindTexture!(GlTexture2D, _hudTextureOverlay);
        _glTexParameteri!(GlTexture2D, GlTextureMinFilter, GlNearest);
        _glTexParameteri!(GlTexture2D, GlTextureMagFilter, GlNearest);
        _glTexParameteri!(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        _glTexParameteri!(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        _glTexImage2D!(GlTexture2D, 0, GlRgba8, _hudTextureWidth, _hudTextureHeight, 0, GlRgba, GlUnsignedByte, nint.Zero);
        _glBindTexture!(GlTexture2D, 0);

        _hudPixels = new byte[_hudTextureWidth * _hudTextureHeight * 4];

        _hudTextureInitialized = true;
    }

    internal void RenderHudOverlay(int surfaceWidth, int surfaceHeight)
    {
        if (!EnableHudOverlay || !_hudTextureInitialized || _hudProgram == 0)
            return;

        if (_hudTextDirty)
        {
            _currentHudText = BuildHudText();
            _hudTextDirty = false;
        }

        var hudText = _currentHudText;

        var viewport = GetAspectFitRect(surfaceWidth, surfaceHeight, _textureWidth, _textureHeight);
        var viewportWidth = Math.Max(1, viewport.Width);
        var viewportHeight = Math.Max(1, viewport.Height);

        MeasureHudText(hudText, out var contentWidthPx, out var contentHeightPx);
        UpdateHudGeometry(viewportWidth, viewportHeight, contentWidthPx, contentHeightPx);

        // Only update texture if text changed
        if (hudText != _lastHudText)
        {
            _lastHudText = hudText;
            UpdateHudTexture(hudText);
        }

        // Render HUD overlay
        _glUseProgram!(_hudProgram);

        if (_hudProjectionLocation >= 0)
        {
            unsafe
            {
                fixed (float* ptr = IdentityMatrix4x4)
                    _glUniformMatrix4fv!(_hudProjectionLocation, 1, false, (nint)ptr);
            }
        }

        if (_hudTextureLocation >= 0)
            _glUniform1I!(_hudTextureLocation, 0);

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _hudTextureOverlay);

        _glEnable!(GlBlend);
        _glBlendFunc!(GlSrcAlpha, GlOneMinusSrcAlpha);

        _glBindVertexArray!(_hudVao);
        _glDrawArrays!(GlTriangleFan, 0, 4);
        _glBindVertexArray!(0);

        _glDisable!(GlBlend);
        _glUseProgram!(0);
    }

    private void UpdateHudTexture(string text)
    {
        if (_hudPixels == null || _hudPixels.Length != _hudTextureWidth * _hudTextureHeight * 4)
            _hudPixels = new byte[_hudTextureWidth * _hudTextureHeight * 4];

        // Simple character rendering (using a very basic approach)
        RenderTextToBuffer(text, _hudPixels, _hudTextureWidth, _hudTextureHeight);

        // Upload to texture
        _glBindTexture!(GlTexture2D, _hudTextureOverlay);
        unsafe
        {
            fixed (byte* ptr = _hudPixels)
                _glTexSubImage2D!(GlTexture2D, 0, 0, 0, _hudTextureWidth, _hudTextureHeight, GlRgba, GlUnsignedByte, (nint)ptr);
        }
        _glBindTexture!(GlTexture2D, 0);
    }

    private void UpdateHudGeometry(int viewportWidth, int viewportHeight, int contentWidthPx, int contentHeightPx)
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
            left,  top,    0.0f, 1.0f,
            right, top,    uRight, 1.0f,
            right, bottom, uRight, vBottom,
            left,  bottom, 0.0f, vBottom
        ];

        _glBindBuffer!(GlArrayBuffer, _hudVbo);
        var handle = GCHandle.Alloc(quadVertices, GCHandleType.Pinned);
        try
        {
            _glBufferData!(GlArrayBuffer, quadVertices.Length * sizeof(float), handle.AddrOfPinnedObject(), GlStaticDraw);
        }
        finally
        {
            handle.Free();
        }

        _glBindBuffer!(GlArrayBuffer, 0);
    }

    private string BuildHudText()
    {
        var fmt = _currentPixelFormatInfo
            .ToUpperInvariant()
            .Replace("→", "/")
            .Replace("->", "/");

        var gpu = _currentHardwareDecoding ? 1 : 0;
        return $"RENDER:{_currentRenderFps:F1} VIDEO:{_currentVideoFps:F1} {fmt}\nQ:{_currentQueueDepth} UP:{_currentUploadMsPerFrame:F2} AV:{_currentAvDriftMs:F1} GPU:{gpu} DROP:{_currentDroppedFrames}";
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
        // Clear to transparent black
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;     // R
            pixels[i + 1] = 0; // G
            pixels[i + 2] = 0; // B
            pixels[i + 3] = 200; // A (semi-transparent)
        }

        // Simple monospace character drawing (ASCII only, using an upscaled dot matrix).
        var lines = text.Split('\n');
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
        // Very simple character bitmaps (ASCII A-Z, 0-9, and symbols)
        // Each row is represented as a byte where bits indicate pixels
        var patterns = GetCharacterPattern(ch);
        if (patterns == null)
            return;

        for (int row = 0; row < patterns.Length; row++)
        {
            var pattern = patterns[row];
            for (int col = 0; col < 8; col++)
            {
                if ((pattern & (1 << (7 - col))) != 0)
                {
                    for (int sy = 0; sy < HudScaleY; sy++)
                    {
                        for (int sx = 0; sx < HudScaleX; sx++)
                        {
                            int px = x + col * HudScaleX + sx;
                            int py = y + row * HudScaleY + sy;
                            if (px >= 0 && px < width && py >= 0 && py < height)
                            {
                                // OpenGL treats the first uploaded row as the bottom row of the
                                // texture, while our text raster uses a top-left origin. Flip the
                                // row here so the visible text is not vertically misplaced/clipped.
                                int flippedPy = height - 1 - py;
                                int idx = (flippedPy * width + px) * 4;
                                pixels[idx] = 0;       // R
                                pixels[idx + 1] = 220; // G (bright green for HUD)
                                pixels[idx + 2] = 0;   // B
                                pixels[idx + 3] = 255; // A (fully opaque)
                            }
                        }
                    }
                }
            }
        }
    }

    private static byte[]? GetCharacterPattern(char ch)
    {
        // Simple dot-matrix patterns for common characters
        return ch switch
        {
            // Numbers
            '0' => new byte[] { 0x7C, 0x82, 0x82, 0x82, 0x7C },
            '1' => new byte[] { 0x30, 0x30, 0x30, 0x30, 0x78 },
            '2' => new byte[] { 0x7C, 0x02, 0x7C, 0x80, 0xFE },
            '3' => new byte[] { 0xFE, 0x02, 0x7C, 0x02, 0xFE },
            '4' => new byte[] { 0x82, 0x82, 0xFE, 0x02, 0x02 },
            '5' => new byte[] { 0xFE, 0x80, 0xFE, 0x02, 0xFE },
            '6' => new byte[] { 0x7C, 0x80, 0xFE, 0x82, 0x7C },
            '7' => new byte[] { 0xFE, 0x02, 0x04, 0x08, 0x10 },
            '8' => new byte[] { 0x7C, 0x82, 0x7C, 0x82, 0x7C },
            '9' => new byte[] { 0x7C, 0x82, 0x7E, 0x02, 0x7C },
            // Letters
            'A' => new byte[] { 0x38, 0x44, 0x82, 0xFE, 0x82 },
            'B' => new byte[] { 0xFC, 0x82, 0xFC, 0x82, 0xFC },
            'C' => new byte[] { 0x7C, 0x80, 0x80, 0x80, 0x7C },
            'D' => new byte[] { 0xFC, 0x82, 0x82, 0x82, 0xFC },
            'E' => new byte[] { 0xFE, 0x80, 0xFE, 0x80, 0xFE },
            'F' => new byte[] { 0xFE, 0x80, 0xFE, 0x80, 0x80 },
            'G' => new byte[] { 0x7C, 0x80, 0x8E, 0x82, 0x7C },
            'H' => new byte[] { 0x82, 0x82, 0xFE, 0x82, 0x82 },
            'I' => new byte[] { 0x7C, 0x10, 0x10, 0x10, 0x7C },
            'L' => new byte[] { 0x80, 0x80, 0x80, 0x80, 0xFE },
            'N' => new byte[] { 0x82, 0xC2, 0xA2, 0x92, 0x8A },
            'O' => new byte[] { 0x7C, 0x82, 0x82, 0x82, 0x7C },
            'P' => new byte[] { 0xFC, 0x82, 0xFC, 0x80, 0x80 },
            'Q' => new byte[] { 0x7C, 0x82, 0x82, 0x8A, 0x7E },
            'R' => new byte[] { 0xFC, 0x82, 0xFC, 0x84, 0x82 },
            'S' => new byte[] { 0x7C, 0x80, 0x7C, 0x02, 0x7C },
            'T' => new byte[] { 0xFE, 0x10, 0x10, 0x10, 0x10 },
            'U' => new byte[] { 0x82, 0x82, 0x82, 0x82, 0x7C },
            'V' => new byte[] { 0x82, 0x82, 0x82, 0x44, 0x38 },
            'X' => new byte[] { 0x82, 0x44, 0x38, 0x44, 0x82 },
            'Y' => new byte[] { 0x82, 0x44, 0x38, 0x10, 0x10 },
            // Symbols
            ':' => new byte[] { 0x00, 0x30, 0x00, 0x30, 0x00 },
            '.' => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x30 },
            '-' => new byte[] { 0x00, 0x00, 0x7E, 0x00, 0x00 },
            '/' => new byte[] { 0x02, 0x04, 0x08, 0x10, 0x20 },
            _ => null
        };
    }


    // Additional GL constants and delegate for matrix uniform
    private const int GlBlend = 0x0BE2;
    private const int GlSrcAlpha = 0x0302;
    private const int GlOneMinusSrcAlpha = 0x0303;
    private const int GlTriangleFan = 0x0006;
    private const int GlNearest = 0x2600;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlClampToEdge = 0x812F;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlendFuncProc(int sfactor, int dfactor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EnableProc(int cap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisableProc(int cap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UniformMatrix4fvProc(int location, int count, bool transpose, nint value);

    private BlendFuncProc? _glBlendFunc;
    private EnableProc? _glEnable;
    private DisableProc? _glDisable;
    private UniformMatrix4fvProc? _glUniformMatrix4fv;

    internal void LoadHudGlFunctions()
    {
        bool Load<T>(string name, out T? d) where T : Delegate
        {
            var p = SDL.GLGetProcAddress(name);
            if (p == nint.Zero)
            {
                d = null;
                return false;
            }

            d = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        Load("glBlendFunc", out _glBlendFunc);
        Load("glEnable", out _glEnable);
        Load("glDisable", out _glDisable);
        Load("glUniformMatrix4fv", out _glUniformMatrix4fv);
    }

    internal void DisposeHudResources()
    {
        if (_hudProgram != 0) _glDeleteProgram?.Invoke(_hudProgram);
        if (_hudVao != 0) { var a = _hudVao; _glDeleteVertexArrays?.Invoke(1, in a); }
        if (_hudVbo != 0) { var b = _hudVbo; _glDeleteBuffers?.Invoke(1, in b); }
        if (_hudTextureOverlay != 0) { var t = _hudTextureOverlay; _glDeleteTextures?.Invoke(1, in t); }

        _hudProgram = 0;
        _hudVao = 0;
        _hudVbo = 0;
        _hudTextureOverlay = 0;
    }
}

