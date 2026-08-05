using HaCue2.Machine;
using HaCue2.ViewModels;

namespace HaCue2.Session;

/// <summary>Applies operator-selected PPM/VU deflection and peak hold to raw program-meter readings.</summary>
public sealed class ProgramMeterPresenter
{
    private readonly Dictionary<string, HeldPeak> _held = new(StringComparer.Ordinal);

    public IReadOnlyList<ProgramMeter> Present(
        IReadOnlyList<ProgramMeter> raw,
        AppSettings settings,
        DateTimeOffset now)
    {
        var hold = TimeSpan.FromMilliseconds(Math.Max(0, settings.PeakHoldMs));
        var vu = string.Equals(settings.MeterBallistics, "VU", StringComparison.OrdinalIgnoreCase);
        var result = new List<ProgramMeter>(raw.Count);

        foreach (var meter in raw)
        {
            if (!_held.TryGetValue(meter.Caption, out var state)
                || meter.Peak >= state.Value
                || now - state.Since >= hold)
            {
                state = new HeldPeak(meter.Peak, now);
                _held[meter.Caption] = state;
            }

            result.Add(meter with
            {
                Level = vu ? meter.Level : meter.Peak,
                Peak = state.Value,
            });
        }

        return result;
    }

    private readonly record struct HeldPeak(double Value, DateTimeOffset Since);
}
