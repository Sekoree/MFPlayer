using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSCLib;

public sealed class OSCServer : IOSCServer
{
    private readonly UdpClient _udpClient;
    private readonly ILogger<OSCServer> _logger;
    private readonly OSCRouter _router = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTimeOffset _lastOversizeLogUtc = DateTimeOffset.MinValue;
    private long _oversizeDrops;
    private bool _disposed;

    public OSCServer(OSCServerOptions options, ILogger<OSCServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port < IPEndPoint.MinPort || options.Port > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(options.Port), options.Port, "Port must be between 0 and 65535.");
        if (options.MaxPacketBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPacketBytes), options.MaxPacketBytes, "MaxPacketBytes must be greater than 0.");
        if (options.OversizeLogInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.OversizeLogInterval), options.OversizeLogInterval, "OversizeLogInterval cannot be negative.");

        Options = options;
        _logger = logger ?? NullLogger<OSCServer>.Instance;
        _udpClient = new UdpClient(options.Port);
    }

    public OSCServerOptions Options { get; }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public IDisposable RegisterHandler(string addressPattern, OSCMessageHandler handler)
        => _router.Register(addressPattern, handler);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return Task.CompletedTask;

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => ReceiveLoopAsync(_loopCts.Token), CancellationToken.None);
        _logger.LogInformation("OSC UDP server started on port {Port}", Options.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var loopTask = _loopTask;
        var loopCts = _loopCts;
        if (loopTask is null)
            return;

        loopCts?.Cancel();
        try
        {
            await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            // If the receive loop faulted (e.g. Throw oversize policy), still complete cleanup.
            _logger.LogDebug(ex, "OSC receive loop exited with an exception during stop.");
        }
        finally
        {
            loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        _logger.LogInformation("OSC UDP server stopped on port {Port}", Options.Port);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OSC server socket receive failed.");
                continue;
            }

            if (received.Buffer.Length > Options.MaxPacketBytes)
            {
                HandleOversizePacket(received.Buffer.Length);
                continue;
            }

            if (Options.EnableTraceHexDump && _logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("OSC datagram {Length}B from {Remote}: {Hex}", received.Buffer.Length, received.RemoteEndPoint, Convert.ToHexString(received.Buffer));

            if (!OSCPacketCodec.TryDecode(received.Buffer, Options.DecodeOptions, out var packet, out var error))
            {
                _logger.LogWarning("Failed to decode OSC packet from {Remote}: {Error}", received.RemoteEndPoint, error);
                continue;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Decoded OSC packet {Kind} from {Remote}", packet!.Kind, received.RemoteEndPoint);

            await DispatchPacketAsync(packet!, received.RemoteEndPoint, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchPacketAsync(
        OSCPacket packet,
        IPEndPoint remote,
        OSCTimeTag? bundleTimeTag,
        CancellationToken cancellationToken)
    {
        if (packet.Kind == OSCPacketKind.Message)
        {
            var context = new OSCMessageContext(packet.Message!, remote, bundleTimeTag, DateTimeOffset.UtcNow);
            _ = await _router.DispatchAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bundle = packet.Bundle!;
        foreach (var child in bundle.Elements)
            await DispatchPacketAsync(child, remote, bundle.TimeTag, cancellationToken).ConfigureAwait(false);
    }

    private void HandleOversizePacket(int packetLength)
    {
        _oversizeDrops++;
        if (Options.OversizePolicy == OSCOversizePolicy.Throw)
            throw new InvalidOperationException($"OSC packet size {packetLength} exceeds configured max {Options.MaxPacketBytes}.");

        var now = DateTimeOffset.UtcNow;
        if (now - _lastOversizeLogUtc < Options.OversizeLogInterval)
            return;

        _lastOversizeLogUtc = now;
        _logger.LogWarning(
            "Dropped oversized OSC datagram {Length}B (> {MaxPacketBytes}B). Total dropped: {DroppedCount}",
            packetLength,
            Options.MaxPacketBytes,
            _oversizeDrops);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore dispose shutdown exceptions
        }

        _udpClient.Dispose();
        _disposed = true;
    }
}

