using HaCue2.Core.Media;
using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Video;

namespace HaCue2.Engine;

public sealed partial class ShowHost
{
    /// <summary>
    /// What each surface's idle frame was last built FROM, so an unchanged one is not rebuilt.
    /// </summary>
    /// <remarks>
    /// Every edit reloads the document, and reloads are debounced at 300 ms rather than rare - so
    /// without this, typing a cue label re-decodes every composition's idle image FROM DISK three times
    /// a second, and re-allocates a canvas-sized buffer per output while it does. The signature is the
    /// authored path plus the size, which is the whole of what the frame is built from.
    /// </remarks>
    private readonly Dictionary<string, string> _idleSignatures = [];

    /// <summary>Decodes authored stills once and transfers their frames to the composition runtime.</summary>
    private async Task ApplyIdleFramesAsync(HaCueProject project, string? projectPath)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var composition in project.Compositions)
        {
            var key = $"c:{composition.Id}";
            // The FIT is part of the signature: changing how the slate fills the canvas rebuilds the
            // frame, exactly as changing the picture does.
            var signature = $"{composition.IdleImagePath}|{composition.IdleImageFit}|{Canvas(composition)}";
            wanted.Add(key);

            if (!Changed(key, signature))
                continue;

            var frame = await DecodeIdleAsync(
                project,
                composition.IdleImagePath,
                projectPath,
                $"“{composition.Name}” idle image").ConfigureAwait(false);

            if (frame is not null)
                frame = IdleFrames.Fitted(
                    frame, composition.Width, composition.Height, composition.IdleImageFit);

            await _session.SetCompositionIdleFrameAsync(composition.Id.ToString(), frame).ConfigureAwait(false);
        }

        foreach (var output in project.VideoOutputs)
        {
            if (output.CompositionId is not { } compositionId
                || project.Compositions.FirstOrDefault(item => item.Id == compositionId) is not { } canvas
                || _screens.Open.FirstOrDefault(open => open.Id == output.Id) is not { } open)
                continue;

            var key = $"o:{output.Id}";
            var signature =
                $"{output.IdleFallbackPath}|{output.IdleFallbackFit}|{compositionId}|{Canvas(canvas)}";
            wanted.Add(key);

            if (!Changed(key, signature))
                continue;

            var frame = await DecodeIdleAsync(
                project,
                output.IdleFallbackPath,
                projectPath,
                $"“{output.Name}” idle fallback").ConfigureAwait(false);

            if (frame is not null)
                frame = IdleFrames.Fitted(frame, canvas.Width, canvas.Height, output.IdleFallbackFit);

            // Black as the last resort, so an output ALWAYS has something to show. Without it a
            // composition with nothing playing submits no frames at all, and a sink that is configured
            // by its first submit never opens its window - an operator who has just added a projector
            // sees no window and no error, and concludes the output does not work. The composition
            // pane has always labelled an empty idle path "black"; this is what makes that true.
            await _session.SetOutputIdleFrameAsync(
                compositionId.ToString(),
                open.OutputId,
                frame ?? IdleFrames.Black(canvas.Width, canvas.Height)).ConfigureAwait(false);
        }

        // A surface that is gone must lose its signature, or re-adding an output with the same id - an
        // undo, most obviously - would find its idle "unchanged" and never submit the frame that opens
        // its window.
        foreach (var stale in _idleSignatures.Keys.Where(key => !wanted.Contains(key)).ToList())
            _idleSignatures.Remove(stale);
    }

    /// <summary>
    /// The canvas properties a reload rebuilds a composition for.
    /// </summary>
    /// <remarks>
    /// Size AND rate, because those are exactly what the session keys "unchanged" on: a composition
    /// whose rate changed is rebuilt, taking every attached output's idle frame with it. Leaving rate
    /// out of the signature would call that unchanged and never re-apply - so an operator who moved a
    /// canvas from 30 to 29.97 fps would watch their projector go dark for the rest of the show.
    /// </remarks>
    private static string Canvas(CompositionDefinition composition) =>
        $"{composition.Width}×{composition.Height}@{composition.FramesPerSecond:0.###}";

    /// <summary>Whether this surface's idle frame has to be rebuilt, recording the new signature.</summary>
    private bool Changed(string key, string signature)
    {
        if (_idleSignatures.TryGetValue(key, out var last) && last == signature)
            return false;

        _idleSignatures[key] = signature;
        return true;
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
            Report($"{label} could not be loaded - {failure.Message}");
            return null;
        }
    }
}
