namespace S.Media.OpenGL;

/// <summary>
/// Canonical GLSL shader sources shared by all OpenGL rendering backends
/// (<c>SDL3VideoView</c>, <c>SDL3ShaderPipeline</c>, <c>AvaloniaGLRenderer</c>).
/// Editing a shader here fixes it everywhere.
/// </summary>
/// <remarks>
/// Two profiles are provided for each shader stage:
/// <list type="bullet">
/// <item><b>Core</b> — OpenGL 3.3 core profile (<c>#version 330 core</c>)</item>
/// <item><b>Es</b>   — OpenGL ES 3.0 (<c>#version 300 es</c>)</item>
/// </list>
///
/// YUV fragment shaders expose two uniforms beyond the texture samplers:
/// <list type="bullet">
/// <item><c>uPixelFormat</c> (int) — selects the chroma layout / bit-depth path.</item>
/// <item><c>uFullRange</c> (int) — <c>1</c> = full range (0–255), <c>0</c> = limited/TV range
///   (16–235 luma, 16–240 chroma). Most H.264/H.265 content is limited range.</item>
/// </list>
/// </remarks>
internal static class GlslShaders
{
    // ── Vertex ───────────────────────────────────────────────────────────────

    internal static string VertexCore { get; } = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    internal static string VertexEs { get; } = """
        #version 300 es
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    // ── Fragment — RGBA ───────────────────────────────────────────────────────

    internal static string FragmentRgbaCore { get; } = """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main() {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    internal static string FragmentRgbaEs { get; } = """
        #version 300 es
        precision mediump float;
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main() {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    // ── Fragment — YUV ────────────────────────────────────────────────────────
    //
    // uPixelFormat modes:
    //   1 = NV12   (semi-planar, 8-bit,  UV interleaved)
    //   2 = planar 8-bit  (YUV 4:2:0 / 4:2:2 / 4:4:4)
    //   3 = P010LE (semi-planar, 10-bit, UV interleaved)
    //   4 = planar 10-bit (YUV 4:2:0 / 4:2:2 / 4:4:4 P10LE)
    //
    // uFullRange:
    //   0 = limited range — apply BT.601/709 range expansion (16-235 luma → 0-1)
    //   1 = full range    — samples are already 0-1 (or 0-1023 for 10-bit)
    //
    // BT.709 colour matrix coefficients used throughout.

    private const string YuvBody = """
        vec3 yuvToRgb(float y, float u, float v) {
            float r = y + 1.5748 * v;
            float g = y - 0.1873 * u - 0.4681 * v;
            float b = y + 1.8556 * u;
            return clamp(vec3(r, g, b), 0.0, 1.0);
        }

        void main() {
            // Scale 10-bit samples (stored as 16-bit normalised) back to [0,1] equivalent
            float scale = (uPixelFormat == 3 || uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

            float y = texture(uTextureY, vTexCoord).r * scale;
            float u, v;

            if (uPixelFormat == 1 || uPixelFormat == 3) {
                // Semi-planar: UV packed in RG of uTextureU
                vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                u = uv.r;
                v = uv.g;
            } else {
                u = texture(uTextureU, vTexCoord).r * scale;
                v = texture(uTextureV, vTexCoord).r * scale;
            }

            // Limited-range expansion (BT.709 / BT.601):
            //   luma  16-235 → 0-1    multiply by 255/219, subtract 16/255
            //   chroma 16-240 → -0.5..+0.5   multiply by 255/224, subtract 0.5
            if (uFullRange == 0) {
                y = (y - 16.0 / 255.0) * (255.0 / 219.0);
                u = (u - 128.0 / 255.0) * (255.0 / 224.0);
                v = (v - 128.0 / 255.0) * (255.0 / 224.0);
            } else {
                u -= 0.5;
                v -= 0.5;
            }

            FragColor = vec4(yuvToRgb(y, u, v), 1.0);
        }
        """;

    internal static string FragmentYuvCore { get; } =
        "#version 330 core\n" +
        "in vec2 vTexCoord;\n" +
        "uniform sampler2D uTextureY;\n" +
        "uniform sampler2D uTextureU;\n" +
        "uniform sampler2D uTextureV;\n" +
        "uniform int uPixelFormat;\n" +
        "uniform int uFullRange;\n" +
        "out vec4 FragColor;\n" +
        YuvBody;

    internal static string FragmentYuvEs { get; } =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in vec2 vTexCoord;\n" +
        "uniform sampler2D uTextureY;\n" +
        "uniform sampler2D uTextureU;\n" +
        "uniform sampler2D uTextureV;\n" +
        "uniform int uPixelFormat;\n" +
        "uniform int uFullRange;\n" +
        "out vec4 FragColor;\n" +
        YuvBody;
}

