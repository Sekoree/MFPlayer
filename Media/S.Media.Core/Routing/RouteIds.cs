namespace S.Media.Core.Routing;

/// <summary>Strongly-typed identifier for a registered input (audio or video channel).</summary>
public readonly record struct InputId(Guid Value)
{
    public static InputId New() => new(Guid.NewGuid());
    public override string ToString() => $"Input({Value:N8})";
}

/// <summary>Strongly-typed identifier for a registered endpoint.</summary>
public readonly record struct EndpointId(Guid Value)
{
    public static EndpointId New() => new(Guid.NewGuid());
    public override string ToString() => $"Endpoint({Value:N8})";
}

/// <summary>Strongly-typed identifier for a route (input → endpoint).</summary>
public readonly record struct RouteId(Guid Value)
{
    public static RouteId New() => new(Guid.NewGuid());
    public override string ToString() => $"Route({Value:N8})";
}

