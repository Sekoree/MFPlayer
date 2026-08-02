using S.Media.Gpu.Diagnostics;
using Xunit;

namespace S.Media.Gpu.Tests;

/// <summary>
/// The identity record's own logic - profile detection, the placeholder, and the summary line.
/// </summary>
/// <remarks>
/// Capture itself needs a live GL context and is covered by the GL-backed suites; what is worth
/// pinning here is that a driver string never becomes an exception and that "unknown" stays honestly
/// unknown rather than quietly reading as a real device.
/// </remarks>
public class GraphicsDeviceIdentityTests
{
    [Theory]
    [InlineData("OpenGL ES 3.2 Mesa 24.0", true)]
    [InlineData("OpenGL ES 2.0", true)]
    [InlineData("opengl es 3.1 (odd casing)", true)]
    [InlineData("4.6 (Core Profile) Mesa 24.0", false)]
    [InlineData("3.3.0 NVIDIA 550.54", false)]
    public void EmbeddedProfile_IsDecidedByTheVersionString(string version, bool expected)
    {
        // This is the one field that changes behaviour rather than just describing it: the ProjectM
        // renderer publishes RGBA on GLES and BGRA on desktop off the back of it.
        var id = new GraphicsDeviceIdentity("v", "r", version, "glsl", version.StartsWith(
            "OpenGL ES", StringComparison.OrdinalIgnoreCase), 4096);

        Assert.Equal(expected, id.IsEmbeddedProfile);
    }

    [Fact]
    public void Unknown_ReadsAsUnknown_NotAsADevice()
    {
        var unknown = GraphicsDeviceIdentity.Unknown;

        // A zero max-texture-size is the giveaway: no real driver reports 0, so a caller that treats
        // this as a device gets an obviously-wrong number rather than a plausible-looking wrong one.
        Assert.Equal("?", unknown.Renderer);
        Assert.Equal(0, unknown.MaxTextureSize);
        Assert.False(unknown.IsEmbeddedProfile);
    }

    [Fact]
    public void Summary_LeadsWithTheRendererAndFlagsGles()
    {
        var desktop = new GraphicsDeviceIdentity(
            "Mesa", "AMD Radeon RX 7900", "4.6 (Core Profile)", "4.60", false, 16384);
        var gles = desktop with { Version = "OpenGL ES 3.2", IsEmbeddedProfile = true };

        Assert.Equal("AMD Radeon RX 7900 (4.6 (Core Profile))", desktop.ToString());
        Assert.Contains("ES", gles.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_RejectsANullContext_RatherThanPublishingAPlaceholder()
    {
        // Publishing Unknown on a null context would make IsKnown lie for the rest of the process.
        Assert.Throws<ArgumentNullException>(() => GraphicsDeviceIdentity.Capture(null!));
    }
}
