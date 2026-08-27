using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Video;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>The `range=` receive override: NDI carries no range metadata, so unpack stamps
/// full-range BT.709 by default and honours an explicit override for limited-range senders.</summary>
public sealed class NDIVideoFrameUnpackRangeTests
{
    private static VideoFrame Unpack(VideoColorRange? overrideRange)
    {
        const int width = 4;
        const int height = 2;
        var payload = new byte[width * 2 * height];
        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyvy,
                FrameRateN = 30,
                FrameRateD = 1,
                LineStrideInBytes = width * 2,
                PData = pin.AddrOfPinnedObject(),
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame, overrideRange));
            Assert.NotNull(frame);
            return frame!;
        }
        finally
        {
            pin.Free();
        }
    }

    [Fact]
    public void Default_StampsFullRange()
    {
        using var frame = Unpack(overrideRange: null);
        Assert.Equal(VideoColorRange.Full, frame.ColorRange);
    }

    [Fact]
    public void LimitedOverride_StampsLimitedRange()
    {
        using var frame = Unpack(VideoColorRange.Limited);
        Assert.Equal(VideoColorRange.Limited, frame.ColorRange);
    }
}
