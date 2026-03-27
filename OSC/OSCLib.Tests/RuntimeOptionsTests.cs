using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public class RuntimeOptionsTests
{
    [Fact]
    public async Task Server_StrictDecode_Default_Rejects_UnknownTag()
    {
        var port = Random.Shared.Next(20000, 45000);
        var logger = new TestLogger<OSCServer>();

        await using var server = new OSCServer(new OSCServerOptions { Port = port }, logger);

        var wasCalled = false;
        using var sub = server.RegisterHandler("//", (_, _) =>
        {
            wasCalled = true;
            return ValueTask.CompletedTask;
        });

        await server.StartAsync();
        await SendRawAsync(port, BuildUnknownTagPacket());
        await Task.Delay(150);

        Assert.False(wasCalled);
        Assert.True(logger.Count(LogLevel.Warning, "Failed to decode OSC packet") >= 1);
    }

    [Fact]
    public async Task Server_NonStrictDecode_CanPreserveUnknownTag()
    {
        var port = Random.Shared.Next(20000, 45000);
        var logger = new TestLogger<OSCServer>();

        var options = new OSCServerOptions
        {
            Port = port,
            DecodeOptions = new OSCDecodeOptions
            {
                StrictMode = false,
                PreserveUnknownArguments = true,
                UnknownTagByteLengthResolver = static (_, _) => 4
            }
        };

        await using var server = new OSCServer(options, logger);
        var received = new TaskCompletionSource<OSCUnknownArgument>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = server.RegisterHandler("/x", (context, _) =>
        {
            received.TrySetResult(context.Message.Arguments.Single().AsUnknown());
            return ValueTask.CompletedTask;
        });

        await server.StartAsync();
        await SendRawAsync(port, BuildUnknownTagPacket());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var unknown = await received.Task.WaitAsync(timeout.Token);

        Assert.Equal('z', unknown.Tag);
        Assert.Equal("DEADBEEF", Convert.ToHexString(unknown.RawData.Span));
    }

    [Fact]
    public async Task OversizeDropAndLog_IsThrottled_ByInterval()
    {
        var port = Random.Shared.Next(20000, 45000);
        var logger = new TestLogger<OSCServer>();

        var options = new OSCServerOptions
        {
            Port = port,
            MaxPacketBytes = 16,
            OversizePolicy = OSCOversizePolicy.DropAndLog,
            OversizeLogInterval = TimeSpan.FromMinutes(1)
        };

        await using var server = new OSCServer(options, logger);
        await server.StartAsync();

        var payload = new byte[64];
        for (var i = 0; i < 3; i++)
            await SendRawAsync(port, payload);

        await Task.Delay(150);

        Assert.Equal(1, logger.Count(LogLevel.Warning, "Dropped oversized OSC datagram"));
    }

    [Fact]
    public async Task OversizeDropAndLog_LogsAgain_AfterThrottleInterval()
    {
        var port = Random.Shared.Next(20000, 45000);
        var logger = new TestLogger<OSCServer>();

        var options = new OSCServerOptions
        {
            Port = port,
            MaxPacketBytes = 16,
            OversizePolicy = OSCOversizePolicy.DropAndLog,
            OversizeLogInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var server = new OSCServer(options, logger);
        await server.StartAsync();

        var payload = new byte[64];
        await SendRawAsync(port, payload);
        await Task.Delay(80);
        await SendRawAsync(port, payload);

        await Task.Delay(150);

        Assert.True(
            logger.Count(LogLevel.Warning, "Dropped oversized OSC datagram") >= 2,
            "Expected oversize warning to be emitted again after throttle interval elapsed.");
    }

    [Fact]
    public async Task OversizeThrowPolicy_FaultsReceiveLoop()
    {
        var port = Random.Shared.Next(20000, 45000);
        var logger = new TestLogger<OSCServer>();

        var options = new OSCServerOptions
        {
            Port = port,
            MaxPacketBytes = 16,
            OversizePolicy = OSCOversizePolicy.Throw,
            OversizeLogInterval = TimeSpan.Zero
        };

        await using var server = new OSCServer(options, logger);
        await server.StartAsync();

        await SendRawAsync(port, new byte[64]);
        var loopStopped = await WaitUntilAsync(static s => !s.IsRunning, server, TimeSpan.FromSeconds(2));

        Assert.True(loopStopped, "Expected server receive loop to fault/stop on oversized datagram in Throw policy.");
    }

    [Fact]
    public async Task Client_MaxPacketBytes_IsEnforced_PerClientOptions()
    {
        var clientOptions = new OSCClientOptions { MaxPacketBytes = 16 };
        await using var client = new OSCClient(new IPEndPoint(IPAddress.Loopback, 9), clientOptions);

        var hugeMessage = new OSCMessage("/big", [OSCArgs.Blob(new byte[128])]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(OSCPacket.FromMessage(hugeMessage)));
    }

    private static async Task SendRawAsync(int port, byte[] payload)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static byte[] BuildUnknownTagPacket()
    {
        return
        [
            0x2F, 0x78, 0x00, 0x00, // /x
            0x2C, 0x7A, 0x00, 0x00, // ,z
            0xDE, 0xAD, 0xBE, 0xEF
        ];
    }

    private static async Task<bool> WaitUntilAsync(Func<OSCServer, bool> predicate, OSCServer server, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(server))
                return true;

            await Task.Delay(20);
        }

        return predicate(server);
    }
}
