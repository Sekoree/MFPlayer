namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Optional capability: advertises accepted formats so the graph can
/// validate route compatibility at creation time.
/// </summary>
public interface IFormatCapabilities<TFormat> where TFormat : struct
{
    IReadOnlyList<TFormat> SupportedFormats { get; }
    TFormat? PreferredFormat { get; }
}

