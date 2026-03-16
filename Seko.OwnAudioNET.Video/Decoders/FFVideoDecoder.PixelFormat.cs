using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Seko.OwnAudioNET.Video.Decoders;

public unsafe partial class FFVideoDecoder
{
	private static VideoPixelFormat[] ResolvePreferredOutputFormats(IReadOnlyList<VideoPixelFormat>? preferred)
	{
		if (preferred == null || preferred.Count == 0)
			return [VideoPixelFormat.Rgba32];

		var deduped = new List<VideoPixelFormat>(preferred.Count);
		foreach (var format in preferred)
		{
			if (!IsSupportedOutputFormat(format))
				continue;

			if (!deduped.Contains(format))
				deduped.Add(format);
		}

		return deduped.Count > 0 ? deduped.ToArray() : [VideoPixelFormat.Rgba32];
	}

	private static bool IsSupportedOutputFormat(VideoPixelFormat format)
	{
		return format is VideoPixelFormat.Rgba32 or VideoPixelFormat.Nv12 or VideoPixelFormat.Yuv420p;
	}

	private VideoPixelFormat SelectOutputPixelFormat(AVFrame* sourceFrame)
	{
		var sourcePixelFormat = (AVPixelFormat)sourceFrame->format;
		if (_preferSourcePixelFormatWhenSupported &&
			TryMapAvPixelFormat(sourcePixelFormat, out var mappedSourceFormat) &&
			Array.IndexOf(_preferredOutputFormats, mappedSourceFormat) >= 0)
		{
			return mappedSourceFormat;
		}

		if (_preferLowestConversionCost)
		{
			var selected = _preferredOutputFormats[0];
			var selectedCost = EstimateConversionCost(sourcePixelFormat, selected);
			for (var i = 1; i < _preferredOutputFormats.Length; i++)
			{
				var candidate = _preferredOutputFormats[i];
				var candidateCost = EstimateConversionCost(sourcePixelFormat, candidate);
				if (candidateCost < selectedCost)
				{
					selected = candidate;
					selectedCost = candidateCost;
				}
			}

			return selected;
		}

		foreach (var preferred in _preferredOutputFormats)
		{
			if (IsSupportedOutputFormat(preferred))
				return preferred;
		}

		return VideoPixelFormat.Rgba32;
	}

	private static int EstimateConversionCost(AVPixelFormat sourcePixelFormat, VideoPixelFormat outputFormat)
	{
		if (TryMapAvPixelFormat(sourcePixelFormat, out var mapped) && mapped == outputFormat)
			return 0;

		return outputFormat switch
		{
			VideoPixelFormat.Yuv420p => sourcePixelFormat switch
			{
				AVPixelFormat.AV_PIX_FMT_NV12 => 1,
				AVPixelFormat.AV_PIX_FMT_YUV422P => 2,
				AVPixelFormat.AV_PIX_FMT_YUV422P10LE => 2,
				AVPixelFormat.AV_PIX_FMT_YUV422P10BE => 2,
				AVPixelFormat.AV_PIX_FMT_P010LE => 2,
				AVPixelFormat.AV_PIX_FMT_P010BE => 2,
				AVPixelFormat.AV_PIX_FMT_RGBA => 6,
				_ => 4
			},
			VideoPixelFormat.Nv12 => sourcePixelFormat switch
			{
				AVPixelFormat.AV_PIX_FMT_YUV420P => 1,
				AVPixelFormat.AV_PIX_FMT_YUV422P => 3,
				AVPixelFormat.AV_PIX_FMT_YUV422P10LE => 3,
				AVPixelFormat.AV_PIX_FMT_YUV422P10BE => 3,
				AVPixelFormat.AV_PIX_FMT_P010LE => 1,
				AVPixelFormat.AV_PIX_FMT_P010BE => 1,
				AVPixelFormat.AV_PIX_FMT_RGBA => 6,
				_ => 4
			},
			VideoPixelFormat.Rgba32 => sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_RGBA ? 0 : 8,
			_ => 10
		};
	}

	private static AVPixelFormat ToAvPixelFormat(VideoPixelFormat outputFormat)
	{
		return outputFormat switch
		{
			VideoPixelFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
			VideoPixelFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
			VideoPixelFormat.Nv12 => AVPixelFormat.AV_PIX_FMT_NV12,
			_ => AVPixelFormat.AV_PIX_FMT_RGBA
		};
	}

	private static bool TryMapAvPixelFormat(AVPixelFormat source, out VideoPixelFormat pixelFormat)
	{
		switch (source)
		{
			case AVPixelFormat.AV_PIX_FMT_RGBA:
				pixelFormat = VideoPixelFormat.Rgba32;
				return true;
			case AVPixelFormat.AV_PIX_FMT_YUV420P:
				pixelFormat = VideoPixelFormat.Yuv420p;
				return true;
			case AVPixelFormat.AV_PIX_FMT_NV12:
				pixelFormat = VideoPixelFormat.Nv12;
				return true;
			default:
				pixelFormat = default;
				return false;
		}
	}

