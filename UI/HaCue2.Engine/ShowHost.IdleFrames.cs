using HaCue2.Core.Media;
using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Video;

namespace HaCue2.Engine;

public sealed partial class ShowHost
{
    /// <summary>Decodes authored stills once and transfers their frames to the composition runtime.</summary>
    private async Task ApplyIdleFramesAsync(HaCueProject project, string? projectPath)
    {
        foreach (var composition in project.Compositions)
        {
            var frame = await DecodeIdleAsync(
                project,
                composition.IdleImagePath,
                projectPath,
                $"“{composition.Name}” idle image").ConfigureAwait(false);
            await _session.SetCompositionIdleFrameAsync(composition.Id.ToString(), frame).ConfigureAwait(false);
        }

        foreach (var output in project.VideoOutputs)
        {
            if (output.CompositionId is not { } compositionId
                || _screens.Open.FirstOrDefault(open => open.Id == output.Id) is not { } open)
                continue;

            var frame = await DecodeIdleAsync(
                project,
                output.IdleFallbackPath,
                projectPath,
                $"“{output.Name}” idle fallback").ConfigureAwait(false);
            await _session.SetOutputIdleFrameAsync(
                compositionId.ToString(), open.OutputId, frame).ConfigureAwait(false);
        }
    }

    private async Task<VideoFrame?> DecodeIdleAsync(
        HaCueProject project,
        string authoredPath,
        string? projectPath,
        string label)
    {
        if (string.IsNullOrWhiteSpace(authoredPath))
            return null;

        try
        {
            var path = MediaPaths.Resolve(project, authoredPath, projectPath);
            return await Task.Run(() =>
            {
                using var decoder = VideoFileDecoder.Open(path);
                decoder.SelectOutputFormat(PixelFormat.Bgra32);
                if (!decoder.TryReadNextFrame(out var decoded))
                    throw new InvalidDataException("the image produced no video frame");
                using (decoded)
                    return VideoFrameCpuClone.DuplicateCpuBacking(decoded, decoded.ColorTransferHint);
            }, _life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or ArgumentException)
        {
            Report($"{label} could not be loaded — {failure.Message}");
            return null;
        }
    }
}
