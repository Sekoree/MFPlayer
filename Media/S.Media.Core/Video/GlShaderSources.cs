namespace S.Media.Core.Video;

/// <summary>
/// Shared OpenGL shader sources and fullscreen quad geometry used by SDL3 and Avalonia renderers.
/// </summary>
public static class GlShaderSources
{
    public const string VertexPassthrough = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vUV = aUV;
        }
        """;

    public const string FragmentPassthrough = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexture;
        void main() {
            fragColor = texture(uTexture, vUV);
        }
        """;

    public const string FragmentNv12 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexY;
        uniform sampler2D uTexUV;
        uniform int uLimitedRange;
        uniform int uColorMatrix;
        void main() {
            float yRaw = texture(uTexY, vUV).r;
            vec2 uvRaw = texture(uTexUV, vUV).rg;

            float y = uLimitedRange != 0
                ? clamp((yRaw - (16.0 / 255.0)) * (255.0 / 219.0), 0.0, 1.0)
                : yRaw;
            float u = uLimitedRange != 0
                ? clamp((uvRaw.x - 0.5) * (255.0 / 224.0), -0.5, 0.5)
                : (uvRaw.x - 0.5);
            float v = uLimitedRange != 0
                ? clamp((uvRaw.y - 0.5) * (255.0 / 224.0), -0.5, 0.5)
                : (uvRaw.y - 0.5);

            float r;
            float g;
            float b;
            if (uColorMatrix != 0) {
                r = y + 1.5748 * v;
                g = y - 0.187324 * u - 0.468124 * v;
                b = y + 1.8556 * u;
            } else {
                r = y + 1.402 * v;
                g = y - 0.344136 * u - 0.714136 * v;
                b = y + 1.772 * u;
            }
            fragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }
        """;

    public const string FragmentI420 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexY;
        uniform sampler2D uTexU;
        uniform sampler2D uTexV;
        uniform int uLimitedRange;
        uniform int uColorMatrix;
        void main() {
            float yRaw = texture(uTexY, vUV).r;
            float uRaw = texture(uTexU, vUV).r;
            float vRaw = texture(uTexV, vUV).r;

            float y = uLimitedRange != 0
                ? clamp((yRaw - (16.0 / 255.0)) * (255.0 / 219.0), 0.0, 1.0)
                : yRaw;
            float u = uLimitedRange != 0
                ? clamp((uRaw - 0.5) * (255.0 / 224.0), -0.5, 0.5)
                : (uRaw - 0.5);
            float v = uLimitedRange != 0
                ? clamp((vRaw - 0.5) * (255.0 / 224.0), -0.5, 0.5)
                : (vRaw - 0.5);

            float r;
            float g;
            float b;
            if (uColorMatrix != 0) {
                r = y + 1.5748 * v;
                g = y - 0.187324 * u - 0.468124 * v;
                b = y + 1.8556 * u;
            } else {
                r = y + 1.402 * v;
                g = y - 0.344136 * u - 0.714136 * v;
                b = y + 1.772 * u;
            }
            fragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }
        """;

    public const string FragmentI422P10 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform usampler2D uTexY;
        uniform usampler2D uTexU;
        uniform usampler2D uTexV;
        uniform int uLimitedRange;
        uniform int uColorMatrix;

        float unpack10(uint raw) {
            uint v = raw;
            if (v > 1023u)
                v >>= 6u;
            return clamp(float(v) / 1023.0, 0.0, 1.0);
        }

        void main() {
            float yRaw = unpack10(texture(uTexY, vUV).r);
            float uRaw = unpack10(texture(uTexU, vUV).r);
            float vRaw = unpack10(texture(uTexV, vUV).r);

            // Use studio-range normalization when requested: Y in [64..940], UV centered at 512 with span 448.
            float y = uLimitedRange != 0
                ? clamp((yRaw - (64.0 / 1023.0)) * (1023.0 / 876.0), 0.0, 1.0)
                : yRaw;
            float u = uLimitedRange != 0
                ? clamp((uRaw - (512.0 / 1023.0)) * (1023.0 / 896.0), -0.5, 0.5)
                : (uRaw - 0.5);
            float v = uLimitedRange != 0
                ? clamp((vRaw - (512.0 / 1023.0)) * (1023.0 / 896.0), -0.5, 0.5)
                : (vRaw - 0.5);

            float r;
            float g;
            float b;
            if (uColorMatrix != 0) {
                // BT.709
                r = y + 1.5748 * v;
                g = y - 0.187324 * u - 0.468124 * v;
                b = y + 1.8556 * u;
            } else {
                // BT.601
                r = y + 1.402 * v;
                g = y - 0.344136 * u - 0.714136 * v;
                b = y + 1.772 * u;
            }
            fragColor = vec4(clamp(r, 0.0, 1.0), clamp(g, 0.0, 1.0), clamp(b, 0.0, 1.0), 1.0);
        }
        """;

    public static readonly float[] FullscreenQuadVerts =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f,
    ];
}

