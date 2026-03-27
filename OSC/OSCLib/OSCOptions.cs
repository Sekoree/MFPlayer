using System.Net;

namespace OSCLib;

/// <summary>
/// Controls how inbound OSC packets are parsed.
/// </summary>
public sealed class OSCDecodeOptions
{
    /// <summary>
    /// When <see langword="true"/>, unknown type tags and malformed payloads are rejected.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool StrictMode { get; set; } = true;

    /// <summary>
    /// Allows decoding messages that omit the OSC type tag string.
    /// Default: <see langword="true"/> for compatibility with older senders.
    /// </summary>
    public bool AllowMissingTypeTagString { get; set; } = true;

    /// <summary>
    /// Preserves unknown arguments as <see cref="OSCUnknownArgument"/> when strict mode is disabled.
    /// </summary>
    public bool PreserveUnknownArguments { get; set; }

    /// <summary>
    /// Optional callback that returns the payload byte-length for an unknown type tag.
    /// Required when <see cref="StrictMode"/> is disabled and unknown payload bytes should be consumed.
    /// </summary>
    public Func<char, ReadOnlySpan<char>, int>? UnknownTagByteLengthResolver { get; set; }

    /// <summary>
    /// Maximum nesting depth for OSC array tags.
    /// Default: <c>16</c>.
    /// </summary>
    public int MaxArrayDepth { get; set; } = 16;
}

/// <summary>
/// Runtime settings for <see cref="OSCServer"/>.
/// </summary>
public sealed class OSCServerOptions
{
    /// <summary>
    /// Local UDP port to bind.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Maximum accepted datagram size in bytes.
    /// Default: <c>8192</c>.
    /// </summary>
    public int MaxPacketBytes { get; set; } = 8192;

    /// <summary>
    /// Action for packets larger than <see cref="MaxPacketBytes"/>.
    /// Default: <see cref="OSCOversizePolicy.DropAndLog"/>.
    /// </summary>
    public OSCOversizePolicy OversizePolicy { get; set; } = OSCOversizePolicy.DropAndLog;

    /// <summary>
    /// Minimum interval between repeated oversize-drop warning logs.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan OversizeLogInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decode behavior applied to inbound datagrams.
    /// </summary>
    public OSCDecodeOptions DecodeOptions { get; set; } = new();

    /// <summary>
    /// Enables per-datagram hex dumps at <c>Trace</c> log level.
    /// </summary>
    public bool EnableTraceHexDump { get; set; }
}

/// <summary>
/// Runtime settings for <see cref="OSCClient"/>.
/// </summary>
public sealed class OSCClientOptions
{
    /// <summary>
    /// Maximum packet size the client is allowed to send.
    /// Default: <c>8192</c>.
    /// </summary>
    public int MaxPacketBytes { get; set; } = 8192;

    /// <summary>
    /// Reserved for symmetry with server-side decode settings.
    /// </summary>
    public OSCDecodeOptions DecodeOptions { get; set; } = new();
}

/// <summary>
/// Message dispatch context passed to route handlers.
/// </summary>
public readonly record struct OSCMessageContext(
    OSCMessage Message,
    IPEndPoint RemoteEndPoint,
    OSCTimeTag? BundleTimeTag,
    DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Route callback signature used by <see cref="IOSCServer"/>.
/// </summary>
public delegate ValueTask OSCMessageHandler(OSCMessageContext context, CancellationToken cancellationToken);

/// <summary>
/// UDP OSC client contract.
/// </summary>
public interface IOSCClient : IAsyncDisposable
{
    /// <summary>
    /// Encodes and sends an OSC packet.
    /// </summary>
    ValueTask SendAsync(OSCPacket packet, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience wrapper that builds and sends a single OSC message packet.
    /// </summary>
    ValueTask SendMessageAsync(string address, IReadOnlyList<OSCArgument>? arguments = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// UDP OSC server contract.
/// </summary>
public interface IOSCServer : IAsyncDisposable
{
    /// <summary>
    /// Active server options.
    /// </summary>
    OSCServerOptions Options { get; }

    /// <summary>
    /// Indicates whether the receive loop is currently active.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Registers a route handler and returns a token that unregisters on dispose.
    /// </summary>
    IDisposable RegisterHandler(string addressPattern, OSCMessageHandler handler);

    /// <summary>
    /// Starts the UDP receive loop.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the UDP receive loop.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
