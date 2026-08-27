namespace HaCue2.Core.Model;

/// <summary>
/// One NDI source name this show puts on the network - the on-wire identity an audio line and a
/// video output can share.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is stored ONCE, here.</b> A linked A/V feed used to be two records joined three ways
/// at once - Guid links, name/hint strings, and a carries-audio flag - and the runtime joined them by
/// the STRINGS while the UI joined them by the Guids, so the reachable incoherent states (a rename
/// that split the sender on the network, a flag disagreeing with the link) were exactly the bugs the
/// 2026-08-25 review lists. A carrier makes those states unrepresentable: what the sender carries is
/// derived from which rows reference it - an <see cref="AudioLineDefinition"/> referencing it is the
/// audio half, a <see cref="VideoOutputDefinition"/> referencing it is the video half - and the audio
/// channel count lives on the line alone.
/// </para>
/// <para>
/// An audio-only NDI feed - the common NDI case - is simply a carrier with only a line half; it can
/// grow a video half later without being recreated. At most one row of each kind may reference a
/// carrier (the native sender's halves are single-writer; the validator enforces it here and
/// <c>NdiSenderHub.Acquire</c> enforces it again at open time).
/// </para>
/// </remarks>
public sealed record NdiCarrierDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The NDI source name receivers see. Renaming it here renames BOTH halves.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Folds pre-carrier documents (schema ≤ 3) onto <see cref="NdiCarrierDefinition"/>s on load.
/// </summary>
/// <remarks>
/// The old runtime joined an NDI line and an NDI output into one sender by their EFFECTIVE NAME
/// (hint-or-row-name), whatever the Guid links said - so the migration joins by the same rule the
/// show actually ran under: equal effective names become one carrier, and the Guid links only get to
/// join halves whose names already agreed with each other's rows. Idempotent: a row that already has
/// a carrier is left alone, so re-running over a migrated in-memory document changes nothing.
/// </remarks>
public static class CarrierMigration
{
    public static void Migrate(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var byName = new Dictionary<string, NdiCarrierDefinition>(StringComparer.Ordinal);
        foreach (var carrier in project.NdiCarriers)
            byName.TryAdd(carrier.Name, carrier);

        NdiCarrierDefinition For(string effectiveName)
        {
            if (byName.TryGetValue(effectiveName, out var existing))
                return existing;

            var carrier = new NdiCarrierDefinition { Name = effectiveName };
            project.NdiCarriers.Add(carrier);
            byName[effectiveName] = carrier;
            return carrier;
        }

        foreach (var output in project.VideoOutputs.Where(item => item.Kind == VideoOutputKind.Ndi))
        {
            if (output.CarrierId is null)
            {
                // The hint-or-name rule is what the engine resolved the sender name from.
                var effective = output.TargetHint.Length > 0 ? output.TargetHint : output.Name;
                output.CarrierId = For(effective).Id;
            }

            // The hint is retired for NDI rows: the carrier IS the on-wire identity now, and a stale
            // hint kept beside it would be the very second source of truth this migration removes.
            output.TargetHint = "";
        }

        foreach (var line in project.AudioLines.Where(item => item.Kind == AudioLineKind.Ndi))
        {
            if (line.CarrierId is null)
            {
                var effective = line.DeviceHint.Length > 0 ? line.DeviceHint : line.Name;

                // The Guid link decides only when the names never disagreed: a linked pair whose
                // names DID differ was already two senders on the wire, and honouring the link here
                // would silently merge feeds the old build kept apart.
                var linked = line.LegacyLinkedVideoOutputId is { } outputId
                    ? project.VideoOutputs.FirstOrDefault(item => item.Id == outputId)
                    : null;
                line.CarrierId =
                    linked?.CarrierId is { } carrierId
                    && project.FindCarrier(carrierId) is { } joined
                    && joined.Name == effective
                        ? joined.Id
                        : For(effective).Id;
            }

            line.DeviceHint = "";
        }

        // The legacy channel count lived on the VIDEO record, duplicating the line's. Where the two
        // disagree the LINE wins - it is what the bay actually opened - so nothing to copy; the
        // legacy value is simply dropped with the flag it came with.
    }
}
