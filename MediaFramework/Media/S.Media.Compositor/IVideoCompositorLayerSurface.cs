using Silk.NET.OpenGL;

namespace S.Media.Compositor;

/// <summary>
/// A custom GL layer that renders directly into the compositor's canvas FBO - the "3D object layer" plugin
/// seam (Doc 05). It runs in the compositor's GL context, on the compositor's render thread (same-context
/// only - never a cross-process frame). Mirrors the C-ABI <c>MfpLayerSurfaceVTable</c>
/// (<c>configure_gl</c> / <c>render</c> / <c>destroy</c>) so a native plugin adapts onto this interface.
/// </summary>
public interface IVideoCompositorLayerSurface : IDisposable
{
    /// <summary>Configure (or reconfigure) for the canvas the surface will draw into. Called on the
    /// compositor thread with its context current, before the first <see cref="Render"/>.</summary>
    void ConfigureGl(GL gl, VideoFormat canvas);

    /// <summary>
    /// Render this layer into <paramref name="targetFbo"/> (the bound canvas framebuffer) at
    /// <paramref name="masterTime"/>, applying the layer <paramref name="transform"/> and
    /// <paramref name="opacity"/>. Runs on the compositor thread with its context current.
    /// <para>
    /// <paramref name="masterTime"/> is the composite's master/presentation time unless the placement
    /// carries a <see cref="CompositorSurfaceLayer.RenderTime"/>, in which case it is THAT per-surface
    /// instant. Implementations need no special handling either way - render the content for the time
    /// they are handed - but they must not assume the value is shared with the other layers of the
    /// composite, nor that it is monotonic across a source change.
    /// </para>
    /// </summary>
    void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity);
}

/// <summary>
/// Optional companion for a layer surface that owns objects tied to the compositor's GL context.
/// <see cref="IVideoCompositorLayerSurface.Dispose"/> may run on a control thread and should only stop
/// logical use; the GL compositor calls <see cref="ReleaseGl"/> on its owner thread with the context
/// current before destroying that context.
/// </summary>
public interface IVideoCompositorGlResource
{
    void ReleaseGl(GL gl);
}

/// <summary>
/// A layer-surface placed in a composite: the GL-rendering <see cref="IVideoCompositorLayerSurface"/> plus
/// its destination <see cref="Transform"/> and <see cref="Opacity"/>. Surface layers render on top of the
/// frame layers, in list order, directly into the compositor's canvas (no intermediate frame) - unless
/// <see cref="Effects"/> is non-empty, in which case the host renders the surface into an intermediate
/// canvas-sized texture and composites that through the per-layer effect chain (chroma key etc.), the
/// same shader path frame layers use.
/// </summary>
public readonly record struct CompositorSurfaceLayer(
    IVideoCompositorLayerSurface Surface,
    LayerTransform2D Transform,
    float Opacity,
    IReadOnlyList<VideoLayerEffect>? Effects = null,
    IReadOnlyList<WarpSection>? MappingSections = null)
{
    /// <summary>
    /// Optional per-surface render instant. Null (the default, and every ordinary placement) means the
    /// surface renders at the composite's master/presentation time - unchanged behavior. When set, the
    /// host passes THIS value to <see cref="IVideoCompositorLayerSurface.Render"/>'s <c>masterTime</c>
    /// for this placement only; every other layer of the same composite is untouched.
    /// <para>
    /// A frame layer selects a picture out of its slot's submitted queue, so it can be taken off master
    /// alignment simply by switching to latest-wins (<see cref="SlotKeepPolicy.Latest"/>). A surface has
    /// no queue - it RENDERS whatever instant it is handed - so detaching it from the composite's clock
    /// needs this explicit alternative. The one shipping caller is the dual-voice crossfade handoff: the
    /// outgoing clip's tail keeps compositing for the fade window while the composition's master time has
    /// already moved to the INCOMING clip's playhead, and without this the tail would render the wrong
    /// clip's instant (a model posed at 0:00 while its audio plays out at 3:12).
    /// </para>
    /// </summary>
    public TimeSpan? RenderTime { get; init; }
}

/// <summary>
/// Capability interface for compositors that can host <see cref="CompositorSurfaceLayer"/>s (NXT-10 -
/// layer surfaces as a first-class compositor citizen). Callers discover support with a type test instead
/// of hard-coding a backend; the CPU compositor deliberately does NOT implement it (the surface contract
/// renders through a live GL context), so a surface-producing source falls back to its CPU frame path
/// there. The host is responsible for calling <see cref="IVideoCompositorLayerSurface.ConfigureGl"/> on
/// its render thread before a surface's first <see cref="IVideoCompositorLayerSurface.Render"/> and again
/// after every canvas reconfigure.
/// </summary>
public interface IVideoCompositorSurfaceHost : IVideoCompositor
{
    /// <summary>
    /// Composite <paramref name="frameLayers"/> (back-to-front), then render
    /// <paramref name="surfaceLayers"/> on top (list order) directly into the canvas, and return the
    /// finished frame at <paramref name="presentationTime"/>. Each surface renders at
    /// <paramref name="presentationTime"/> unless its placement carries a
    /// <see cref="CompositorSurfaceLayer.RenderTime"/>, which overrides it for that placement only.
    /// </summary>
    VideoFrame CompositeWithSurfaces(
        IReadOnlyList<CompositorLayer> frameLayers,
        IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
        TimeSpan presentationTime);
}

/// <summary>
/// A video source that can ALSO render itself as a compositor layer surface (GPU-side, no CPU frame) -
/// e.g. a 3D renderer whose software raster is only a fallback. When the target composition's compositor
/// is an <see cref="IVideoCompositorSurfaceHost"/>, the session asks for a surface via
/// <see cref="CreateLayerSurface"/> and does NOT attach a frame output for the placement; the source may
/// then skip full-frame rasterization (its <c>TryReadNextFrame</c> should stay cheap - transport/priming
/// may still pull frames). On a CPU-only compositor the source is consumed through its normal frame path.
/// </summary>
public interface ILayerSurfaceVideoSource
{
    /// <summary>Creates the surface that will render this source's content. Called at most once per
    /// playback; the caller owns the surface's lifetime (disposed with the layer).</summary>
    IVideoCompositorLayerSurface CreateLayerSurface();
}
