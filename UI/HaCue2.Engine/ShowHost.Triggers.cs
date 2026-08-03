using HaCue2.Core.Model;
using S.Control;

namespace HaCue2.Engine;

/// <summary>
/// What arrives from outside: a matched trigger, and a control surface riding a parameter.
/// </summary>
/// <remarks>
/// <b>Everything here lands on the same verbs the buttons call.</b> A triggered GO is a GO. A separate
/// fire path for external input would be a second implementation of what GO means, and the two would
/// eventually disagree in front of an audience.
/// </remarks>
public sealed partial class ShowHost
{
    /// <summary>One binding object per parameter, so soft takeover has somewhere to keep its latch.</summary>
    private readonly Dictionary<string, ContinuousBinding> _continuous = [];

    /// <summary>The program master trim in decibels, as the parameter registry reads and writes it.</summary>
    private double _masterTrimDb;

    /// <summary>Carries out what a trigger asked for.</summary>
    /// <remarks>
    /// Deliberately the same methods the UI calls. A trigger that had its own fire path would be a
    /// second implementation of "what GO means", and the two would eventually disagree in front of an
    /// audience.
    /// </remarks>
    private async Task ApplyAsync(TriggerAction action)
    {
        switch (action.Target)
        {
            case TriggerTarget.Cue when action.CueId is { } cueId:
                await FireAsync(cueId).ConfigureAwait(false);
                break;

            case TriggerTarget.Transport:
                await TransportAsync(action.ParameterId).ConfigureAwait(false);
                break;

            case TriggerTarget.Parameter when action.Value is { } value:
                ApplyParameter(action.ParameterId, value);
                break;

            case TriggerTarget.Parameter:
                // A note-on bound to a fader-shaped parameter. Reported rather than dropped, because a
                // binding that quietly does nothing reads as a dead controller.
                Report($"“{action.Describe}” carries no value to write");
                break;
        }
    }

    /// <summary>
    /// Rides one parameter from a control surface.
    /// </summary>
    /// <remarks>
    /// <b>Soft takeover, by default.</b> A physical fader has its own position, and after a cue or the
    /// mouse has moved a parameter the two disagree — applying the fader's value on its first move
    /// would jump the level audibly, mid-show. The binding ignores the control until it passes close to
    /// the current value and only then latches on. The binding object is kept per parameter because the
    /// latch is state: rebuilt per message, it would never latch at all.
    /// </remarks>
    private void ApplyParameter(string parameterId, double value)
    {
        if (!_parameters.TryGetTarget(parameterId, out var target))
        {
            Report($"“{parameterId}” is not a parameter this show offers");
            return;
        }

        ContinuousBinding binding;

        lock (_gate)
        {
            if (!_continuous.TryGetValue(parameterId, out var existing))
            {
                // The trigger layer has already scaled into the binding's own range, so the spec's
                // input range is that range rather than a raw 0..127.
                existing = new ContinuousBinding(
                    new ContinuousBindingSpec(
                        parameterId,
                        InputMin: target.Minimum,
                        InputMax: target.Maximum,
                        SoftTakeover: true),
                    _parameters);

                _continuous[parameterId] = existing;
            }

            binding = existing;
        }

        binding.Apply(value);
    }

    /// <summary>The transport verbs a trigger can name. Unknown names are reported, never guessed.</summary>
    private async Task TransportAsync(string verb)
    {
        switch (verb.Trim().ToLowerInvariant())
        {
            case "go":
                foreach (var list in _project.CueLists)
                {
                    await GoAsync(list).ConfigureAwait(false);
                    break;
                }

                break;

            case "stop":
                await StopAllAsync().ConfigureAwait(false);
                break;

            case "panic":
                await PanicAsync().ConfigureAwait(false);
                break;

            case "pause":
                await SetPausedAsync(!IsPaused).ConfigureAwait(false);
                break;

            default:
                Report($"“{verb}” is not a transport verb — try go, stop, pause or panic");
                break;
        }
    }
}
