using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Seko.OwnAudioNET.Video.Avalonia;

public partial class VideoGL
{
    private unsafe bool UploadRgbaFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.GetPlaneLength(0) <= 0 || frame.Width <= 0 || frame.Height <= 0)
            return false;

        var rgbaData = GetTightlyPackedPlane(frame, 0, frame.Width * 4, frame.Height, ref _plane0Scratch);
        if (rgbaData == null)
            return false;

        return UploadRgbaPixels(gl, frame.Width, frame.Height, rgbaData);
    }

    private unsafe bool UploadRgbaPixels(GlInterface gl, int width, int height, byte[] rgbaData)
    {
        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureRgba);

        var internalFormat = GlConsts.GL_RGBA8;
        var format = GlConsts.GL_RGBA;

        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(rgbaData))
        {
            var pixels = (nint)ptr;

            if (!_rgbaTextureInitialized || _textureWidth != width || _textureHeight != height)
            {
                _textureWidth = width;
                _textureHeight = height;
                _rgbaTextureInitialized = true;

                gl.TexImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    internalFormat,
                    _textureWidth,
                    _textureHeight,
                    0,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    nint.Zero);
            }

            if (_texSubImage2D != null)
            {
                _texSubImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    0,
                    0,
                    width,
                    height,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    pixels);
            }
            else
            {
                gl.TexImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    internalFormat,
                    width,
                    height,
                    0,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    pixels);
            }
        }

        return true;
    }

    private unsafe bool UploadNv12Frame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uvPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        if (yPlane == null || uvPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            UploadDualChannelTexture(gl, chromaWidth, chromaHeight, uvPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 1;
            _rgbaTextureInitialized = false;
            return true;
        }

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
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth, chromaHeight, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth, chromaHeight, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadSingleChannelTexture(gl, chromaWidth, chromaHeight, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadSingleChannelTexture(gl, chromaWidth, chromaHeight, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 2;
            _rgbaTextureInitialized = false;
            return true;
        }

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
        var chromaWidth = (width + 1) / 2;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadSingleChannelTexture(gl, chromaWidth, height, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadSingleChannelTexture(gl, chromaWidth, height, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 2;
            _rgbaTextureInitialized = false;
            return true;
        }

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

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, width, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, width, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadSingleChannelTexture(gl, width, height, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadSingleChannelTexture(gl, width, height, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 2;
            _rgbaTextureInitialized = false;
            return true;
        }

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

        var chromaWidth = (width + 1) / 2;
        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth * 2, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath && _can16BitTextures)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadR16Texture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadR16Texture(gl, chromaWidth, height, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadR16Texture(gl, chromaWidth, height, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 4;
            _rgbaTextureInitialized = false;
            return true;
        }

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

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth * 2, chromaHeight, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath && _can16BitTextures)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadR16Texture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadR16Texture(gl, chromaWidth, chromaHeight, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadR16Texture(gl, chromaWidth, chromaHeight, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 4;
            _rgbaTextureInitialized = false;
            return true;
        }

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

        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, width * 2, height, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, width * 2, height, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath && _can16BitTextures)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadR16Texture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadR16Texture(gl, width, height, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadR16Texture(gl, width, height, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 4;
            _rgbaTextureInitialized = false;
            return true;
        }

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

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var yPlane = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uvPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 4, chromaHeight, ref _plane1Scratch);
        if (yPlane == null || uvPlane == null)
            return false;

        if (_canUseGpuYuvPath && _can16BitTextures)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadR16Texture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            UploadRg16Texture(gl, chromaWidth, chromaHeight, uvPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 3;
            _rgbaTextureInitialized = false;
            return true;
        }

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

    private unsafe void UploadSingleChannelTexture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlR8, width, height, 0, GlRed, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
        }
    }

    private unsafe void UploadDualChannelTexture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlRg8, width, height, 0, GlRg, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
        }
    }

    private unsafe void UploadR16Texture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlR16, width, height, 0, GlRed, GlUnsignedShort, (nint)ptr);
        }
    }

    private unsafe void UploadRg16Texture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlRg16, width, height, 0, GlRg, GlUnsignedShort, (nint)ptr);
        }
    }
}