	private static bool CanCopyFrameDirectly(AVFrame* frame, VideoPixelFormat outputFormat)
	{
		var sourcePixelFormat = (AVPixelFormat)frame->format;
		if (!TryMapAvPixelFormat(sourcePixelFormat, out var sourceFormat))
			return false;

		if (sourceFormat != outputFormat)
			return false;

		return outputFormat switch
		{
			VideoPixelFormat.Rgba32 => frame->data[0] != null && frame->linesize[0] > 0,
			VideoPixelFormat.Nv12 => frame->data[0] != null && frame->data[1] != null && frame->linesize[0] > 0 && frame->linesize[1] > 0,
			VideoPixelFormat.Yuv420p => frame->data[0] != null && frame->data[1] != null && frame->data[2] != null &&
										frame->linesize[0] > 0 && frame->linesize[1] > 0 && frame->linesize[2] > 0,
			_ => false
		};
	}

	private bool TryCopyFrameToVideoFrame(AVFrame* sourceFrame, VideoPixelFormat outputFormat, double ptsSeconds, out VideoFrame frame, out string? error)
	{
		frame = null!;
		error = null;

		if (sourceFrame->width <= 0 || sourceFrame->height <= 0)
		{
			error = "Decoded frame has invalid dimensions.";
			return false;
		}

		var width = sourceFrame->width;
		var height = sourceFrame->height;

		switch (outputFormat)
		{
			case VideoPixelFormat.Rgba32:
			{
				var stride = sourceFrame->linesize[0];
				if (sourceFrame->data[0] == null || stride <= 0)
				{
					error = "RGBA frame has invalid source pointers.";
					return false;
				}

				var bytes = checked(stride * height);
				frame = VideoFrame.CreatePooledRgba32(bytes, width, height, stride, ptsSeconds);
				CopyPlane(sourceFrame->data[0], frame.GetPlaneData(0), frame.GetPlaneLength(0));
				return true;
			}
			case VideoPixelFormat.Nv12:
			{
				var yStride = sourceFrame->linesize[0];
				var uvStride = sourceFrame->linesize[1];
				if (sourceFrame->data[0] == null || sourceFrame->data[1] == null || yStride <= 0 || uvStride <= 0)
				{
					error = "NV12 frame has invalid source pointers.";
					return false;
				}

				var chromaHeight = (height + 1) / 2;
				var yBytes = checked(yStride * height);
				var uvBytes = checked(uvStride * chromaHeight);
				frame = VideoFrame.CreatePooledNv12(yBytes, uvBytes, width, height, yStride, uvStride, ptsSeconds);
				CopyPlane(sourceFrame->data[0], frame.GetPlaneData(0), frame.GetPlaneLength(0));
				CopyPlane(sourceFrame->data[1], frame.GetPlaneData(1), frame.GetPlaneLength(1));
				return true;
			}
			case VideoPixelFormat.Yuv420p:
			{
				var yStride = sourceFrame->linesize[0];
				var uStride = sourceFrame->linesize[1];
				var vStride = sourceFrame->linesize[2];
				if (sourceFrame->data[0] == null || sourceFrame->data[1] == null || sourceFrame->data[2] == null ||
					yStride <= 0 || uStride <= 0 || vStride <= 0)
				{
					error = "YUV420p frame has invalid source pointers.";
					return false;
				}

				var chromaHeight = (height + 1) / 2;
				var yBytes = checked(yStride * height);
				var uBytes = checked(uStride * chromaHeight);
				var vBytes = checked(vStride * chromaHeight);
				frame = VideoFrame.CreatePooledYuv420p(yBytes, uBytes, vBytes, width, height, yStride, uStride, vStride, ptsSeconds);
				CopyPlane(sourceFrame->data[0], frame.GetPlaneData(0), frame.GetPlaneLength(0));
				CopyPlane(sourceFrame->data[1], frame.GetPlaneData(1), frame.GetPlaneLength(1));
				CopyPlane(sourceFrame->data[2], frame.GetPlaneData(2), frame.GetPlaneLength(2));
				return true;
			}
			default:
				error = $"Unsupported output format {outputFormat}.";
				return false;
		}
	}

	private static void CopyPlane(byte* src, byte[] destination, int bytesToCopy)
	{
		if (bytesToCopy <= 0)
			return;

		fixed (byte* dst = &MemoryMarshal.GetArrayDataReference(destination))
		{
			Buffer.MemoryCopy(src, dst, destination.Length, bytesToCopy);
		}
	}
}

