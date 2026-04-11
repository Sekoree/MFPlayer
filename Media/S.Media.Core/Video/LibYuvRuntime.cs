using System.Runtime.InteropServices;

namespace S.Media.Core.Video;

internal static class LibYuvRuntime
{
    private delegate int ArgbShuffleDelegate(
        nint srcArgb,
        int srcStrideArgb,
        nint dstArgb,
        int dstStrideArgb,
        nint shuffler,
        int width,
        int height);

    private delegate int Nv12ToArgbDelegate(
        nint srcY,
        int srcStrideY,
        nint srcUv,
        int srcStrideUv,
        nint dstArgb,
        int dstStrideArgb,
        int width,
        int height);

    private delegate int I420ToArgbDelegate(
        nint srcY,
        int srcStrideY,
        nint srcU,
        int srcStrideU,
        nint srcV,
        int srcStrideV,
        nint dstArgb,
        int dstStrideArgb,
        int width,
        int height);

    private delegate int UyvyToArgbDelegate(
        nint srcUyvy,
        int srcStrideUyvy,
        nint dstArgb,
        int dstStrideArgb,
        int width,
        int height);

    /// <summary>
    /// Delegate for libyuv I210ToARGB / I210ToABGR.
    /// Converts 10-bit planar 4:2:2 (I210 / yuv422p10le) to packed ARGB or ABGR.
    /// Strides are in bytes; each src plane pointer is a uint16_t* cast to nint.
    /// </summary>
    private delegate int I210ToArgbDelegate(
        nint srcY,
        int srcStrideY,
        nint srcU,
        int srcStrideU,
        nint srcV,
        int srcStrideV,
        nint dstArgb,
        int dstStrideArgb,
        int width,
        int height);

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static bool _available;
    private static volatile bool _enabled = true;
    private static nint _libraryHandle;
    private static ArgbShuffleDelegate? _argbShuffle;
    private static Nv12ToArgbDelegate? _nv12ToArgb;
    private static Nv12ToArgbDelegate? _nv12ToAbgr;
    private static I420ToArgbDelegate? _i420ToArgb;
    private static I420ToArgbDelegate? _i420ToAbgr;
    private static UyvyToArgbDelegate? _uyvyToArgb;
    private static UyvyToArgbDelegate? _uyvyToAbgr;
    private static I210ToArgbDelegate? _i210ToArgb;
    private static I210ToArgbDelegate? _i210ToAbgr;

    // ARGBShuffle mask to convert BGRA <-> RGBA.
    // dst[0]=src[2], dst[1]=src[1], dst[2]=src[0], dst[3]=src[3]
    private static readonly byte[] ShuffleMaskBgraRgba = [2, 1, 0, 3];

