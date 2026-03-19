namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Tuning parameters for <see cref="AudioVideoMixer"/> drift correction.
/// Defaults mirror the previous hardcoded behavior.
/// </summary>
public sealed class AudioVideoDriftCorrectionConfig
{
    /// <summary>Enables/disables automatic drift correction.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Periodic correction cadence in milliseconds.</summary>
    public int CorrectionIntervalMs { get; set; } = 50;

    /// <summary>No correction is applied when absolute drift is inside this deadband.</summary>
    public double DeadbandSeconds { get; set; } = 0.008;

    /// <summary>When absolute drift exceeds this threshold a hard seek is attempted.</summary>
    public double HardResyncThresholdSeconds { get; set; } = 0.200;

    /// <summary>Proportional gain used to compute each micro-correction step.</summary>
    public double CorrectionGain { get; set; } = 0.12;

    /// <summary>Maximum absolute correction step applied per tick (seconds).</summary>
    public double MaxStepSeconds { get; set; } = 0.002;

    /// <summary>Maximum absolute accumulated correction offset (seconds).</summary>
    public double MaxAbsoluteCorrectionSeconds { get; set; } = 0.120;

    public AudioVideoDriftCorrectionConfig CloneNormalized()
    {
        var normalizedIntervalMs = Math.Max(1, CorrectionIntervalMs);

        var normalizedDeadband = NormalizeNonNegativeFinite(DeadbandSeconds, 0.008);
        var normalizedHardResync = NormalizeNonNegativeFinite(HardResyncThresholdSeconds, 0.200);
        if (normalizedHardResync < normalizedDeadband)
            normalizedHardResync = normalizedDeadband;

        var normalizedGain = NormalizeFinite(CorrectionGain, 0.12);
        if (normalizedGain < 0)
            normalizedGain = Math.Abs(normalizedGain);

        var normalizedMaxStep = NormalizeNonNegativeFinite(MaxStepSeconds, 0.002);
        var normalizedMaxAbsolute = NormalizeNonNegativeFinite(MaxAbsoluteCorrectionSeconds, 0.120);
        if (normalizedMaxAbsolute < normalizedMaxStep)
            normalizedMaxAbsolute = normalizedMaxStep;

        return new AudioVideoDriftCorrectionConfig
        {
            Enabled = Enabled,
            CorrectionIntervalMs = normalizedIntervalMs,
            DeadbandSeconds = normalizedDeadband,
            HardResyncThresholdSeconds = normalizedHardResync,
            CorrectionGain = normalizedGain,
            MaxStepSeconds = normalizedMaxStep,
            MaxAbsoluteCorrectionSeconds = normalizedMaxAbsolute
        };
    }

    private static double NormalizeFinite(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : value;
    }

    private static double NormalizeNonNegativeFinite(double value, double fallback)
    {
        var normalized = NormalizeFinite(value, fallback);
        return normalized < 0 ? Math.Abs(normalized) : normalized;
    }
}

