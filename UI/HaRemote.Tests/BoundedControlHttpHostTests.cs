using System.Net;
using System.Net.Sockets;
using HaRemote;
using Xunit;

namespace HaRemote.Tests;

/// <summary>
/// The SHARED transport contract both apps' control APIs run on (F-05): admission (both policies),
/// per-request deadlines, request-size guards, OPTIONS handling, and the bounded shutdown drain.
/// These are the mechanics that previously lived twice and drifted once (HaCue2 shipped unbounded
/// while HaPlay was bounded); one suite here is what keeps them converged.
/// </summary>
public sealed class BoundedControlHttpHostTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ControlHttpResult Err(int status, string message) =>
        new(status, $"{{\"error\":\"{message}\"}}");

    private static ControlHttpHostOptions Options(
        Func<ControlHttpRequest, CancellationToken, Task<ControlHttpResult>> dispatch) => new()
    {
        Dispatch = dispatch,
        Error = Err,
    };

    private static (BoundedControlHttpHost Host, int Port) Start(ControlHttpHostOptions options)
    {
        var port = FreePort();
        var host = BoundedControlHttpHost.TryStart([$"http://localhost:{port}/"], options, out var error);
        Assert.True(host is not null, $"host did not bind: {error}");
        return (host!, port);
    }

    [Fact]
    public async Task DispatchAnswers()
    {
        var (host, port) = Start(Options((request, _) =>
            Task.FromResult(new ControlHttpResult(200, $"{{\"path\":\"{request.Path}\"}}"))));
        await using (host)
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"http://localhost:{port}/hello")
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("/hello", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ADeadlineExceededDispatchAnswersTheCallerWithTheConfiguredResult()
    {
        var parked = new TaskCompletionSource<ControlHttpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => parked.Task,
            Error = Err,
            RequestDeadline = TimeSpan.FromMilliseconds(300),
            DeadlineExceeded = budget => Err(503, $"deadline {budget.TotalMilliseconds:0} ms"),
        };
        var (host, port) = Start(options);
        await using (host)
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync($"http://localhost:{port}/slow")
                    .WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("deadline", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                parked.TrySetResult(new ControlHttpResult(200, "{}"));
            }
        }
    }

    [Fact]
    public async Task ATokenIgnoringTimedOutDispatchKeepsItsAdmissionSlotUntilItEnds()
    {
        var firstDispatch = new TaskCompletionSource<ControlHttpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => Interlocked.Increment(ref dispatchCount) == 1
                ? firstDispatch.Task
                : Task.FromResult(new ControlHttpResult(200, "{}")),
            Error = Err,
            MaxConcurrentRequests = 1,
            OverCapacity = OverCapacityPolicy.Refuse,
            RequestDeadline = TimeSpan.FromMilliseconds(250),
            DeadlineExceeded = _ => Err(503, "deadline"),
        };
        var (host, port) = Start(options);
        await using (host)
        {
            try
            {
                using var client = new HttpClient();
                var timedOut = await client.GetAsync($"http://localhost:{port}/first")
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);

                // The caller has its answer, but the dispatch ignored cancellation and is still
                // live. It must continue occupying the only slot rather than allowing another
                // dispatch to accumulate behind it.
                var refused = await client.GetAsync($"http://localhost:{port}/second")
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                Assert.Contains("concurrent-request limit", await refused.Content.ReadAsStringAsync());
                Assert.Equal(1, Volatile.Read(ref dispatchCount));

                firstDispatch.TrySetResult(new ControlHttpResult(200, "{}"));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                HttpResponseMessage? admitted = null;
                while (DateTime.UtcNow < deadline)
                {
                    admitted = await client.GetAsync($"http://localhost:{port}/after");
                    if (admitted.StatusCode == HttpStatusCode.OK)
                        break;
                    admitted.Dispose();
                    admitted = null;
                    await Task.Delay(20);
                }

                Assert.NotNull(admitted);
                using (admitted)
                    Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            }
            finally
            {
                firstDispatch.TrySetResult(new ControlHttpResult(200, "{}"));
            }
        }
    }

    [Fact]
    public async Task RefusePolicyAnswers503WhenEverySlotIsTaken()
    {
        var parked = new TaskCompletionSource<ControlHttpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => parked.Task,
            Error = Err,
            MaxConcurrentRequests = 1,
            OverCapacity = OverCapacityPolicy.Refuse,
        };
        var (host, port) = Start(options);
        await using (host)
        {
            try
            {
                using var occupant = new HttpClient();
                var occupying = occupant.GetAsync($"http://localhost:{port}/first");
                // Let the first request take the only slot before probing.
                await Task.Delay(300);

                using var probe = new HttpClient();
                var refused = await probe.GetAsync($"http://localhost:{port}/second")
                    .WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                Assert.Contains("concurrent-request limit", await refused.Content.ReadAsStringAsync());

                parked.TrySetResult(new ControlHttpResult(200, "{}"));
                var first = await occupying.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }
            finally
            {
                parked.TrySetResult(new ControlHttpResult(200, "{}"));
            }
        }
    }

    [Fact]
    public async Task BackpressurePolicyQueuesInsteadOfRefusing()
    {
        var parked = new TaskCompletionSource<ControlHttpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => parked.Task,
            Error = Err,
            MaxConcurrentRequests = 1,
            OverCapacity = OverCapacityPolicy.Backpressure,
        };
        var (host, port) = Start(options);
        await using (host)
        {
            using var clientA = new HttpClient();
            using var clientB = new HttpClient();
            var first = clientA.GetAsync($"http://localhost:{port}/first");
            await Task.Delay(300);
            var second = clientB.GetAsync($"http://localhost:{port}/second");
            await Task.Delay(300);

            // Neither answered yet - the second is QUEUED, not refused.
            Assert.False(second.IsCompleted);

            parked.TrySetResult(new ControlHttpResult(200, "{}"));
            var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }
    }

    [Fact]
    public async Task ShutdownDoesNotWaitForeverOnAHungHandler()
    {
        var never = new TaskCompletionSource<ControlHttpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => never.Task,
            Error = Err,
            RequestDeadline = TimeSpan.FromSeconds(30),
            ShutdownDrainBudget = TimeSpan.FromMilliseconds(300),
        };
        var (host, port) = Start(options);

        using var client = new HttpClient();
        var inFlight = client.GetAsync($"http://localhost:{port}/hung");
        await Task.Delay(300);

        // Never released - and disposal must still complete inside its budget rather than holding
        // the whole application's teardown.
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        _ = inFlight;
        never.TrySetResult(new ControlHttpResult(200, "{}"));
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedBeforeDispatch()
    {
        var dispatched = false;
        var (host, port) = Start(Options((_, _) =>
        {
            dispatched = true;
            return Task.FromResult(new ControlHttpResult(200, "{}"));
        }));
        await using (host)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/x");
            request.Headers.TryAddWithoutValidation("X-Big", new string('a', 9 * 1024));
            var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(431, (int)response.StatusCode);
            Assert.False(dispatched);
        }
    }

    [Fact]
    public async Task OptionsIsAnsweredByTheHostWhenConfigured()
    {
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => Task.FromResult(new ControlHttpResult(200, "{}")),
            Error = Err,
            OptionsAllow = _ => "GET, POST",
        };
        var (host, port) = Start(options);
        await using (host)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Options, $"http://localhost:{port}/x");
            var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Contains("GET, POST", response.Content.Headers.Allow.Count > 0
                ? string.Join(", ", response.Content.Headers.Allow)
                : string.Join(", ", response.Headers.GetValues("Allow")));
        }
    }

    [Fact]
    public async Task ADispatchThatThrowsAnswersWithTheConfiguredFailureResult()
    {
        var options = new ControlHttpHostOptions
        {
            Dispatch = (_, _) => throw new InvalidOperationException("boom"),
            Error = Err,
            DispatchFailure = () => Err(500, "the request failed"),
        };
        var (host, port) = Start(options);
        await using (host)
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"http://localhost:{port}/x")
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("request failed", body);
            Assert.DoesNotContain("boom", body);
        }
    }
}