    internal static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _enabled && _available;
        }
    }

    internal static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    internal static bool TrySwapBgraRgba(ReadOnlyMemory<byte> source, byte[] destination, int width, int height)
    {
        EnsureLoaded();
        if (!_enabled)
            return false;
        if (!_available || _argbShuffle == null)
            return false;

        if (!MemoryMarshal.TryGetArray(source, out ArraySegment<byte> srcSeg) || srcSeg.Array == null)
            return false;

        int stride = width * 4;
        if (width <= 0 || height <= 0 || destination.Length < stride * height)
            return false;

        var srcHandle = default(GCHandle);
        var dstHandle = default(GCHandle);
        var maskHandle = default(GCHandle);
        try
        {
            srcHandle = GCHandle.Alloc(srcSeg.Array, GCHandleType.Pinned);
            dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            maskHandle = GCHandle.Alloc(ShuffleMaskBgraRgba, GCHandleType.Pinned);

            nint srcPtr = srcHandle.AddrOfPinnedObject() + srcSeg.Offset;
            nint dstPtr = dstHandle.AddrOfPinnedObject();
            nint maskPtr = maskHandle.AddrOfPinnedObject();

            int ret = _argbShuffle(srcPtr, stride, dstPtr, stride, maskPtr, width, height);
            return ret == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (dstHandle.IsAllocated) dstHandle.Free();
            if (maskHandle.IsAllocated) maskHandle.Free();
        }
    }

    internal static bool TryConvertNv12(ReadOnlyMemory<byte> source, byte[] destination, int width, int height, bool dstRgba)
    {
        EnsureLoaded();
        if (!_enabled)
            return false;
        var converter = dstRgba ? _nv12ToAbgr : _nv12ToArgb;
        if (!_available || converter == null)
            return false;

        if (!MemoryMarshal.TryGetArray(source, out ArraySegment<byte> srcSeg) || srcSeg.Array == null)
            return false;

        int ySize = width * height;
        int uvSize = width * ((height + 1) / 2);
        int srcRequired = ySize + uvSize;
        int dstRequired = width * height * 4;

        if (width <= 0 || height <= 0 || srcSeg.Count < srcRequired || destination.Length < dstRequired)
            return false;

        return TryRunPackedPlanar2(converter, srcSeg, ySize, width, width, destination, width, height);
    }

    internal static bool TryConvertI420(ReadOnlyMemory<byte> source, byte[] destination, int width, int height, bool dstRgba)
    {
        EnsureLoaded();
        if (!_enabled)
            return false;
        var converter = dstRgba ? _i420ToAbgr : _i420ToArgb;
        if (!_available || converter == null)
            return false;

        if (!MemoryMarshal.TryGetArray(source, out ArraySegment<byte> srcSeg) || srcSeg.Array == null)
            return false;

        int halfWidth = (width + 1) / 2;
        int halfHeight = (height + 1) / 2;
        int ySize = width * height;
        int uSize = halfWidth * halfHeight;
        int srcRequired = ySize + uSize + uSize;
        int dstRequired = width * height * 4;

        if (width <= 0 || height <= 0 || srcSeg.Count < srcRequired || destination.Length < dstRequired)
            return false;

        var srcHandle = default(GCHandle);
        var dstHandle = default(GCHandle);
        try
        {
            srcHandle = GCHandle.Alloc(srcSeg.Array, GCHandleType.Pinned);
            dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            nint basePtr = srcHandle.AddrOfPinnedObject() + srcSeg.Offset;
            nint srcY = basePtr;
            nint srcU = basePtr + ySize;
            nint srcV = srcU + uSize;
            nint dst = dstHandle.AddrOfPinnedObject();

            int ret = converter(srcY, width, srcU, halfWidth, srcV, halfWidth, dst, width * 4, width, height);
            return ret == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (dstHandle.IsAllocated) dstHandle.Free();
        }
    }

    internal static bool TryConvertUyvy(ReadOnlyMemory<byte> source, byte[] destination, int width, int height, bool dstRgba)
    {
        EnsureLoaded();
        if (!_enabled)
            return false;
        var converter = dstRgba ? _uyvyToAbgr : _uyvyToArgb;
        if (!_available || converter == null)
            return false;

        if (!MemoryMarshal.TryGetArray(source, out ArraySegment<byte> srcSeg) || srcSeg.Array == null)
            return false;

        int srcRequired = width * height * 2;
        int dstRequired = width * height * 4;
        if (width <= 0 || height <= 0 || srcSeg.Count < srcRequired || destination.Length < dstRequired)
            return false;

        var srcHandle = default(GCHandle);
        var dstHandle = default(GCHandle);
        try
        {
            srcHandle = GCHandle.Alloc(srcSeg.Array, GCHandleType.Pinned);
            dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            nint srcPtr = srcHandle.AddrOfPinnedObject() + srcSeg.Offset;
            nint dstPtr = dstHandle.AddrOfPinnedObject();
            int ret = converter(srcPtr, width * 2, dstPtr, width * 4, width, height);
            return ret == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (dstHandle.IsAllocated) dstHandle.Free();
        }
    }

    /// <summary>
    /// Converts 10-bit planar 4:2:2 (Yuv422p10 / I210 / yuv422p10le) to BGRA32 or RGBA32.
    /// Data layout: Y plane (width*2*height bytes), U plane ((width/2)*2*height bytes),
    /// V plane ((width/2)*2*height bytes) — each sample stored as 16-bit LE.
    /// Uses libyuv I210ToARGB / I210ToABGR which accept byte strides for 16-bit planes.
    /// </summary>
    internal static bool TryConvertI210(ReadOnlyMemory<byte> source, byte[] destination, int width, int height, bool dstRgba)
    {
        EnsureLoaded();
        if (!_enabled)
            return false;
        var converter = dstRgba ? _i210ToAbgr : _i210ToArgb;
        if (!_available || converter == null)
            return false;

        if (!MemoryMarshal.TryGetArray(source, out ArraySegment<byte> srcSeg) || srcSeg.Array == null)
            return false;

        // Each Y sample is 2 bytes; each chroma sample is 2 bytes at half horizontal resolution.
        int yStride  = width * 2;   // bytes per row for Y plane
        int uvStride = width;       // bytes per row for U/V plane: (width/2) * 2 = width
        int ySize    = yStride  * height;
        int uvSize   = uvStride * height;
        int srcRequired = ySize + uvSize * 2;
        int dstRequired = width * height * 4;

        if (width <= 0 || height <= 0 || srcSeg.Count < srcRequired || destination.Length < dstRequired)
            return false;

        var srcHandle = default(GCHandle);
        var dstHandle = default(GCHandle);
        try
        {
            srcHandle = GCHandle.Alloc(srcSeg.Array, GCHandleType.Pinned);
            dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            nint basePtr = srcHandle.AddrOfPinnedObject() + srcSeg.Offset;
            nint srcY = basePtr;
            nint srcU = basePtr + ySize;
            nint srcV = basePtr + ySize + uvSize;
            nint dst  = dstHandle.AddrOfPinnedObject();

            int ret = converter(srcY, yStride, srcU, uvStride, srcV, uvStride, dst, width * 4, width, height);
            return ret == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (dstHandle.IsAllocated) dstHandle.Free();
        }
    }

    private static void EnsureLoaded()
    {
        if (_initialised)
            return;

        lock (Gate)
        {
            if (_initialised)
                return;

            TryLoadLibrary();
            _initialised = true;
        }
    }

    private static void TryLoadLibrary()
    {
        foreach (var name in GetLibraryCandidates())
        {
            try
            {
                if (!NativeLibrary.TryLoad(name, out _libraryHandle))
                    continue;

                if (!NativeLibrary.TryGetExport(_libraryHandle, "ARGBShuffle", out var shuffleExport))
                    shuffleExport = nint.Zero;

                if (shuffleExport != nint.Zero)
                    _argbShuffle = Marshal.GetDelegateForFunctionPointer<ArgbShuffleDelegate>(shuffleExport);

                _nv12ToArgb = TryGetDelegate<Nv12ToArgbDelegate>("NV12ToARGB");
                _nv12ToAbgr = TryGetDelegate<Nv12ToArgbDelegate>("NV12ToABGR");
                _i420ToArgb = TryGetDelegate<I420ToArgbDelegate>("I420ToARGB");
                _i420ToAbgr = TryGetDelegate<I420ToArgbDelegate>("I420ToABGR");
                _uyvyToArgb = TryGetDelegate<UyvyToArgbDelegate>("UYVYToARGB");
                _uyvyToAbgr = TryGetDelegate<UyvyToArgbDelegate>("UYVYToABGR");
                _i210ToArgb = TryGetDelegate<I210ToArgbDelegate>("I210ToARGB");
                _i210ToAbgr = TryGetDelegate<I210ToArgbDelegate>("I210ToABGR");

                _available = _argbShuffle != null || _nv12ToArgb != null || _nv12ToAbgr != null ||
                             _i420ToArgb != null || _i420ToAbgr != null ||
                             _uyvyToArgb != null || _uyvyToAbgr != null ||
                             _i210ToArgb != null || _i210ToAbgr != null;
                if (!_available)
                    continue;

                return;
            }
            catch
            {
                // Keep trying other candidates.
            }
        }
    }

    private static IEnumerable<string> GetLibraryCandidates()
    {
        if (OperatingSystem.IsWindows())
            return ["yuv.dll", "libyuv.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libyuv.dylib"];
        return ["libyuv.so", "libyuv.so.0"];
    }

    private static T? TryGetDelegate<T>(string exportName) where T : class
    {
        if (_libraryHandle == nint.Zero)
            return null;

        if (!NativeLibrary.TryGetExport(_libraryHandle, exportName, out var ptr) || ptr == nint.Zero)
            return null;

        return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
    }

    private static bool TryRunPackedPlanar2(
        Nv12ToArgbDelegate converter,
        ArraySegment<byte> srcSeg,
        int secondPlaneOffset,
        int strideY,
        int strideUv,
        byte[] destination,
        int width,
        int height)
    {
        var srcHandle = default(GCHandle);
        var dstHandle = default(GCHandle);
        try
        {
            srcHandle = GCHandle.Alloc(srcSeg.Array!, GCHandleType.Pinned);
            dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            nint basePtr = srcHandle.AddrOfPinnedObject() + srcSeg.Offset;
            nint srcY = basePtr;
            nint srcUv = basePtr + secondPlaneOffset;
            nint dst = dstHandle.AddrOfPinnedObject();

            int ret = converter(srcY, strideY, srcUv, strideUv, dst, width * 4, width, height);
            return ret == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (dstHandle.IsAllocated) dstHandle.Free();
        }
    }
}
