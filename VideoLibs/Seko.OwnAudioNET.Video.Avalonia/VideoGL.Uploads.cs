using Avalonia.OpenGL;
using Seko.OwnAudioNET.Video.OpenGL;
using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.Avalonia;

public partial class VideoGL
{
    private unsafe bool UploadRgbaFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.GetPlaneLength(0) <= 0 || frame.Width <= 0 || frame.Height <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Rgba32, frame.Width, frame.Height);
        if (TryUploadGpuPlanFromFrame(gl, frame, frame.Width, frame.Height, plan))
            return true;

        var rgbaData = GetTightlyPackedPlane(frame, 0, frame.Width * 4, frame.Height, ref _plane0Scratch);
        if (rgbaData == null)
            return false;

        return UploadRgbaPixels(gl, frame.Width, frame.Height, rgbaData);
    }

    private unsafe bool UploadRgbaPixels(GlInterface gl, int width, int height, byte[] rgbaData)
    {
        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureRgba);

        UploadTexture2D(ref _rgbaState, gl, width, height, GlConsts.GL_RGBA8, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, rgbaData);
        _textureWidth = width;
        _textureHeight = height;

        return true;
    }

    private unsafe bool UploadNv12Frame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Nv12, width, height);
        var chromaWidth = plan.Plane1.Width;
        var chromaHeight = plan.Plane1.Height;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uvPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        if (yPlane == null || uvPlane == null)
            return false;

        var pixelCount = checked(width * height);
        var rgbaLength = checked(pixelCount * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertNv12ToRgba(yPlane, uvPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv420pFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv420p, width, height);
        var chromaWidth = plan.Plane1.Width;
        var chromaHeight = plan.Plane1.Height;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth, chromaHeight, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth, chromaHeight, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var pixelCount = checked(width * height);
        var rgbaLength = checked(pixelCount * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertYuv420pToRgba(yPlane, uPlane, vPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv422pFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv422p, width, height);
        var chromaWidth = plan.Plane1.Width;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertYuv422pToRgba(yPlane, uPlane, vPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv444pFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv444p, width, height);

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, width, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, width, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertYuv444pToRgba(yPlane, uPlane, vPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv422p10leFrame(GlInterface gl, VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv422p10le, width, height);
        var chromaWidth = plan.Plane1.Width;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth * 2, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var y8 = Downscale10BitTo8Bit(yPlane, width, height, ref _plane0Scratch8);
        var u8 = Downscale10BitTo8Bit(uPlane, chromaWidth, height, ref _plane1Scratch8);
        var v8 = Downscale10BitTo8Bit(vPlane, chromaWidth, height, ref _plane2Scratch8);
        if (y8 == null || u8 == null || v8 == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];
        ConvertYuv422pToRgba(y8, u8, v8, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv420p10leFrame(GlInterface gl, VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv420p10le, width, height);
        var chromaWidth = plan.Plane1.Width;
        var chromaHeight = plan.Plane1.Height;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth * 2, chromaHeight, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var y8 = Downscale10BitTo8Bit(yPlane, width, height, ref _plane0Scratch8);
        var u8 = Downscale10BitTo8Bit(uPlane, chromaWidth, chromaHeight, ref _plane1Scratch8);
        var v8 = Downscale10BitTo8Bit(vPlane, chromaWidth, chromaHeight, ref _plane2Scratch8);
        if (y8 == null || u8 == null || v8 == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];
        ConvertYuv420pToRgba(y8, u8, v8, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv444p10leFrame(GlInterface gl, VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.Yuv444p10le, width, height);

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, width * 2, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, width * 2, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        var y8 = Downscale10BitTo8Bit(yPlane, width, height, ref _plane0Scratch8);
        var u8 = Downscale10BitTo8Bit(uPlane, width, height, ref _plane1Scratch8);
        var v8 = Downscale10BitTo8Bit(vPlane, width, height, ref _plane2Scratch8);
        if (y8 == null || u8 == null || v8 == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];
        ConvertYuv444pToRgba(y8, u8, v8, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadP010leFrame(GlInterface gl, VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(VideoPixelFormat.P010le, width, height);
        var chromaWidth = plan.Plane1.Width;
        var chromaHeight = plan.Plane1.Height;

        if (TryUploadGpuPlanFromFrame(gl, frame, width, height, plan))
            return true;

        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uvPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 4, chromaHeight, ref _plane1Scratch);
        if (yPlane == null || uvPlane == null)
            return false;

        var y8 = Downscale10BitMsbTo8Bit(yPlane, width, height, ref _plane0Scratch8);
        var uv8 = Downscale10BitMsbDualTo8Bit(uvPlane, chromaWidth, chromaHeight, ref _plane1Scratch8);
        if (y8 == null || uv8 == null)
            return false;

        var rgbaLength = checked(width * height * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];
        ConvertNv12ToRgba(y8, uv8, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private bool TryUploadGpuPlanFromFrame(
        GlInterface gl,
        VideoFrame frame,
        int width,
        int height,
        in VideoGlUploadPlanner.VideoGlGpuPlan plan)
    {
        if (!plan.IsSupported)
            return false;

        if (plan.IsYuv && !_canUseGpuYuvPath)
            return false;

        if (plan.Is10Bit && !_can16BitTextures)
            return false;

        byte[]? plane0 = null;
        byte[]? plane1 = null;
        byte[]? plane2 = null;
        var stride0 = 0;
        var stride1 = 0;
        var stride2 = 0;
        var usedStridedUpload = false;

        for (var i = 0; i < plan.PlaneCount; i++)
        {
            var descriptor = GetPlaneDescriptor(plan, i);
            ref var scratch = ref GetPlaneScratch(descriptor.PlaneIndex);
            var plane = GetPlaneUploadBytes(
                frame,
                descriptor.PlaneIndex,
                descriptor.RowBytes,
                descriptor.Height,
                allowStridedUpload: _pixelStoreI != null,
                ref scratch,
                out var sourceStrideBytes);

            if (plane == null)
                return false;

            if (i == 0)
            {
                plane0 = plane;
                stride0 = sourceStrideBytes;
            }
            else if (i == 1)
            {
                plane1 = plane;
                stride1 = sourceStrideBytes;
            }
            else
            {
                plane2 = plane;
                stride2 = sourceStrideBytes;
            }
        }

        for (var i = 0; i < plan.PlaneCount; i++)
        {
            var descriptor = GetPlaneDescriptor(plan, i);
            var data = i == 0 ? plane0 : i == 1 ? plane1 : plane2;
            var sourceStrideBytes = i == 0 ? stride0 : i == 1 ? stride1 : stride2;
            if (data == null)
                return false;

            Interlocked.Increment(ref _diagUploadPlaneCount);

            var textureUnit = GetTextureUnit(descriptor.Slot);
            var textureId = GetTextureId(descriptor.Slot);
            gl.ActiveTexture(textureUnit);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, textureId);

            if (_pixelStoreI != null)
            {
                _pixelStoreI(GlUnpackAlignment, 1);
                var bytesPerPixel = descriptor.Width > 0 ? Math.Max(1, descriptor.RowBytes / descriptor.Width) : 1;
                var rowLengthPixels = sourceStrideBytes > descriptor.RowBytes
                    ? Math.Max(0, sourceStrideBytes / bytesPerPixel)
                    : 0;
                _pixelStoreI(GlUnpackRowLength, rowLengthPixels);
                if (rowLengthPixels > 0)
                {
                    usedStridedUpload = true;
                    Interlocked.Increment(ref _diagStridedUploadPlaneCount);
                }
            }

            ref var state = ref GetTextureState(descriptor.Slot);
            UploadTexture2D(
                ref state,
                gl,
                descriptor.Width,
                descriptor.Height,
                descriptor.InternalFormat,
                descriptor.Format,
                descriptor.Type,
                data);

            if (_pixelStoreI != null)
            {
                _pixelStoreI(GlUnpackRowLength, 0);
                _pixelStoreI(GlUnpackAlignment, 4);
            }
        }

        if (usedStridedUpload)
            Interlocked.Increment(ref _diagStridedUploadFrameCount);

        if (plan.IsYuv && VideoGlUploadPlanner.IsSemiPlanar(plan.YuvMode))
        {
            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
        }

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        _textureWidth = width;
        _textureHeight = height;
        _useYuvProgramThisFrame = plan.IsYuv;
        _yuvPixelFormatThisFrame = plan.YuvMode;
        return true;
    }

    private static VideoGlUploadPlanner.VideoGlPlanePlan GetPlaneDescriptor(
        in VideoGlUploadPlanner.VideoGlGpuPlan plan,
        int index)
        => index switch
        {
            0 => plan.Plane0,
            1 => plan.Plane1,
            2 => plan.Plane2,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

    private ref byte[]? GetPlaneScratch(int planeIndex)
    {
        switch (planeIndex)
        {
            case 0: return ref _plane0Scratch;
            case 1: return ref _plane1Scratch;
            case 2: return ref _plane2Scratch;
            default: throw new ArgumentOutOfRangeException(nameof(planeIndex));
        }
    }

    private int GetTextureUnit(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
        => slot switch
        {
            VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba => GlConsts.GL_TEXTURE0,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Y => GlConsts.GL_TEXTURE0,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Uv => GlTexture1,
            VideoGlUploadPlanner.VideoGlPlaneSlot.U => GlTexture1,
            VideoGlUploadPlanner.VideoGlPlaneSlot.V => GlTexture2,
            _ => GlConsts.GL_TEXTURE0
        };

    private int GetTextureId(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
        => slot switch
        {
            VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba => _textureRgba,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Y => _textureY,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Uv => _textureUv,
            VideoGlUploadPlanner.VideoGlPlaneSlot.U => _textureU,
            VideoGlUploadPlanner.VideoGlPlaneSlot.V => _textureV,
            _ => _textureRgba
        };

    private ref TextureUploadState GetTextureState(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
    {
        switch (slot)
        {
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba: return ref _rgbaState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Y: return ref _yState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Uv: return ref _uvState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.U: return ref _uState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.V: return ref _vState;
            default: throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void UploadTexture2D(ref TextureUploadState state, GlInterface gl, int width, int height, int internalFormat, int format, int type, byte[] data)
    {
        VideoGlTextureUploadOrchestrator.UploadTexture2D(
            ref state,
            width,
            height,
            internalFormat,
            format,
            type,
            data,
            (ifmt, w, h, fmt, t, pixels) => gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, ifmt, w, h, 0, fmt, t, pixels),
            _texSubImage2D == null
                ? null
                : (w, h, fmt, t, pixels) => _texSubImage2D(GlConsts.GL_TEXTURE_2D, 0, 0, 0, w, h, fmt, t, pixels));
    }

}

