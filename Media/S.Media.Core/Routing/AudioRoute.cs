namespace S.Media.Core.Routing;

public readonly record struct AudioRoute(Guid SourceId, int SourceChannel, int OutputChannel);

