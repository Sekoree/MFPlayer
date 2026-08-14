using System.Buffers;
using S.Media.Compositor;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Compositor.Tests;

/// <summary>
/// F-07 fast-path coverage: the coverage-aware clear skip (a full-canvas Source blit makes the
/// transparent-black clear redundant; anything less must keep it) and the row-stepped bilinear
/// fast path (must match the generic per-pixel loop's region exactly and its bytes within one
/// rounding step). The clear-skip tests POISON the shared array pool first, so a wrongly-skipped
/// clear shows up as 0xFF garbage rather than as luckily-zero freshly-allocated memory.
/// </summary>
public sealed class CpuCompositorFastPathTests
{
    private static readonly Rational Fps = new(30, 1);

    private static VideoFrame GradientBgra(int w, int h, byte alpha = 255)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * stride + x * 4;
            pixels[i + 0] = (byte)(x * 16);
            pixels[i + 1] = (byte)(y * 16);
            pixels[i + 2] = (byte)((x + y) * 8);
            pixels[i + 3] = alpha;
        }

        return new VideoFrame(
            TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, Fps), [pixels], [stride]);
    }

    /// <summary>Rent-and-poison a few pool buffers of the composite's size class so the compositor
    /// most likely receives a non-zero buffer. Best-effort by nature (the pool gives no
    /// guarantees) - a fresh zeroed array can only make these tests pass vacuously, never fail.</summary>
    private static void PoisonPool(int byteCount)
    {
        var rented = new byte[4][];
        for (var i = 0; i < rented.Length; i++)
        {
            rented[i] = ArrayPool<byte>.Shared.Rent(byteCount);
            Array.Fill(rented[i], (byte)0xFF);
        }

        foreach (var buffer in rented)
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }

    [Fact]
    public void FullCoverSourceLayer_MatchesSourceExactly_EvenOnPoisonedPool()
    {
        var canvas = new VideoFormat(16, 16, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = GradientBgra(16, 16);
        PoisonPool(16 * 16 * 4);

        using var result = compositor.Composite(
            [new CompositorLayer(source, LayerTransform2D.Identity, 1f, BlendMode.Source)],
            TimeSpan.Zero);

        Assert.True(source.Planes[0].Span.SequenceEqual(result.Planes[0].Span));
    }

    [Fact]
    public void PartialCoverLayer_UncoveredCanvasStaysTransparent_EvenOnPoisonedPool()
    {
        // A translated layer does NOT cover the canvas, so the clear must still run - garbage in
        // the uncovered strip is exactly the bug the coverage predicate must never introduce.
        var canvas = new VideoFormat(16, 16, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = GradientBgra(16, 16);
        PoisonPool(16 * 16 * 4);

        using var result = compositor.Composite(
            [new CompositorLayer(source, LayerTransform2D.Translate(4f, 0f), 1f, BlendMode.Source)],
            TimeSpan.Zero);

        var pixels = result.Planes[0].Span;
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 4; x++)
        {
            var i = y * 16 * 4 + x * 4;
            Assert.Equal(0, pixels[i + 3]);
            Assert.Equal(0, pixels[i + 0]);
        }
    }

    [Fact]
    public void ZeroOpacityFirstLayer_FullCoverSecondLayerStillComposesCorrectly()
    {
        // The "first drawn" layer for coverage purposes is the first with opacity > 0 - the same
        // layer Composite's dstUntouched logic selects.
        var canvas = new VideoFormat(16, 16, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var invisible = GradientBgra(16, 16);
        using var source = GradientBgra(16, 16);
        PoisonPool(16 * 16 * 4);

        using var result = compositor.Composite(
            [
                CompositorLayer.Default(invisible) with { Opacity = 0f },
                new CompositorLayer(source, LayerTransform2D.Identity, 1f, BlendMode.Source),
            ],
            TimeSpan.Zero);

        Assert.True(source.Planes[0].Span.SequenceEqual(result.Planes[0].Span));
    }

    [Fact]
    public void FullCoverSourceOverLayer_BlendsAgainstClearedCanvas_EvenOnPoisonedPool()
    {
        // SourceOver READS the destination, so full geometric coverage is not enough to skip the
        // clear: a semi-transparent pixel must blend against transparent black, not pool garbage.
        var canvas = new VideoFormat(16, 16, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = GradientBgra(16, 16, alpha: 128);
        PoisonPool(16 * 16 * 4);

        using var result = compositor.Composite(
            [CompositorLayer.Default(source) with { BlendMode = BlendMode.SourceOver }],
            TimeSpan.Zero);

        // Premultiplied source-over onto transparent black is the source values unchanged.
        var src = source.Planes[0].Span;
        var dst = result.Planes[0].Span;
        for (var i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(src[i + 0], dst[i + 0]);
            Assert.Equal(src[i + 3], dst[i + 3]);
        }
    }

    [Fact]
    public void BilinearIdentityTransform_IsAnExactCopy()
    {
        // At exact pixel centers every bilinear weight collapses onto one texel, so identity
        // bilinear must reproduce the source byte-for-byte - through the interior fast sampler in
        // the middle AND the clamped sampler on the border ring.
        var canvas = new VideoFormat(16, 16, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas, CompositorSamplingMode.Bilinear);
        using var source = GradientBgra(16, 16);

        using var result = compositor.Composite([CompositorLayer.Default(source)], TimeSpan.Zero);

        Assert.True(source.Planes[0].Span.SequenceEqual(result.Planes[0].Span));
    }

    [Theory]
    [InlineData(0f)]      // pure scale
    [InlineData(0.12f)]   // scale + rotation (exercises both crop nudging and the interior ring)
    public void BilinearFastPath_MatchesPerPixelReference(float rotation)
    {
        var canvasW = 24;
        var canvasH = 20;
        var canvas = new VideoFormat(canvasW, canvasH, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas, CompositorSamplingMode.Bilinear);
        using var source = GradientBgra(12, 10);
        var transform = LayerTransform2D.Compose(
            LayerTransform2D.Scale(1.7f, 1.9f), LayerTransform2D.Rotate(rotation));

        using var result = compositor.Composite(
            [CompositorLayer.Default(source) with { Transform = transform }], TimeSpan.Zero);

        var reference = ReferenceBilinearSourceComposite(
            source, transform, canvasW, canvasH);
        var actual = result.Planes[0].Span;

        for (var i = 0; i < reference.Length; i++)
        {
            var delta = Math.Abs(actual[i] - reference[i]);
            // One rounding step of tolerance: the fast path accumulates source coordinates
            // incrementally (documented one-ulp divergence), the reference evaluates them exactly.
            Assert.True(delta <= 1, $"byte {i}: fast={actual[i]} reference={reference[i]}");
        }
    }

    /// <summary>The generic loop's bilinear Source-blend math, restated independently: per-pixel
    /// exact inverse transform, crop/source gates, clamped 4-tap sample, premultiplied write onto
    /// a transparent-black canvas.</summary>
    private static byte[] ReferenceBilinearSourceComposite(
        VideoFrame source, LayerTransform2D transform, int canvasW, int canvasH)
    {
        var srcW = source.Format.Width;
        var srcH = source.Format.Height;
        var srcStride = source.Strides[0];
        var src = source.Planes[0].Span;
        var dst = new byte[canvasW * canvasH * 4];
        var inv = transform.Invert();

        for (var dy = 0; dy < canvasH; dy++)
        for (var dx = 0; dx < canvasW; dx++)
        {
            var (sxf, syf) = inv.Apply(dx + 0.5f, dy + 0.5f);
            if (sxf < 0 || sxf >= srcW || syf < 0 || syf >= srcH)
                continue;

            var fx = sxf - 0.5f;
            var fy = syf - 0.5f;
            var x0 = (int)MathF.Floor(fx);
            var y0 = (int)MathF.Floor(fy);
            var x1 = x0 + 1;
            var y1 = y0 + 1;
            if (x1 < 0 || y1 < 0 || x0 >= srcW || y0 >= srcH)
                continue;
            var tx = fx - x0;
            var ty = fy - y0;
            var w00 = (1f - tx) * (1f - ty);
            var w10 = tx * (1f - ty);
            var w01 = (1f - tx) * ty;
            var w11 = tx * ty;
            x0 = Math.Clamp(x0, 0, srcW - 1);
            x1 = Math.Clamp(x1, 0, srcW - 1);
            y0 = Math.Clamp(y0, 0, srcH - 1);
            y1 = Math.Clamp(y1, 0, srcH - 1);
            var i00 = y0 * srcStride + x0 * 4;
            var i10 = y0 * srcStride + x1 * 4;
            var i01 = y1 * srcStride + x0 * 4;
            var i11 = y1 * srcStride + x1 * 4;

            var di = dy * canvasW * 4 + dx * 4;
            for (var c = 0; c < 4; c++)
            {
                var v = src[i00 + c] * w00 + src[i10 + c] * w10 + src[i01 + c] * w01 + src[i11 + c] * w11;
                dst[di + c] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }
        }

        return dst;
    }
}
