namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Optional capability: this endpoint can provide a clock.
/// Hardware audio outputs, video outputs, and virtual tick endpoints implement this.
/// </summary>
public interface IClockCapableEndpoint
{
    IMediaClock Clock { get; }
}

