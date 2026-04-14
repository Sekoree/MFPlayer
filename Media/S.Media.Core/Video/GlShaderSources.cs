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

    /// <summary>
    /// Passthrough that compensates for the Y-flipped fullscreen quad when sampling from
    /// an FBO whose content was already rendered with that same flipped quad (double-flip).
    /// </summary>
    public const string FragmentPassthroughFbo = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexture;
        void main() {
            fragColor = texture(uTexture, vec2(vUV.x, 1.0 - vUV.y));
        }
        """;

    /// <summary>
    /// Bicubic (Catmull-Rom) blit shader for rendering an FBO colour attachment to the screen.
    /// Uses 16 <c>texelFetch</c> calls in a 4×4 separable kernel — much sharper than trilinear
    /// mipmapping (which is a box filter) while still providing clean interpolation at any scale.
    /// <para>
    /// At 1:1 the kernel degenerates to an exact texel fetch (all weight on the centre sample).
    /// At moderate down/upscale ratios (up to ~2×) the negative lobes of the a = −0.5 kernel
    /// preserve sharp edges and fine detail (e.g. small text).
    /// </para>
    /// <para>Includes the Y-flip required when sampling from an FBO rendered with the flipped
    /// fullscreen quad.</para>
    /// </summary>
    public const string FragmentBicubicBlit = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexture;

        // Catmull-Rom cubic weight (a = -0.5 Mitchell-Netravali).
        float crWeight(float t) {
            float at  = abs(t);
            float at2 = at * at;
            float at3 = at2 * at;
            if (at <= 1.0) return  1.5 * at3 - 2.5 * at2 + 1.0;
            if (at <= 2.0) return -0.5 * at3 + 2.5 * at2 - 4.0 * at + 2.0;
            return 0.0;
        }

        void main() {
            // Flip Y to compensate for FBO orientation (double-flip with the quad).
            vec2 uv = vec2(vUV.x, 1.0 - vUV.y);

            ivec2 ts = textureSize(uTexture, 0);
            float srcX = uv.x * float(ts.x) - 0.5;
            float srcY = uv.y * float(ts.y) - 0.5;

            int   ix = int(floor(srcX));
            int   iy = int(floor(srcY));
            float fx = fract(srcX);
            float fy = fract(srcY);

            // Horizontal weights.
            float wx0 = crWeight(fx + 1.0);
            float wx1 = crWeight(fx);
            float wx2 = crWeight(1.0 - fx);
            float wx3 = crWeight(2.0 - fx);
            // Vertical weights.
            float wy0 = crWeight(fy + 1.0);
            float wy1 = crWeight(fy);
            float wy2 = crWeight(1.0 - fy);
            float wy3 = crWeight(2.0 - fy);

            // 4×4 bicubic tap grid (16 texelFetch calls).
            ivec2 hi = ts - 1;
            vec4 r0 = wx0 * texelFetch(uTexture, clamp(ivec2(ix-1, iy-1), ivec2(0), hi), 0)
                    + wx1 * texelFetch(uTexture, clamp(ivec2(ix,   iy-1), ivec2(0), hi), 0)
                    + wx2 * texelFetch(uTexture, clamp(ivec2(ix+1, iy-1), ivec2(0), hi), 0)
                    + wx3 * texelFetch(uTexture, clamp(ivec2(ix+2, iy-1), ivec2(0), hi), 0);
            vec4 r1 = wx0 * texelFetch(uTexture, clamp(ivec2(ix-1, iy  ), ivec2(0), hi), 0)
                    + wx1 * texelFetch(uTexture, clamp(ivec2(ix,   iy  ), ivec2(0), hi), 0)
                    + wx2 * texelFetch(uTexture, clamp(ivec2(ix+1, iy  ), ivec2(0), hi), 0)
                    + wx3 * texelFetch(uTexture, clamp(ivec2(ix+2, iy  ), ivec2(0), hi), 0);
            vec4 r2 = wx0 * texelFetch(uTexture, clamp(ivec2(ix-1, iy+1), ivec2(0), hi), 0)
                    + wx1 * texelFetch(uTexture, clamp(ivec2(ix,   iy+1), ivec2(0), hi), 0)
                    + wx2 * texelFetch(uTexture, clamp(ivec2(ix+1, iy+1), ivec2(0), hi), 0)
                    + wx3 * texelFetch(uTexture, clamp(ivec2(ix+2, iy+1), ivec2(0), hi), 0);
            vec4 r3 = wx0 * texelFetch(uTexture, clamp(ivec2(ix-1, iy+2), ivec2(0), hi), 0)
                    + wx1 * texelFetch(uTexture, clamp(ivec2(ix,   iy+2), ivec2(0), hi), 0)
                    + wx2 * texelFetch(uTexture, clamp(ivec2(ix+1, iy+2), ivec2(0), hi), 0)
                    + wx3 * texelFetch(uTexture, clamp(ivec2(ix+2, iy+2), ivec2(0), hi), 0);

            fragColor = clamp(wy0*r0 + wy1*r1 + wy2*r2 + wy3*r3, vec4(0.0), vec4(1.0));
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
            if (uColorMatrix == 2) {
                // BT.2020
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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
            if (uColorMatrix == 2) {
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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
            if (uColorMatrix == 2) {
                // BT.2020
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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

    /// <summary>
    /// UYVY 4:2:2 packed format. Data is uploaded as an RGBA8 texture at (width/2)×height;
    /// each RGBA texel packs one pixel pair: R=U, G=Y0, B=V, A=Y1.
    /// <para>
    /// Both <b>luma</b> and <b>chroma</b> are reconstructed via separable
    /// 4×4 Catmull-Rom (bicubic) interpolation.  Luma operates in full-width
    /// source-pixel space (16 <c>texelFetch</c> calls extracting G/A per pixel);
    /// chroma operates in half-width texel space with a co-siting correction
    /// (<c>srcX / 2</c>) so even pixels hit the texel centre exactly and odd
    /// pixels interpolate symmetrically.  Because UYVY 4:2:2 has full vertical
    /// chroma resolution, the vertical weights are computed once and shared
    /// between the luma and chroma passes (32 <c>texelFetch</c> total).
    /// </para>
    /// </summary>
    public const string FragmentUyvy422 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexUYVY;
        uniform int uVideoWidth;
        uniform int uLimitedRange;
        uniform int uColorMatrix;

        // Catmull-Rom cubic weight (a = -0.5 Mitchell-Netravali).
        // Negative lobes at |t| in (1,2) sharpen edges that bilinear smooths over.
        float crWeight(float t) {
            float at  = abs(t);
            float at2 = at * at;
            float at3 = at2 * at;
            if (at <= 1.0)
                return  1.5 * at3 - 2.5 * at2 + 1.0;
            if (at <= 2.0)
                return -0.5 * at3 + 2.5 * at2 - 4.0 * at + 2.0;
            return 0.0;
        }

        // Fetch a single per-pixel luma sample at integer source-pixel coordinates.
        // Each UYVY texel packs two Y values: G = Y_even, A = Y_odd.
        float fetchLuma(int px, int py, ivec2 ts) {
            px = clamp(px, 0, ts.x * 2 - 1);
            py = clamp(py, 0, ts.y - 1);
            vec4 uyvy = texelFetch(uTexUYVY, ivec2(px >> 1, py), 0);
            return ((px & 1) == 0) ? uyvy.g : uyvy.a;
        }

        // Fetch chroma (U, V) from a texel in the half-width UYVY texture.
        vec2 fetchChroma(int tx, int ty, ivec2 ts) {
            tx = clamp(tx, 0, ts.x - 1);
            ty = clamp(ty, 0, ts.y - 1);
            vec4 uyvy = texelFetch(uTexUYVY, ivec2(tx, ty), 0);
            return vec2(uyvy.r, uyvy.b);   // (U, V)
        }

        void main() {
            ivec2 texSize = textureSize(uTexUYVY, 0);

            // Source-pixel position in full-resolution space.
            float srcX = vUV.x * float(uVideoWidth) - 0.5;
            float srcY = vUV.y * float(texSize.y)    - 0.5;

            // ── Shared vertical grid (4:2:2 = full vertical chroma) ─────
            int   iy = int(floor(srcY));
            float fy = fract(srcY);
            float wy0 = crWeight(fy + 1.0);
            float wy1 = crWeight(fy);
            float wy2 = crWeight(1.0 - fy);
            float wy3 = crWeight(2.0 - fy);

            // ── Luma: 4×4 bicubic in full-width pixel space ─────────────
            int   ix = int(floor(srcX));
            float fx = fract(srcX);
            float wx0 = crWeight(fx + 1.0);
            float wx1 = crWeight(fx);
            float wx2 = crWeight(1.0 - fx);
            float wx3 = crWeight(2.0 - fx);

            float lr0 = wx0*fetchLuma(ix-1,iy-1,texSize) + wx1*fetchLuma(ix,iy-1,texSize)
                      + wx2*fetchLuma(ix+1,iy-1,texSize) + wx3*fetchLuma(ix+2,iy-1,texSize);
            float lr1 = wx0*fetchLuma(ix-1,iy,  texSize) + wx1*fetchLuma(ix,iy,  texSize)
                      + wx2*fetchLuma(ix+1,iy,  texSize) + wx3*fetchLuma(ix+2,iy,  texSize);
            float lr2 = wx0*fetchLuma(ix-1,iy+1,texSize) + wx1*fetchLuma(ix,iy+1,texSize)
                      + wx2*fetchLuma(ix+1,iy+1,texSize) + wx3*fetchLuma(ix+2,iy+1,texSize);
            float lr3 = wx0*fetchLuma(ix-1,iy+2,texSize) + wx1*fetchLuma(ix,iy+2,texSize)
                      + wx2*fetchLuma(ix+1,iy+2,texSize) + wx3*fetchLuma(ix+2,iy+2,texSize);

            float yRaw = clamp(wy0*lr0 + wy1*lr1 + wy2*lr2 + wy3*lr3, 0.0, 1.0);

            // ── Chroma: 4×4 bicubic in half-width texel space ───────────
            // Co-siting: UYVY chroma is co-sited with the even (left) pixel
            // of each pair, so the continuous chroma position is srcX / 2.
            float chromaX = srcX * 0.5;
            int   cx  = int(floor(chromaX));
            float cfx = fract(chromaX);
            float cwx0 = crWeight(cfx + 1.0);
            float cwx1 = crWeight(cfx);
            float cwx2 = crWeight(1.0 - cfx);
            float cwx3 = crWeight(2.0 - cfx);

            vec2 cr0 = cwx0*fetchChroma(cx-1,iy-1,texSize) + cwx1*fetchChroma(cx,iy-1,texSize)
                     + cwx2*fetchChroma(cx+1,iy-1,texSize) + cwx3*fetchChroma(cx+2,iy-1,texSize);
            vec2 cr1 = cwx0*fetchChroma(cx-1,iy,  texSize) + cwx1*fetchChroma(cx,iy,  texSize)
                     + cwx2*fetchChroma(cx+1,iy,  texSize) + cwx3*fetchChroma(cx+2,iy,  texSize);
            vec2 cr2 = cwx0*fetchChroma(cx-1,iy+1,texSize) + cwx1*fetchChroma(cx,iy+1,texSize)
                     + cwx2*fetchChroma(cx+1,iy+1,texSize) + cwx3*fetchChroma(cx+2,iy+1,texSize);
            vec2 cr3 = cwx0*fetchChroma(cx-1,iy+2,texSize) + cwx1*fetchChroma(cx,iy+2,texSize)
                     + cwx2*fetchChroma(cx+1,iy+2,texSize) + cwx3*fetchChroma(cx+2,iy+2,texSize);

            vec2 chromaRaw = clamp(wy0*cr0 + wy1*cr1 + wy2*cr2 + wy3*cr3,
                                   vec2(0.0), vec2(1.0));
            float uRaw = chromaRaw.x;
            float vRaw = chromaRaw.y;

            // ── YUV → RGB with range + matrix ───────────────────────────
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
            if (uColorMatrix == 2) {
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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

    /// <summary>
    /// P010 (10-bit semi-planar 4:2:0). Two planes: Y as R16UI (w×h), UV as RG16UI (w/2×h/2).
    /// </summary>
    public const string FragmentP010 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform usampler2D uTexY;
        uniform usampler2D uTexUV;
        uniform int uLimitedRange;
        uniform int uColorMatrix;

        float unpack10(uint raw) {
            // P010 stores 10-bit value in the upper 10 bits of a 16-bit word
            uint v = raw >> 6u;
            return clamp(float(v) / 1023.0, 0.0, 1.0);
        }

        void main() {
            float yRaw = unpack10(texture(uTexY, vUV).r);
            uvec2 uvRaw2 = texture(uTexUV, vUV).rg;
            float uRaw = unpack10(uvRaw2.r);
            float vRaw = unpack10(uvRaw2.g);

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
            if (uColorMatrix == 2) {
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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

    /// <summary>
    /// Yuv444p (8-bit planar 4:4:4). Three planes: Y, U, V — each R8, all same size.
    /// </summary>
    public const string FragmentYuv444p = """
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
            if (uColorMatrix == 2) {
                r = y + 1.4746 * v;
                g = y - 0.1645 * u - 0.5713 * v;
                b = y + 1.8814 * u;
            } else if (uColorMatrix == 1) {
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

    /// <summary>
    /// Gray8 / single-channel luma. One R8 texture; rendered as greyscale.
    /// </summary>
    public const string FragmentGray8 = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexY;
        void main() {
            float y = texture(uTexY, vUV).r;
            fragColor = vec4(y, y, y, 1.0);
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

