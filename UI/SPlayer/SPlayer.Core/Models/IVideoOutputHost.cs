using System;
using S.Media.Core.Media.Endpoints;

namespace SPlayer.Core.Models;

/// <summary>
/// Backend used to host a video output. The Outputs tab exposes both via the
/// "+ Video" split-button so a user can compare the two side-by-side on the
/// same source.
/// </summary>
public enum VideoOutputBackend
{
    /// <summary>
    /// Avalonia <c>OpenGlControlBase</c>-driven output. Renders inside an
    /// Avalonia <see cref="Avalonia.Controls.Window"/> and is paced by the
    /// Avalonia compositor. Convenient because it shares the app's theming
    /// and DPI handling, but its render-loop is dispatcher-driven.
    /// </summary>
    Avalonia,

    /// <summary>
    /// SDL3 + OpenGL output. Runs in its own native window with a dedicated
    /// vsync-paced render thread that does not share the Avalonia
    /// dispatcher. Preferred for heavy 4K / high-bitrate content where the
    /// Avalonia renderer can stall on the UI thread.
    /// </summary>
    Sdl3
}

/// <summary>
/// Owner of a concrete video output (its window/lifetime + the underlying
/// <see cref="IVideoEndpoint"/>). Lets <see cref="VideoEndpointModel"/> work
/// uniformly across the Avalonia and SDL3 backends without baking either
/// dependency into the model itself.
///
/// <para>
/// Implementations own the underlying window/native resources and must:
/// </para>
/// <list type="bullet">
///   <item><description>Expose the same <see cref="IVideoEndpoint"/> instance for the lifetime of the host.</description></item>
///   <item><description>Translate <see cref="Close"/> into "close my window and tear down resources"; the host raises <see cref="Closed"/> when that completes.</description></item>
///   <item><description>Surface <see cref="ShowHud"/> / <see cref="LimitRenderToInputFps"/> as live, two-way properties on the underlying endpoint.</description></item>
/// </list>
/// </summary>
public interface IVideoOutputHost
{
    /// <summary>The video endpoint backing this output.</summary>
    IVideoEndpoint VideoEndpoint { get; }

    /// <summary>Toggles the HUD overlay on the underlying endpoint.</summary>
    bool ShowHud { get; set; }

    /// <summary>
    /// Limits render-loop pacing to the source FPS hint. See
    /// <c>AppSettings.LimitRenderFpsToSource</c>.
    /// </summary>
    bool LimitRenderToInputFps { get; set; }

    /// <summary>
    /// Short backend name used in the UI ("Avalonia" / "SDL3"). Surfaced in
    /// the Outputs row info text so the user can see at a glance which
    /// backend an entry is using.
    /// </summary>
    string BackendName { get; }

    /// <summary>Closes the host's window. Idempotent.</summary>
    void Close();

    /// <summary>
    /// Raised exactly once after the host's window has finished closing
    /// (the user clicked × or <see cref="Close"/> was invoked). The
    /// <see cref="VideoEndpointModel"/> uses this to remove itself from the
    /// outputs collection.
    /// </summary>
    event EventHandler? Closed;
}
