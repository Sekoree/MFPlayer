using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Present.SDL3;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>The production-only optional services supplied to every HaCue2 show session.</summary>
/// <remarks>
/// Keeping these delegates named and testable prevents a constructor edit from silently dropping GPU
/// composition or subtitles while all lower-level framework tests continue to pass.
/// </remarks>
internal static class ShowSessionWiring
{
    public static IVideoOverlaySource? CreateSubtitleOverlay(
        string path, int streamIndex, int width, int height) =>
        SubtitleOverlayFactory.FromFileDeferred(path, width, height, streamIndex);

    public static ClipCompositionCompositor CreateCompositor(VideoFormat canvasFormat)
    {
        var requested = Environment.GetEnvironmentVariable("HACUE2_COMPOSITOR")
                        ?? Environment.GetEnvironmentVariable("S_MEDIA_COMPOSITOR");
        var selected = SDL3CompositionCompositorFactory.Create(canvasFormat, requested, "HaCue2");
        return new ClipCompositionCompositor(
            selected.Compositor,
            selected.RequiresBgraLayerConversion,
            selected.BackendName,
            selected.DisposeOnDriverThread);
    }
}
