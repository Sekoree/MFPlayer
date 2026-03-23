using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSCLib;

public sealed class OSCClient : IOSCClient
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ILogger<OSCClient> _logger;
    private bool _disposed;

    public OSCClient(string host, int port, OSCClientOptions? options = null, ILogger<OSCClient>? logger = null)
        : this(ResolveEndpoint(host, port), options, logger)
    {
    }

    public OSCClient(IPEndPoint remoteEndPoint, OSCClientOptions? options = null, ILogger<OSCClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        Options = options ?? new OSCClientOptions();
        if (Options.MaxPacketBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPacketBytes), Options.MaxPacketBytes, "MaxPacketBytes must be greater than 0.");

        _remoteEndPoint = remoteEndPoint;
        _logger = logger ?? NullLogger<OSCClient>.Instance;

        _udpClient = new UdpClient(remoteEndPoint.AddressFamily);
        _udpClient.Connect(remoteEndPoint);
    }

    public OSCClientOptions Options { get; }

    public async ValueTask SendAsync(OSCPacket packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);

        using var encoded = OSCPacketCodec.EncodeToRented(packet);

        if (encoded.Length > Options.MaxPacketBytes)
            throw new InvalidOperationException($"OSC packet size {encoded.Length} exceeds configured max {Options.MaxPacketBytes}.");

        await _udpClient.Client
            .SendToAsync(encoded.Memory, SocketFlags.None, _remoteEndPoint, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask SendMessageAsync(string address, IReadOnlyList<OSCArgument>? arguments = null, CancellationToken cancellationToken = default)
        => SendAsync(OSCPacket.FromMessage(new OSCMessage(address, arguments)), cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _udpClient.Dispose();
        _disposed = true;
        _logger.LogDebug("OSC client disposed for {RemoteEndPoint}", _remoteEndPoint);
        return ValueTask.CompletedTask;
    }

    private static IPEndPoint ResolveEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");

        var address = Dns.GetHostAddresses(host).FirstOrDefault();
        if (address is null)
            throw new InvalidOperationException($"No IP addresses were resolved for host '{host}'.");

        return new IPEndPoint(address, port);
    }
}

