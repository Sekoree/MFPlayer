using System.Net;
using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public class UdpLoopbackTests
{
    [Fact]
    public async Task Server_Receives_Client_Message()
    {
        var port = Random.Shared.Next(20000, 45000);
        var options = new OSCServerOptions
        {
            Port = port,
            MaxPacketBytes = 8192
        };

        await using var server = new OSCServer(options);
        var received = new TaskCompletionSource<OSCMessageContext>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = server.RegisterHandler("/test/ping", (context, _) =>
        {
            received.TrySetResult(context);
            return ValueTask.CompletedTask;
        });

        await server.StartAsync();

        await using var client = new OSCClient(new IPEndPoint(IPAddress.Loopback, port));
        await client.SendMessageAsync("/test/ping", [OSCArgs.I32(123)]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var context = await received.Task.WaitAsync(timeout.Token);

        Assert.Equal("/test/ping", context.Message.Address);
        Assert.Equal(123, context.Message.Arguments.Single().AsInt32());
    }
}
