using HaCue2.Machine;
using HaCue2.ViewModels;

namespace HaCue2.Session;

/// <summary>Applies operator-selected PPM/VU deflection and peak hold to raw program-meter readings.</summary>
public sealed class ProgramMeterPresenter
{
    /// <summary>
    /// PPM fall-back rate, in normalized meter scale per second.
    /// </summary>
    /// <remarks>
    /// A PPM's defining behaviour is instant attack and a SLOW, constant fall (IEC 60268-10 is
    /// 20 dB in ~1.7 s). Without it the bar just tracks each buffer's peak, so a transient between
    /// two reads never deflects and a falling level drops in poll-sized steps. The meter scale is
    /// normalized over 60 dB, so ~12 dB/s is 0.2 of the scale per second.
    /// </remarks>
    private const double PpmFallPerSecond = 0.2;

    private readonly Dictionary<string, HeldPeak> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeldPeak> _shown = new(StringComparer.Ordinal);

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
                Level = vu ? meter.Level : Ppm(meter.Caption, meter.Peak, now),
                Peak = state.Value,
            });
        }

        return result;
    }

    /// <summary>PPM deflection: the bar jumps to a louder peak instantly and falls at the meter's
    /// own rate rather than at whatever cadence the readings arrive.</summary>
    private double Ppm(string caption, double peak, DateTimeOffset now)
    {
        var previous = _shown.TryGetValue(caption, out var shown) ? shown : new HeldPeak(0, now);
        var dt = (now - previous.Since).TotalSeconds;
        var fallen = dt > 0 ? previous.Value - (PpmFallPerSecond * dt) : previous.Value;
        var value = Math.Max(peak, Math.Max(0, fallen));
        _shown[caption] = new HeldPeak(value, now);
        return value;
    }

    private readonly record struct HeldPeak(double Value, DateTimeOffset Since);
}
