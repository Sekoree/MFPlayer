using S.Media.Core.Media;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// §heavy-media-fixes phase 7 — opt-in interface for video endpoints that
/// can adopt an early "what the source looks like" hint for diagnostics
/// (HUD, logging, layout). When a decoder is attached, the player pushes
/// <see cref="S.Media.Core.Video.IVideoChannel.SourceFormat"/> into the
/// endpoint via this interface so the HUD's <c>src:</c> line reflects the
/// real width / height / fps / pixel format from frame zero, instead of
/// waiting for the first decoded frame to update it.
/// </summary>
public interface IVideoEndpointInputFormatHint
{
    /// <summary>
    /// Pushes a non-authoritative description of what frames will look like
    /// when they start arriving. Implementations should use the hint only
    /// for diagnostics — the first real frame's format remains the source
    /// of truth for renderer state.
    /// </summary>
    void SetInputFormatHint(VideoFormat format);
}
