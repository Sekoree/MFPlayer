namespace Seko.OwnaudioNET.OpenGL;

/// <summary>
/// Raw OpenGL 3.3 core / OpenGL ES 3.0 integer constant values.
/// <para>
/// These complement Avalonia's <c>GlConsts</c> with values it does not expose
/// and provide SDL3 / raw-delegate consumers with a single authoritative source
/// for every needed value so the magic numbers are never duplicated.
/// </para>
/// </summary>
public static class VideoGlConstants
{
    // ── Texture formats ──────────────────────────────────────────────────────
    public const int R8            = 0x8229;
    public const int R16           = 0x822A;
    public const int Rg8           = 0x822B;
    public const int Rg16          = 0x822C;
    public const int Red           = 0x1903;
    public const int Rg            = 0x8227;
    public const int Rgba8         = 0x8058;
    public const int Rgba          = 0x1908;
    public const int UnsignedByte  = 0x1401;
    public const int UnsignedShort = 0x1403;

    // ── Texture units / targets ──────────────────────────────────────────────
    public const int Texture2D        = 0x0DE1;
    public const int Texture0         = 0x84C0;
    public const int Texture1         = Texture0 + 1;
    public const int Texture2         = Texture0 + 2;
    public const int TextureMinFilter = 0x2801;
    public const int TextureMagFilter = 0x2800;
    public const int TextureWrapS     = 0x2802;
    public const int TextureWrapT     = 0x2803;
    public const int Linear           = 0x2601;
    public const int Nearest          = 0x2600;
    public const int ClampToEdge      = 0x812F;

    // ── Buffer / vertex ──────────────────────────────────────────────────────
    public const int ArrayBuffer  = 0x8892;
    public const int StaticDraw   = 0x88E4;
    public const int Float        = 0x1406;

    // ── Draw primitives ──────────────────────────────────────────────────────
    public const int Triangles   = 0x0004;
    public const int TriangleFan = 0x0006;

    // ── Clear / blend ────────────────────────────────────────────────────────
    public const int ColorBufferBit    = 0x00004000;
    public const int Blend             = 0x0BE2;
    public const int SrcAlpha          = 0x0302;
    public const int OneMinusSrcAlpha  = 0x0303;

    // ── Shader / program ─────────────────────────────────────────────────────
    public const int VertexShader   = 0x8B31;
    public const int FragmentShader = 0x8B30;
    public const int CompileStatus  = 0x8B81;
    public const int LinkStatus     = 0x8B82;
}

