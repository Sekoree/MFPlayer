using System.Net;
using Microsoft.Extensions.Logging;
using OSCLib;

if (args.Length == 0)
{
    Console.WriteLine("OSCLib.Smoke usage:");
    Console.WriteLine("  listen <port>");
    Console.WriteLine("  send <host> <port> <address> [int]");
    return;
}

var mode = args[0].ToLowerInvariant();
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(o =>
        {
            o.TimestampFormat = "HH:mm:ss ";
            o.SingleLine = true;
        })
        .SetMinimumLevel(LogLevel.Debug);
});

if (mode == "listen")
{
    if (args.Length < 2 || !int.TryParse(args[1], out var listenPort))
    {
        Console.WriteLine("listen mode requires: listen <port>");
        return;
    }

    var server = new OSCServer(
        new OSCServerOptions
        {
            Port = listenPort,
            EnableTraceHexDump = false
        },
        loggerFactory.CreateLogger<OSCServer>());

    using var _ = server.RegisterHandler("//", (context, _) =>
    {
        Console.WriteLine($"{context.RemoteEndPoint}: {context.Message.Address} args={context.Message.Arguments.Count}");
        return ValueTask.CompletedTask;
    });

    Console.WriteLine($"Listening on UDP {listenPort}. Press Ctrl+C to stop.");
    await server.StartAsync();

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }

    await server.DisposeAsync();
    return;
}

if (mode == "send")
{
    if (args.Length < 4)
    {
        Console.WriteLine("send mode requires: send <host> <port> <address> [int]");
        return;
    }

    if (!int.TryParse(args[2], out var sendPort))
    {
        Console.WriteLine("Invalid port.");
        return;
    }

    var address = args[3];
    var value = args.Length > 4 && int.TryParse(args[4], out var parsed) ? parsed : 1;

    await using var client = new OSCClient(new IPEndPoint(Dns.GetHostAddresses(args[1]).First(), sendPort));
    await client.SendMessageAsync(address, [OSCArgs.I32(value)]);
    Console.WriteLine($"Sent {address} {value} to {args[1]}:{sendPort}");
    return;
}

Console.WriteLine($"Unknown mode '{mode}'.");

