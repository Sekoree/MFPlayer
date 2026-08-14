using System.Net;
using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The remote server is BOUNDED: admission-limited, deadlined, and drained on a budget.
/// </summary>
/// <remarks>
/// It used to spawn one unbounded handler task per accepted connection with no deadline, and its
/// shutdown waited on every handler indefinitely - so anything on the venue network could pile up
/// work without authenticating, and one dispatch stuck behind a wedged transport call could hold
/// application teardown hostage.
/// </remarks>
public sealed class RemoteApiBoundsTests
{
    /// <summary>A transport whose snapshot parks until released - the wedged-dispatch stand-in.</summary>
    private sealed class ParkedTransport : IRemoteApiTransport
    {
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? Previewing => null;
        public bool IsPaused => false;

        public async Task<ShowState> SnapshotAsync()
        {
            await Release.Task.ConfigureAwait(false);
            return ShowState.Idle;
        }

        public Task<Guid?> GoAsync(CueList list) => Task.FromResult<Guid?>(null);
        public Task<bool> FireAsync(Guid cueId) => Task.FromResult(true);
        public Task StopCueAsync(Guid cueId) => Task.CompletedTask;
        public Task<bool> StandbyAsync(CueList list, Guid? cueId) => Task.FromResult(true);
        public Task StopAllAsync() => Task.CompletedTask;
        public Task PanicAsync() => Task.CompletedTask;
        public Task SetPausedAsync(bool paused) => Task.CompletedTask;
    }

    private static int FreePort()
    {
        // Ask the OS for a free port the way every loopback test does; the tiny race between close
        // and reuse is acceptable in a test.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task AWedgedDispatchAnswersTheCallerWithinTheDeadline()
    {
        var transport = new ParkedTransport();
        var server = new RemoteApiServer(transport, () => new TestProject().Project, "secret")
        {
            RequestDeadline = TimeSpan.FromMilliseconds(300),
        };
        var port = FreePort();
        await server.StartAsync(port, lanAllowed: false);

        try
        {
            Assert.True(server.IsRunning);
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-HaCue2-Token", "secret");

            var response = await client
                .GetAsync($"http://localhost:{port}/api/v1/status")
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            transport.Release.TrySetResult();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShutdownDoesNotWaitForeverOnAHungHandler()
    {
        var transport = new ParkedTransport();
        var server = new RemoteApiServer(transport, () => new TestProject().Project, "secret")
        {
            RequestDeadline = TimeSpan.FromSeconds(30),
            ShutdownDrainBudget = TimeSpan.FromMilliseconds(300),
        };
        var port = FreePort();
        await server.StartAsync(port, lanAllowed: false);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-HaCue2-Token", "secret");
            var inFlight = client.GetAsync($"http://localhost:{port}/api/v1/status");

            // Let the request reach the parked dispatch before shutting down.
            await Task.Delay(300);

            // Never released - and disposal must still complete inside its budget rather than
            // holding the whole application's teardown.
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _ = inFlight; // the client's fate is the OS's business once the listener is gone
        }
        finally
        {
            transport.Release.TrySetResult();
        }
    }
}
