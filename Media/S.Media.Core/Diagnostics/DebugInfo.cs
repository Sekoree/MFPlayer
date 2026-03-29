namespace S.Media.Core.Diagnostics;

public readonly record struct DebugInfo(
    string Key,
    DebugValueKind ValueKind,
    object Value,
    DateTimeOffset RecordedAtUtc);

