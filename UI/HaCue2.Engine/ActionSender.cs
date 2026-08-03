using System.Net;
using System.Net.Sockets;
using HaCue2.Core.Model;
using OSCLib;

namespace HaCue2.Engine;

/// <summary>
/// Sends an action cue's message to its endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Clients are cached per endpoint and live as long as the show does: an OSC client owns a UDP socket,
/// and opening one per cue fire would burn a source port on every GO.
/// </para>
/// <para>
/// <b>MIDI out is not implemented and says so.</b> It needs the control-device layer
/// (<c>S.Control</c>) that HaCue2 has not adopted yet, and the one thing an action cue must never do is
/// report success for a message that went nowhere — an operator who sees no error assumes the desk got
/// it. The refusal is returned, surfaced on the transport, and reported by Project status.
/// </para>
/// </remarks>
public sealed class ActionSender : IDisposable
{
    private readonly Dictionary<Guid, OSCClient> _clients = [];
    private readonly Dictionary<Guid, string> _sent = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>
    /// What each endpoint was last sent, and when — the Targets screen's "Last seen" column.
    /// </summary>
    /// <remarks>
    /// <b>Successes only.</b> A send that threw did not reach the desk, and recording it here would
    /// tell an operator checking why a console is not responding that the app had just talked to it.
    /// Failures already travel the other way, through <c>Problems</c>, which is where a reader looking
    /// for a fault expects to find one.
    /// </remarks>
    public IReadOnlyDictionary<Guid, string> LastSent
    {
        get
        {
            lock (_gate)
                return new Dictionary<Guid, string>(_sent);
        }
    }

    /// <summary>
    /// Sends one action cue, or explains why it could not.
    /// </summary>
    /// <returns>Null on success; the reason otherwise.</returns>
    public async Task<string?> SendAsync(ActionCueNode cue, ActionEndpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(cue);

        if (endpoint is null)
            return $"“{cue.Label}” names no endpoint";

        if (endpoint.Kind == EndpointKind.MidiOut)
            return $"“{endpoint.Name}” is a MIDI endpoint — MIDI output is not implemented yet";

        if (cue.Address.Length == 0)
            return $"“{cue.Label}” has no address to send to";

        OSCClient client;

        try
        {
            client = ClientFor(endpoint);
        }
        catch (Exception failure) when (failure is FormatException or ArgumentException or SocketException)
        {
            return $"“{endpoint.Name}” could not be reached — {failure.Message}";
        }

        try
        {
            await client.SendMessageAsync(cue.Address, Arguments(cue.Arguments)).ConfigureAwait(false);

            // The ADDRESS rather than the cue label: an operator watching this column is checking what
            // the desk was told, and two cues sending the same address is a normal way to build a show.
            lock (_gate)
                _sent[endpoint.Id] = $"{cue.Address} · {DateTime.Now:HH:mm:ss}";

            return null;
        }
        catch (Exception failure) when (failure is SocketException or ObjectDisposedException or IOException)
        {
            return $"“{cue.Label}” → {endpoint.Name} failed — {failure.Message}";
        }
    }

    private OSCClient ClientFor(ActionEndpoint endpoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_clients.TryGetValue(endpoint.Id, out var existing))
                return existing;

            // Parse rather than resolve: a name lookup on the transport path would block a GO for as
            // long as DNS takes to answer. Endpoints are addresses on a show network.
            var host = IPAddress.Parse(endpoint.Host);
            var client = new OSCClient(new IPEndPoint(host, endpoint.Port));

            _clients[endpoint.Id] = client;
            return client;
        }
    }

    /// <summary>
    /// The cue's argument text as OSC arguments.
    /// </summary>
    /// <remarks>
    /// Whitespace-separated, each token typed by what it looks like: an integer stays an integer, a
    /// decimal becomes a float, everything else is a string. Consoles distinguish these — an EOS cue
    /// number sent as the string "7.2" is not the float 7.2 — so guessing by shape beats sending
    /// everything as text.
    /// </remarks>
    private static List<OSCArgument> Arguments(string text) =>
    [
        .. text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
                int.TryParse(token, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var whole)
                    ? OSCArgument.Int32(whole)
                    : float.TryParse(token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var real)
                        ? OSCArgument.Float32(real)
                        : OSCArgument.String(token)),
    ];

    public void Dispose()
    {
        List<OSCClient> clients;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            clients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var client in clients)
            client.Dispose();
    }
}
