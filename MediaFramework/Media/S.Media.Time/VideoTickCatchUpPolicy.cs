namespace S.Media.Time;

/// <summary>How a <see cref="MediaClock"/> handles video deadlines missed while its driver was busy.</summary>
public enum VideoTickCatchUpPolicy
{
    /// <summary>Raise each missed tick, up to the driver's safety cap. Preserves legacy player behavior.</summary>
    Burst,

    /// <summary>Raise one tick for the newest due frame and count the older deadlines as skipped.</summary>
    Coalesce,
}
