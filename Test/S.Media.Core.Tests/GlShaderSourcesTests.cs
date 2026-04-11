using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class GlShaderSourcesTests
{
    [Fact]
    public void FragmentNv12_ExposesRangeAndMatrixUniforms()
    {
        var src = GlShaderSources.FragmentNv12;

        Assert.Contains("uniform int uLimitedRange", src);
        Assert.Contains("uniform int uColorMatrix", src);
        Assert.Contains("255.0 / 219.0", src);
        Assert.Contains("1.5748", src);
    }

    [Fact]
    public void FragmentI420_ExposesRangeAndMatrixUniforms()
    {
        var src = GlShaderSources.FragmentI420;

        Assert.Contains("uniform int uLimitedRange", src);
        Assert.Contains("uniform int uColorMatrix", src);
        Assert.Contains("255.0 / 224.0", src);
        Assert.Contains("1.5748", src);
    }

    private static float Unpack10(uint raw)
    {
        uint v = raw;
        if (v > 1023)
            v >>= 6;
        return Math.Clamp(v / 1023f, 0f, 1f);
    }

    private static (float R, float G, float B) ConvertYuvToRgb(uint yRaw, uint uRaw, uint vRaw, bool limitedRange = false, bool bt709 = false)
    {
        float yRawN = Unpack10(yRaw);
        float uRawN = Unpack10(uRaw);
        float vRawN = Unpack10(vRaw);

        float y = limitedRange
            ? Math.Clamp((yRawN - (64f / 1023f)) * (1023f / 876f), 0f, 1f)
            : yRawN;
        float u = limitedRange
            ? Math.Clamp((uRawN - (512f / 1023f)) * (1023f / 896f), -0.5f, 0.5f)
            : (uRawN - 0.5f);
        float v = limitedRange
            ? Math.Clamp((vRawN - (512f / 1023f)) * (1023f / 896f), -0.5f, 0.5f)
            : (vRawN - 0.5f);

        float r = bt709
            ? Math.Clamp(y + 1.5748f * v, 0f, 1f)
            : Math.Clamp(y + 1.402f * v, 0f, 1f);
        float g = bt709
            ? Math.Clamp(y - 0.187324f * u - 0.468124f * v, 0f, 1f)
            : Math.Clamp(y - 0.344136f * u - 0.714136f * v, 0f, 1f);
        float b = bt709
            ? Math.Clamp(y + 1.8556f * u, 0f, 1f)
            : Math.Clamp(y + 1.772f * u, 0f, 1f);
        return (r, g, b);
    }

    [Fact]
    public void FragmentI422P10_UsesIntegerSamplerAnd10BitUnpack()
    {
        var src = GlShaderSources.FragmentI422P10;

        Assert.Contains("uniform usampler2D uTexY", src);
        Assert.Contains("uniform usampler2D uTexU", src);
        Assert.Contains("uniform usampler2D uTexV", src);
        Assert.Contains("uniform int uLimitedRange", src);
        Assert.Contains("uniform int uColorMatrix", src);
        Assert.Contains("if (v > 1023u)", src);
        Assert.Contains("float(v) / 1023.0", src);
        Assert.Contains("1.5748", src);
    }

    [Fact]
    public void FragmentI422P10_NeutralChroma_ProducesNeutralGray()
    {
        var (r, g, b) = ConvertYuvToRgb(512, 512, 512);

        Assert.True(Math.Abs(r - g) < 0.01f);
        Assert.True(Math.Abs(g - b) < 0.01f);
        Assert.InRange(r, 0.49f, 0.52f);
    }

    [Fact]
    public void FragmentI422P10_LumaIncrease_RaisesAllChannels()
    {
        var dark = ConvertYuvToRgb(200, 512, 512);
        var bright = ConvertYuvToRgb(800, 512, 512);

        Assert.True(bright.R > dark.R);
        Assert.True(bright.G > dark.G);
        Assert.True(bright.B > dark.B);
    }

    [Fact]
    public void FragmentI422P10_UnpackSupportsShifted16BitPayload()
    {
        float direct = Unpack10(940);
        float shifted16 = Unpack10(940u << 6);

        Assert.InRange(Math.Abs(direct - shifted16), 0f, 0.0001f);
    }

    [Fact]
    public void FragmentI422P10_LimitedRange_BlackPointMapsNearZero()
    {
        var full = ConvertYuvToRgb(64, 512, 512, limitedRange: false);
        var limited = ConvertYuvToRgb(64, 512, 512, limitedRange: true);

        Assert.True(limited.R < full.R);
        Assert.InRange(limited.R, 0f, 0.01f);
        Assert.InRange(limited.G, 0f, 0.01f);
        Assert.InRange(limited.B, 0f, 0.01f);
    }

    [Fact]
    public void FragmentI422P10_LimitedRange_WhitePointMapsNearOne()
    {
        var limited = ConvertYuvToRgb(940, 512, 512, limitedRange: true);

        Assert.InRange(limited.R, 0.99f, 1f);
        Assert.InRange(limited.G, 0.99f, 1f);
        Assert.InRange(limited.B, 0.99f, 1f);
    }

    [Fact]
    public void FragmentI422P10_Bt601VsBt709_ProduceDifferentRgbForSameSample()
    {
        var bt601 = ConvertYuvToRgb(700, 300, 800, limitedRange: false, bt709: false);
        var bt709 = ConvertYuvToRgb(700, 300, 800, limitedRange: false, bt709: true);

        Assert.True(Math.Abs(bt601.R - bt709.R) > 0.005f ||
                    Math.Abs(bt601.G - bt709.G) > 0.005f ||
                    Math.Abs(bt601.B - bt709.B) > 0.005f);
    }
}

