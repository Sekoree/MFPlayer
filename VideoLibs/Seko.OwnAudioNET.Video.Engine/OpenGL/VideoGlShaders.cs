namespace Seko.OwnAudioNET.Video.OpenGL;

/// <summary>
/// GLSL source strings for the video renderer: a pass-through vertex shader,
/// an RGBA fragment shader, and a multi-format YUV → RGB fragment shader.
/// <para>
/// Both OpenGL 3.3 core and OpenGL ES 3.0 variants are provided so that
/// Avalonia (which supports both profiles) and SDL3 (core only) can pick
/// the correct version at runtime.
/// </para>
/// <para>
/// YUV pixel-format codes used by <c>uPixelFormat</c>:<br/>
/// 1 = NV12         (semi-planar, 8-bit)<br/>
/// 2 = YUV planar   (8-bit: yuv420p / yuv422p / yuv444p)<br/>
/// 3 = P010LE       (semi-planar, 10-bit MSB-aligned)<br/>
/// 4 = YUV10 planar (10-bit LSB-packed: yuv420p10le / yuv422p10le / yuv444p10le)
/// </para>
/// </summary>
public static class VideoGlShaders
{
    // ── OpenGL 3.3 core ──────────────────────────────────────────────────────

    /// <summary>OpenGL 3.3 core pass-through vertex shader.</summary>
    public static string VertexShaderCore =>
        """
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

    /// <summary>OpenGL 3.3 core RGBA fragment shader.</summary>
    public static string FragmentShaderCore =>
        """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main()
        {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    /// <summary>OpenGL 3.3 core multi-format YUV → RGB fragment shader.</summary>
    public static string YuvFragmentShaderCore =>
        """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTextureY;
        uniform sampler2D uTextureU;
        uniform sampler2D uTextureV;
        uniform int uPixelFormat;
        out vec4 FragColor;

        vec3 yuvToRgb(float y, float u, float v)
        {
            float r = y + 1.5748 * v;
            float g = y - 0.1873 * u - 0.4681 * v;
            float b = y + 1.8556 * u;
            return clamp(vec3(r, g, b), 0.0, 1.0);
        }

        void main()
        {
            // 1=NV12, 2=planar 8-bit, 3=P010LE, 4=planar 10-bit LSB-packed
            float scale = (uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

            float y = texture(uTextureY, vTexCoord).r * scale;
            float u;
            float v;

            if (uPixelFormat == 1 || uPixelFormat == 3)
            {
                vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                u = uv.r - 0.5;
                v = uv.g - 0.5;
            }
            else
            {
                u = texture(uTextureU, vTexCoord).r * scale - 0.5;
                v = texture(uTextureV, vTexCoord).r * scale - 0.5;
            }

            FragColor = vec4(yuvToRgb(y, u, v), 1.0);
        }
        """;

    // ── OpenGL ES 3.0 ────────────────────────────────────────────────────────

    /// <summary>OpenGL ES 3.0 pass-through vertex shader.</summary>
    public static string VertexShaderEs =>
        """
        #version 300 es
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main()
        {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    /// <summary>OpenGL ES 3.0 RGBA fragment shader.</summary>
    public static string FragmentShaderEs =>
        """
        #version 300 es
        precision mediump float;
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main()
        {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    /// <summary>OpenGL ES 3.0 multi-format YUV → RGB fragment shader.</summary>
    public static string YuvFragmentShaderEs =>
        """
        #version 300 es
        precision mediump float;
        in vec2 vTexCoord;
        uniform sampler2D uTextureY;
        uniform sampler2D uTextureU;
        uniform sampler2D uTextureV;
        uniform int uPixelFormat;
        out vec4 FragColor;

        vec3 yuvToRgb(float y, float u, float v)
        {
            float r = y + 1.5748 * v;
            float g = y - 0.1873 * u - 0.4681 * v;
            float b = y + 1.8556 * u;
            return clamp(vec3(r, g, b), 0.0, 1.0);
        }

        void main()
        {
            // 1=NV12, 2=planar 8-bit, 3=P010LE, 4=planar 10-bit LSB-packed
            float scale = (uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

            float y = texture(uTextureY, vTexCoord).r * scale;
            float u;
            float v;

            if (uPixelFormat == 1 || uPixelFormat == 3)
            {
                vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                u = uv.r - 0.5;
                v = uv.g - 0.5;
            }
            else
            {
                u = texture(uTextureU, vTexCoord).r * scale - 0.5;
                v = texture(uTextureV, vTexCoord).r * scale - 0.5;
            }

            FragColor = vec4(yuvToRgb(y, u, v), 1.0);
        }
        """;
}

