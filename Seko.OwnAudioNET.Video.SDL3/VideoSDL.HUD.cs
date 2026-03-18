using System.Runtime.InteropServices;
using Seko.OwnAudioNET.Video.OpenGL;
using SDL3;

namespace Seko.OwnAudioNET.Video.SDL3;

public sealed partial class VideoSDL
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

    private void InitializeHudRendering()
    {
        if (_hudProgram != 0)
            return;

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

        _glGenVertexArrays!(1, out _hudVao);
        _glGenBuffers!(1, out _hudVbo);

        _glBindVertexArray!(_hudVao);
        _glBindBuffer!(GlArrayBuffer, _hudVbo);

        float[] quadVertices =
        [
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

    private void RenderHudOverlay(int surfaceWidth, int surfaceHeight)
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

        if (hudText != _lastHudText)
        {
            _lastHudText = hudText;
            UpdateHudTexture(hudText);
        }

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

        RenderTextToBuffer(text, _hudPixels, _hudTextureWidth, _hudTextureHeight);

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
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 200;
        }

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
            'H' => [0x82, 0x82, 0xFE, 0x82, 0x82],
            'I' => [0x7C, 0x10, 0x10, 0x10, 0x7C],
            'L' => [0x80, 0x80, 0x80, 0x80, 0xFE],
            'N' => [0x82, 0xC2, 0xA2, 0x92, 0x8A],
            'O' => [0x7C, 0x82, 0x82, 0x82, 0x7C],
            'P' => [0xFC, 0x82, 0xFC, 0x80, 0x80],
            'Q' => [0x7C, 0x82, 0x82, 0x8A, 0x7E],
            'R' => [0xFC, 0x82, 0xFC, 0x84, 0x82],
            'S' => [0x7C, 0x80, 0x7C, 0x02, 0x7C],
            'T' => [0xFE, 0x10, 0x10, 0x10, 0x10],
            'U' => [0x82, 0x82, 0x82, 0x82, 0x7C],
            'V' => [0x82, 0x82, 0x82, 0x44, 0x38],
            'X' => [0x82, 0x44, 0x38, 0x44, 0x82],
            'Y' => [0x82, 0x44, 0x38, 0x10, 0x10],
            ':' => [0x00, 0x30, 0x00, 0x30, 0x00],
            '.' => [0x00, 0x00, 0x00, 0x00, 0x30],
            '-' => [0x00, 0x00, 0x7E, 0x00, 0x00],
            '/' => [0x02, 0x04, 0x08, 0x10, 0x20],
            _ => null
        };
    }

    private const int GlBlend            = VideoGlConstants.Blend;
    private const int GlSrcAlpha         = VideoGlConstants.SrcAlpha;
    private const int GlOneMinusSrcAlpha = VideoGlConstants.OneMinusSrcAlpha;
    private const int GlTriangleFan      = VideoGlConstants.TriangleFan;
    private const int GlNearest          = VideoGlConstants.Nearest;
    private const int GlTextureWrapS     = VideoGlConstants.TextureWrapS;
    private const int GlTextureWrapT     = VideoGlConstants.TextureWrapT;
    private const int GlClampToEdge      = VideoGlConstants.ClampToEdge;

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

    private void LoadHudGlFunctions()
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

    private void DisposeHudResources()
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

