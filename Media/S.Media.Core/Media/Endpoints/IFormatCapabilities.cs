namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Optional capability: advertises accepted formats so the graph can
/// validate route compatibility at creation time.
/// </summary>
/// <remarks>
/// <b>Contract (§3.53 / CH9):</b>
/// <list type="bullet">
///   <item><see cref="SupportedFormats"/> MUST return a non-null collection.
///         Implementations that cannot enumerate supported formats ahead of
///         time should return an empty list to indicate "unknown — try
///         anything and fall back to resample/channel-map". The router logs
///         a warning in that case but creates the route.</item>
///   <item><see cref="PreferredFormat"/> may be <see langword="null"/>; when
///         non-null, it MUST appear in <see cref="SupportedFormats"/> (or be
///         naturally negotiable — callers shouldn't rely on strict equality).</item>
/// </list>
/// The non-null contract is currently pinned by <see cref="System.Diagnostics.Debug.Assert(bool,string)"/>
/// inside <c>AVRouter.CreateAudioRoute</c> / <c>CreateVideoRoute</c>;
/// review item §6.10 promotes this to a throw in a later release.
/// </remarks>
public interface IFormatCapabilities<TFormat> where TFormat : struct
{
    IReadOnlyList<TFormat> SupportedFormats { get; }
    TFormat? PreferredFormat { get; }
}

