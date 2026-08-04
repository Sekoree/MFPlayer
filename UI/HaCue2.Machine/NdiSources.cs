using S.Media.NDI;

namespace HaCue2.Machine;

/// <summary>
/// The NDI senders this machine can see right now.
/// </summary>
/// <remarks>
/// <para>
/// A machine fact like the device list, and asked the same way: once, on demand, with the answer
/// handed to whoever is drawing a picker. Discovery is a network scan with a timeout rather than an
/// enumeration, so it is a method and not a property — a caller has to decide how long it is willing
/// to wait, and a dialog opening is a different budget from a status pass.
/// </para>
/// <para>
/// Every failure is EMPTY plus a reason, never an exception. A machine without the NDI runtime
/// installed, a machine on no network, and a machine where nothing happens to be sending are three
/// different situations that all mean "there is nothing to pick from this list", and none of them is
/// a reason for the dialog not to open — a name can still be typed, and it will resolve at the venue.
/// </para>
/// </remarks>
public static class NdiSources
{
    /// <summary>Long enough for a sender to answer, short enough that a dialog still feels like it opened.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>What a scan found, and why it found nothing when it did.</summary>
    /// <param name="Names">Sender names exactly as NDI advertises them — "STUDIO-PC (CAM 1)".</param>
    /// <param name="Unavailable">Null when the scan ran; the reason when NDI could not be asked at all.</param>
    public sealed record Scan(IReadOnlyList<string> Names, string? Unavailable = null)
    {
        public static Scan Nothing { get; } = new([]);

        /// <summary>A line for the dialog: what was found, or why nothing could be.</summary>
        public string Note => Unavailable is { Length: > 0 } reason
            ? $"NDI is not available on this machine — {reason}"
            : Names.Count == 0
                ? "no senders found · type a name and it will resolve when the sender appears"
                : $"{Names.Count} sender{(Names.Count == 1 ? "" : "s")} on the network";
    }

    /// <summary>Scans the network. Blocks for at most <paramref name="timeout"/>.</summary>
    public static Scan Discover(TimeSpan? timeout = null)
    {
        try
        {
            var found = NDISource.Find(timeout ?? DefaultTimeout);

            return new Scan([.. found
                .Select(source => source.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Typically a missing native runtime. The dialog still opens on a free-text name, which is
            // also how a show gets authored on a laptop for a rig it has never seen.
            return new Scan([], failure.Message);
        }
    }
}
