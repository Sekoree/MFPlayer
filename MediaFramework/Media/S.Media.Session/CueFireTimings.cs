using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// Fire-path phase timings (F-08 acceptance: the latency phases must be observable, not inferred).
/// Cumulative <see cref="TimingAccumulator"/> snapshots - consumers diff consecutive snapshots for
/// windowed views, exactly like <c>ClipCompositionRuntimeStats</c>' timing fields.
/// </summary>
/// <param name="GroupLockWait">Time a fire/GO spent WAITING for its per-group fire locks before any
/// of its own work began. The head-of-line signal: a large value means another fire on the same
/// group(s) held them - the exact wait that was invisible while one global lock serialized
/// everything. Unrelated groups never contribute here by construction.</param>
/// <param name="GoSelect">GO's cue selection (cursor read + next-cue choice), recorded per
/// selection attempt (a cross-group auto-continue chain re-selects under a wider lock set).</param>
/// <param name="FireExecute">An ordinary fire's execution - the cue graph fire including its
/// authored pre/post-waits and media open. Authored waits dominate averages by design;
/// <see cref="TimingSnapshot.MaxMs"/>/<see cref="TimingSnapshot.LastMs"/> are the operator-useful
/// half. Independent/scheduled batch fires are excluded (their duration is owned by barriers and
/// absolute start edges, not by this path).</param>
public sealed record CueFireTimings(
    TimingSnapshot GroupLockWait,
    TimingSnapshot GoSelect,
    TimingSnapshot FireExecute);
