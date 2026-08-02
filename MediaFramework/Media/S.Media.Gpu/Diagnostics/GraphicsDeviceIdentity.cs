using Silk.NET.OpenGL;

namespace S.Media.Gpu.Diagnostics;

/// <summary>
/// What the GL driver says it is, captured once so a status line or a bug report can name the device.
/// </summary>
/// <remarks>
/// <para>
/// Every field here is a string the driver handed us. None of it is interpreted as a capability
/// decision - real capability checks query the specific extension or limit at the point of use. This
/// exists so a black screen or a slow path can be attributed: "GL_RENDERER=llvmpipe" answers, in one
/// line, a question that otherwise costs an afternoon.
/// </para>
/// <para>
/// <see cref="IsEmbeddedProfile"/> earns its place. A GLES context and a desktop-GL context report
/// success identically right up to the point where desktop-only calls fault, and on at least one
/// machine here the system projectM is a GLES build that hard-crashes under a desktop context. Having
/// the profile visible turns that from an unexplained SIGSEGV into a legible mismatch.
/// </para>
/// </remarks>
public sealed record GraphicsDeviceIdentity(
    string Vendor,
    string Renderer,
    string Version,
    string ShadingLanguageVersion,
    bool IsEmbeddedProfile,
    int MaxTextureSize)
{
    /// <summary>Placeholder for "no GL context has been brought up yet" - never null, never throws.</summary>
    public static GraphicsDeviceIdentity Unknown { get; } =
        new("?", "?", "?", "?", IsEmbeddedProfile: false, MaxTextureSize: 0);

    private static GraphicsDeviceIdentity? _current;

    /// <summary>
    /// The first identity captured in this process, or <see cref="Unknown"/> before any GL context exists.
    /// </summary>
    /// <remarks>
    /// First capture wins. Compositions each hold their own thread-affine context, but they are contexts
    /// on one machine's driver, so the second capture would only restate the first. A genuine multi-GPU
    /// split would need per-context identities; nothing in the framework selects a device today, so
    /// promising that would be promising something we do not do.
    /// </remarks>
    public static GraphicsDeviceIdentity Current => Volatile.Read(ref _current) ?? Unknown;

    /// <summary>True once any GL context has reported in.</summary>
    public static bool IsKnown => Volatile.Read(ref _current) is not null;

    /// <summary>
    /// Reads the identity off a current GL context and publishes it as <see cref="Current"/> if nothing
    /// has claimed that yet. Best-effort: a driver that refuses a query yields "?" rather than throwing.
    /// </summary>
    /// <param name="gl">A GL API whose context is current on the calling thread.</param>
    /// <remarks>
    /// Safe to call on every renderer startup. Callers are diagnostics paths on a live context, so this
    /// must never be the thing that takes a renderer down - hence the swallow.
    /// </remarks>
    public static GraphicsDeviceIdentity Capture(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);

        var identity = Read(gl);
        Interlocked.CompareExchange(ref _current, identity, null);
        return identity;
    }

    private static GraphicsDeviceIdentity Read(GL gl)
    {
        var vendor = Query(gl, StringName.Vendor);
        var renderer = Query(gl, StringName.Renderer);
        var version = Query(gl, StringName.Version);
        var glsl = Query(gl, StringName.ShadingLanguageVersion);

        var maxTexture = 0;
        try
        {
            gl.GetInteger(GetPName.MaxTextureSize, out maxTexture);
        }
        catch
        {
            /* limit query best-effort - an unknown limit reads as 0, not as a lie */
        }

        // The GLES version string is specified to begin "OpenGL ES"; desktop GL starts with the number.
        var embedded = version.StartsWith("OpenGL ES", StringComparison.OrdinalIgnoreCase);

        return new GraphicsDeviceIdentity(vendor, renderer, version, glsl, embedded, maxTexture);
    }

    private static string Query(GL gl, StringName name)
    {
        try
        {
            var s = gl.GetStringS(name);
            return string.IsNullOrWhiteSpace(s) ? "?" : s;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>Resets the captured identity. Tests only - production capture is once per process.</summary>
    internal static void ResetForTests() => Volatile.Write(ref _current, null);

    /// <summary>One line for a status column: renderer first, because that is the part that differs.</summary>
    public override string ToString() =>
        IsEmbeddedProfile ? $"{Renderer} ({Version}, ES)" : $"{Renderer} ({Version})";
}
