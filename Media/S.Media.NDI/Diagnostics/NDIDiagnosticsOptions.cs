namespace S.Media.NDI.Diagnostics;

public sealed record NDIDiagnosticsOptions
{
    public bool EnableDedicatedDiagnosticsThread { get; init; } = true;

    public TimeSpan DiagnosticsTickInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    internal TimeSpan MaxReadPauseForDiagnostics { get; init; } = TimeSpan.FromMilliseconds(10);

    internal bool PublishSnapshotsOnRequestOnly { get; init; }

    public NDIDiagnosticsOptions Normalize()
    {
        var tick = DiagnosticsTickInterval;
        if (tick < TimeSpan.Zero)
        {
            tick = TimeSpan.Zero;
        }

        if (tick < TimeSpan.FromMilliseconds(16))
        {
            tick = TimeSpan.FromMilliseconds(16);
        }

        return this with
        {
            DiagnosticsTickInterval = tick,
            MaxReadPauseForDiagnostics = MaxReadPauseForDiagnostics < TimeSpan.Zero ? TimeSpan.Zero : MaxReadPauseForDiagnostics,
        };
    }
}

