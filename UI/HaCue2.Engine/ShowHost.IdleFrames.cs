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
    /// resolved file stamp plus the fit and canvas, which is the whole of what the frame is built from.
    /// </remarks>
    private readonly Dictionary<string, string> _idleSignatures = [];

    /// <summary>Signatures whose decode FAILED, kept separately so an unchanged broken file is
    /// reported once rather than re-decoded (and re-reported) on every 300 ms edit reload. The
    /// signature embeds the file's length + write stamp, so repairing or replacing the file changes
    /// it and triggers the retry - no re-poll of a byte-identical failure is needed for recovery.</summary>
    private readonly Dictionary<string, string> _idleFailureSignatures = [];

    /// <summary>Test seam: only successfully applied idle sources are cached here.</summary>
    internal int CachedIdleFrameCount => _idleSignatures.Count;

    /// <summary>Decodes authored stills once and transfers their frames to the composition runtime.</summary>
    private async Task ApplyIdleFramesAsync(HaCueProject project, string? projectPath)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var composition in project.Compositions)
        {
            var key = $"c:{composition.Id}";
            // The FIT is part of the signature: changing how the slate fills the canvas rebuilds the
            // frame, exactly as changing the picture does.
            var signature =
                $"{IdleSourceStamp(project, composition.IdleImagePath, projectPath)}|{composition.IdleImageFit}|{Canvas(composition)}";
            wanted.Add(key);

            if (!Changed(key, signature))
                continue;

            var frame = await DecodeIdleAsync(
                project,
                composition.IdleImagePath,
                projectPath,
                $"“{composition.Name}” idle image").ConfigureAwait(false);

            var sourceLoaded = string.IsNullOrWhiteSpace(composition.IdleImagePath) || frame is not null;
            if (frame is not null)
                frame = IdleFrames.Fitted(
                    frame, composition.Width, composition.Height, composition.IdleImageFit);

            await _session.SetCompositionIdleFrameAsync(composition.Id.ToString(), frame).ConfigureAwait(false);
            CacheIfLoaded(key, signature, sourceLoaded);
        }

        foreach (var output in project.VideoOutputs)
        {
            if (output.CompositionId is not { } compositionId
                || project.Compositions.FirstOrDefault(item => item.Id == compositionId) is not { } canvas
                || _screens.Open.FirstOrDefault(open => open.Id == output.Id) is not { } open)
                continue;

            var key = $"o:{output.Id}";
            var signature =
                $"{IdleSourceStamp(project, output.IdleFallbackPath, projectPath)}|{output.IdleFallbackFit}|{compositionId}|{Canvas(canvas)}";
            wanted.Add(key);

            if (!Changed(key, signature))
                continue;

            var frame = await DecodeIdleAsync(
                project,
                output.IdleFallbackPath,
                projectPath,
                $"“{output.Name}” idle fallback").ConfigureAwait(false);

            var sourceLoaded = string.IsNullOrWhiteSpace(output.IdleFallbackPath) || frame is not null;
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
            CacheIfLoaded(key, signature, sourceLoaded);
        }

        // A surface that is gone must lose its signature, or re-adding an output with the same id - an
        // undo, most obviously - would find its idle "unchanged" and never submit the frame that opens
        // its window.
        foreach (var stale in _idleSignatures.Keys.Where(key => !wanted.Contains(key)).ToList())
            _idleSignatures.Remove(stale);
        foreach (var stale in _idleFailureSignatures.Keys.Where(key => !wanted.Contains(key)).ToList())
            _idleFailureSignatures.Remove(stale);
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

    /// <summary>Whether this surface's idle frame has to be rebuilt. An unchanged signature in
    /// EITHER cache answers no: a success needs no rework, and a failure whose file has not changed
    /// would only fail (and report) again - the file-identity part of the signature is what brings
    /// a repaired source back through here.</summary>
    private bool Changed(string key, string signature) =>
        !(_idleSignatures.TryGetValue(key, out var last) && last == signature)
        && !(_idleFailureSignatures.TryGetValue(key, out var failed) && failed == signature);

    private void CacheIfLoaded(string key, string signature, bool loaded)
    {
        if (loaded)
        {
            _idleSignatures[key] = signature;
            _idleFailureSignatures.Remove(key);
        }
        else
        {
            _idleFailureSignatures[key] = signature;
            _idleSignatures.Remove(key);
        }
    }

    /// <summary>The authored source plus cheap filesystem identity. A replacement at the same path
    /// must invalidate the cache; hashing on every 300 ms edit reload would recreate the disk churn
    /// this cache exists to avoid, so length + UTC write stamp is the appropriate file-version key.</summary>
    private static string IdleSourceStamp(
        HaCueProject project, string authoredPath, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(authoredPath))
            return "<black>";

        try
        {
            var resolved = MediaPaths.Resolve(project, authoredPath, projectPath);
            var file = new FileInfo(resolved);
            return $"{resolved}|{file.Exists}|{(file.Exists ? file.Length : -1)}|" +
                   $"{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        }
        catch (Exception failure) when (
            failure is ArgumentException or NotSupportedException or PathTooLongException
                or UnauthorizedAccessException or IOException)
        {
            // DecodeIdleAsync owns the operator-facing problem. The authored path is the stable
            // fallback signature: a source that cannot even be stat-ed offers no change signal, so
            // its failure is reported once and retried when the authored path (or fit/canvas)
            // changes - same as any other unchanged broken source.
            return authoredPath;
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
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Broad by design: an idle frame is best-effort, and the decoder's failure surface is not
            // a closed set - FFmpeg rejects a file with FFmpegException, and a decodable file with no
            // frame is InvalidDataException, neither of which the old named list covered. A slate that
            // will not decode must cost one reported line, never the reload it rode in on.
            Report($"{label} could not be loaded - {failure.Message}");
            return null;
        }
    }
}
