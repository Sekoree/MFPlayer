using Avalonia.Headless;
using Avalonia.Threading;
using System.Diagnostics;
using System.Reflection;
using HaPlay.Models;
using HaPlay.Remote;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Remote API routing: 1-based addressing, verb dispatch, and failure codes - all without
/// HTTP (the listener is a thin shell over <see cref="RemoteApiDispatcher.ExecuteAsync"/>).</summary>
public sealed class RemoteApiDispatcherTests
{
    /// <summary>Runs <paramref name="action"/> on the headless UI session and OBSERVES the result.
    /// <c>Dispatch</c> hands back a Task; discarding it (the shape this helper used to have) threw
    /// every assertion failure inside the body away, so each of these tests passed no matter what
    /// the dispatcher answered. Blocking here is safe - the body is synchronous, and the xunit
    /// thread is not the session's dispatcher thread (the async sibling is
    /// <see cref="HeadlessDispatchExtensions.DispatchAsync(HeadlessUnitTestSession, Func{Task}, CancellationToken)"/>).</summary>
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static (RemoteApiDispatcher Dispatcher, CuePlayerViewModel Cues, SoundboardWorkspaceViewModel Soundboard,
        List<MediaPlayerViewModel> Players) CreateDispatcher()
    {
        var cues = new CuePlayerViewModel();
        var soundboard = new SoundboardWorkspaceViewModel();
        var players = new List<MediaPlayerViewModel>
        {
            new(new OutputManagementViewModel(), "Player 1"),
        };
        return (new RemoteApiDispatcher(cues, () => players, soundboard, control: null), cues, soundboard, players);
    }

    private static RemoteApiResult Execute(RemoteApiDispatcher dispatcher, string path, string method = "POST",
        Dictionary<string, string>? query = null) =>
        dispatcher.ExecuteAsync(method, path, query).GetAwaiter().GetResult();

    /// <summary>Two loaded lists that both number their first cue "1" - the ambiguity list scoping exists
    /// to resolve.</summary>
    private static void TwoLists(CuePlayerViewModel cues) =>
        cues.ApplyCueLists(
        [
            new CueList { Name = "Act One", Nodes = { new MediaCueNode { Number = "1", Label = "Opening" } } },
            new CueList { Name = "Act Two", Nodes = { new MediaCueNode { Number = "1", Label = "Reprise" } } },
        ]);

    [Fact]
    public void ListInventory_EnumeratesLoadedLists_AndIsAGet()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();
            TwoLists(cues);

            var result = Execute(dispatcher, "/api/v1/lists", method: "GET");

            Assert.Equal(200, result.Status);
            Assert.Contains("Act One", result.Body, StringComparison.Ordinal);
            Assert.Contains("Act Two", result.Body, StringComparison.Ordinal);
            // The one mixed domain: the inventory reads, everything under it commands.
            Assert.Equal("GET, OPTIONS", RemoteApiDispatcher.AllowedMethodsFor("/api/v1/lists"));
            Assert.Equal("POST, OPTIONS",
                RemoteApiDispatcher.AllowedMethodsFor("/api/v1/lists/Act One/cues/1/go"));
        });
    }

    [Fact]
    public void ListScopedCue_AddressesTheNamedListNotTheSelectedOne()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();
            TwoLists(cues);

            // Both lists have a cue "1". Bare /cues/1 resolves in the SELECTED list first, so a
            // show-control system addressing "Act Two" must be able to say so.
            var result = Execute(dispatcher, "/api/v1/lists/Act Two/cues/1/go");

            Assert.Equal(200, result.Status);
        });
    }

    [Fact]
    public void ListScopedCue_SeparatesUnknownListFromUnknownCue()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();
            TwoLists(cues);

            // Different fixes, so different messages - one 404 covering both sends the caller looking in
            // the wrong place.
            var noList = Execute(dispatcher, "/api/v1/lists/Act Nine/cues/1/go");
            Assert.Equal(404, noList.Status);
            Assert.Contains("Act Nine", noList.Body, StringComparison.Ordinal);

            var noCue = Execute(dispatcher, "/api/v1/lists/Act One/cues/99/go");
            Assert.Equal(404, noCue.Status);
            Assert.Contains("99", noCue.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ListScopedCue_RejectsAMalformedPath()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();
            TwoLists(cues);

            Assert.Equal(404, Execute(dispatcher, "/api/v1/lists/Act One/go").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/lists/Act One/cues/1").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/lists/Act One/nodes/1/go").Status);
        });
    }

    [Fact]
    public void UnknownEndpoint_Returns404_AndBadMethod405()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(404, Execute(dispatcher, "/api/v1/nope").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/cues/launch").Status);
            Assert.Equal(405, Execute(dispatcher, "/api/v1/cues/go", method: "DELETE").Status);
            var getMutation = Execute(dispatcher, "/api/v1/cues/go", method: "GET");
            Assert.Equal(405, getMutation.Status);
            Assert.Equal("POST", getMutation.Allow);
            Assert.Equal("GET, OPTIONS", RemoteApiDispatcher.AllowedMethodsFor("/api/v1/status/detail"));
            Assert.Equal("POST, OPTIONS", RemoteApiDispatcher.AllowedMethodsFor("/api/v1/cues/go"));
        });
    }

    [Fact]
    public void Status_ReportsCounts()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            var result = Execute(dispatcher, "/api/v1/status", method: "GET");

            Assert.Equal(200, result.Status);
            Assert.Contains("\"players\":1", result.Body);
            Assert.Contains("\"soundboards\":1", result.Body);
        });
    }

    [Fact]
    public void CuesGo_FiresGoCommand_And409WhenNothingFireable()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();

            // Empty cue list: nothing fireable.
            Assert.Equal(409, Execute(dispatcher, "/api/v1/cues/go").Status);

            cues.AddEmptyMediaCue(); // selected media cue makes Go available
            Assert.Equal(200, Execute(dispatcher, "/api/v1/cues/go").Status);
        });
    }

    [Fact]
    public void CueByReference_ResolvesByNumberAndId_UnknownIs404()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, cues, _, _) = CreateDispatcher();
            var opener = new MediaCueNode
            {
                Number = "1",
                Label = "Opener",
                Source = new FilePlaylistItem("/tmp/opener.wav"),
            };
            cues.ApplyCueLists([new CueList { Nodes = [opener] }]);

            // The operator-facing cue NUMBER first (case-insensitive), then the cue's Guid id.
            var byNumber = Execute(dispatcher, "/api/v1/cues/1/go");
            Assert.Equal(200, byNumber.Status);
            Assert.Contains("go 1", byNumber.Body);
            Assert.Equal(200, Execute(dispatcher, $"/api/v1/cues/{opener.Id}/go").Status);

            // Unknown reference and unknown verb.
            var unknown = Execute(dispatcher, "/api/v1/cues/99/go");
            Assert.Equal(404, unknown.Status);
            Assert.Contains("99", unknown.Body);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/cues/1/eject").Status);

            // /stop is per-row: 409 while the cue is not running.
            Assert.Equal(409, Execute(dispatcher, "/api/v1/cues/1/stop").Status);
        });
    }

    [Fact]
    public void CueByReferenceGo_UnfireableCue_Is409_NotAMisleading200()
    {
        DispatchUi(static () =>
        {
            // A group that resolves to nothing used to be DROPPED by the fire path, which then fell
            // through to the standby cue: the API answered 200 while a completely unrelated cue
            // started playing. It must fail instead.
            var (dispatcher, cues, _, _) = CreateDispatcher();
            var empty = new CueGroupNode { Number = "1", Label = "Empty group", Children = [] };
            var other = new MediaCueNode
            {
                Number = "2",
                Label = "Not what you asked for",
                Source = new FilePlaylistItem("/tmp/other.wav"),
            };
            cues.ApplyCueLists([new CueList { Nodes = [empty, other] }]);
            var started = new List<Guid>();
            cues.MediaCueExecutor = (m, _) => { started.Add(m.Id); return Task.FromResult<string?>(null); };

            var result = Execute(dispatcher, "/api/v1/cues/1/go");

            Assert.Equal(409, result.Status);
            Assert.Contains("\"ok\":false", result.Body);
            Assert.Contains("nothing to fire", result.Body);
            Assert.Empty(started); // and above all: the OTHER cue did not start

            // The playable sibling still fires normally.
            Assert.Equal(200, Execute(dispatcher, "/api/v1/cues/2/go").Status);
        });
    }

    [Fact]
    public void CuesPause_RequiresActiveCue()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(409, Execute(dispatcher, "/api/v1/cues/pause").Status);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/cues/stop").Status); // stop is always legal
        });
    }

    [Fact]
    public void PlayerVolume_SetsClampedMasterVolume()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();

            var result = Execute(dispatcher, "/api/v1/players/1/volume",
                query: new Dictionary<string, string> { ["db"] = "-12.5" });
            Assert.Equal(200, result.Status);
            Assert.Equal(-12.5, players[0].MasterVolumeDb);

            Execute(dispatcher, "/api/v1/players/1/volume",
                query: new Dictionary<string, string> { ["db"] = "99" });
            Assert.Equal(12.0, players[0].MasterVolumeDb); // clamped

            Assert.Equal(400, Execute(dispatcher, "/api/v1/players/1/volume").Status); // missing param
        });
    }

    [Fact]
    public void PlayerHold_TogglesAndSetsExplicitly()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();

            Execute(dispatcher, "/api/v1/players/1/hold");
            Assert.True(players[0].HoldFallbackVideo); // toggle from off

            Execute(dispatcher, "/api/v1/players/1/hold",
                query: new Dictionary<string, string> { ["on"] = "false" });
            Assert.False(players[0].HoldFallbackVideo);
        });
    }

    [Fact]
    public void PlayerAddressing_ByIndexAndName_Unknown404()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, _) = CreateDispatcher();

            Assert.Equal(200, Execute(dispatcher, "/api/v1/players/Player 1/hold").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/2/play").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/nope/play").Status);
        });
    }

    [Fact]
    public void PlaylistItem_Plays_SelectsTab_AndValidatesIndices()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, _, players) = CreateDispatcher();
            var player = players[0];
            var secondTab = new PlaylistTabViewModel("Set B");
            secondTab.Items.Add(new FilePlaylistItem("/tmp/a.wav"));
            secondTab.Items.Add(new FilePlaylistItem("/tmp/b.wav"));
            player.PlaylistTabs.Add(secondTab);

            // Out of range item / playlist.
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/2/3").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/9/1").Status);

            // Valid: bare form (no verb) plays and selects the tab.
            var result = Execute(dispatcher, "/api/v1/players/1/2/2");
            Assert.Equal(200, result.Status);
            Assert.Same(secondTab, player.SelectedPlaylistTab);
            Assert.Contains("b", result.Body);

            // Explicit /play verb also accepted; unknown verbs are not.
            Assert.Equal(200, Execute(dispatcher, "/api/v1/players/1/2/1/play").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/players/1/2/1/eject").Status);
        });
    }

    [Fact]
    public void SoundboardTile_TapStopFade_RouteToCallbacks()
    {
        DispatchUi(static () =>
        {
            var (dispatcher, _, soundboard, _) = CreateDispatcher();
            var board = soundboard.Boards[0];
            var tile = board.Tiles[2]; // tile number 3
            board.BindTile(tile, "/tmp/sting.wav");
            var played = new List<Guid>();
            var stopped = new List<Guid>();
            var faded = new List<Guid>();
            soundboard.PlaySoundCallback = r => { played.Add(r.TileId); return Task.FromResult<string?>(null); };
            soundboard.StopSoundCallback = id => { stopped.Add(id); return Task.CompletedTask; };
            soundboard.FadeOutSoundCallback = id => { faded.Add(id); return Task.CompletedTask; };

            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3").Status); // bare = tap
            Assert.Equal([tile.Id], played);

            soundboard.OnSoundStarted(tile.Id);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/play").Status); // force restart
            Assert.Equal(2, played.Count);

            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/fade").Status);
            Assert.Equal([tile.Id], faded);
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3/stop").Status);
            Assert.Equal([tile.Id], stopped);

            // Edit mode must not turn a remote trigger into a selection. The tile has to be back at
            // rest first: /fade and /stop go straight to their callbacks (they are not taps), so
            // nothing has told the VM the sound ended - and a bare tap on a tile the VM still thinks
            // is PLAYING is a stop, not a play. (This assertion never actually ran until DispatchUi
            // started observing its body.)
            soundboard.OnSoundEnded(tile.Id);
            soundboard.IsEditMode = true;
            Assert.Equal(200, Execute(dispatcher, "/api/v1/soundboards/1/3").Status);
            Assert.Equal(3, played.Count);
            Assert.Null(soundboard.SelectedTile);

            // Unbound tile → 409; bad indices → 404.
            Assert.Equal(409, Execute(dispatcher, "/api/v1/soundboards/1/1").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/soundboards/9/1").Status);
            Assert.Equal(404, Execute(dispatcher, "/api/v1/soundboards/1/999").Status);
        });
    }

    [Fact]
    public async Task HttpServer_RoundTrips_StatusCommandAndNotFound()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        var (unauthorizedCode, statusCode, statusBody, tapCode, notFoundCode, getMutationCode, getMutationAllow) =
            await session.DispatchAsync(async () =>
        {
            var cues = new CuePlayerViewModel();
            var soundboard = new SoundboardWorkspaceViewModel();
            var played = 0;
            soundboard.PlaySoundCallback = _ => { played++; return Task.FromResult<string?>(null); };
            soundboard.Boards[0].BindTile(soundboard.Boards[0].Tiles[0], "/tmp/x.wav");
            var dispatcher = new RemoteApiDispatcher(cues, () => [], soundboard, null);

            using var server = new RestApiServer();
            var port = GetFreePort();
            const string token = "test-token";
            Assert.True(server.Start(port, dispatcher, token));
            var baseUrl = server.BaseUrl!;

            // finally, not a trailing call: a failed assertion below must not skip the drain, or the leak comes
            // back only on the runs where something ELSE was already wrong.
            try
            {
                using var http = new HttpClient();
                var unauthorized = await http.GetAsync($"{baseUrl}/api/v1/status");
                var status = await http.GetAsync($"{baseUrl}/api/v1/status?key={token}");
                var body = await status.Content.ReadAsStringAsync();
                using var bearer = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/soundboards/1/1/tap");
                bearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var tap = await http.SendAsync(bearer);
                using var notFoundRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/bogus?key={token}");
                var notFound = await http.SendAsync(notFoundRequest);
                var getMutation = await http.GetAsync($"{baseUrl}/api/v1/soundboards/1/1/tap?key={token}");
                return ((int)unauthorized.StatusCode, (int)status.StatusCode, body, (int)tap.StatusCode,
                    (int)notFound.StatusCode, (int)getMutation.StatusCode,
                    getMutation.Content.Headers.Allow.SingleOrDefault());
            }
            finally
            {
                StopAndDrainListener(server);
            }
        }, CancellationToken.None);

        Assert.Equal(401, unauthorizedCode);
        Assert.Equal(200, statusCode);
        Assert.Contains("\"ok\":true", statusBody);
        Assert.Equal(200, tapCode);
        Assert.Equal(404, notFoundCode);
        Assert.Equal(405, getMutationCode);
        Assert.Equal("POST", getMutationAllow);
    }

    [Fact]
    public async Task HttpServer_OptionalToken_OpenWhenUnset_RequiredWhenSet()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        var (noTokenStatus, tokenNoKeyStatus, tokenWithKeyStatus) = await session.DispatchAsync(async () =>
        {
            var dispatcher = new RemoteApiDispatcher(
                new CuePlayerViewModel(), () => [], new SoundboardWorkspaceViewModel(), null);
            using var http = new HttpClient();

            // No token configured → open (closed-LAN automation): status answers without a key.
            using (var open = new RestApiServer())
            {
                var port = GetFreePort();
                Assert.True(open.Start(port, dispatcher, accessToken: null));
                try
                {
                    var r = await http.GetAsync($"{open.BaseUrl}/api/v1/status");
                    var noToken = (int)r.StatusCode;

                    // Token configured → required: unauthenticated 401, correct key 200.
                    using var secured = new RestApiServer();
                    var port2 = GetFreePort();
                    Assert.True(secured.Start(port2, dispatcher, "secret-token"));
                    try
                    {
                        var unauth = (int)(await http.GetAsync($"{secured.BaseUrl}/api/v1/status")).StatusCode;
                        var auth = (int)(await http.GetAsync($"{secured.BaseUrl}/api/v1/status?key=secret-token")).StatusCode;
                        return (noToken, unauth, auth);
                    }
                    finally
                    {
                        StopAndDrainListener(secured); // BOTH servers leak handlers, not just the last one
                    }
                }
                finally
                {
                    StopAndDrainListener(open);
                }
            }
        }, CancellationToken.None);

        Assert.Equal(200, noTokenStatus);
        Assert.Equal(401, tokenNoKeyStatus);
        Assert.Equal(200, tokenWithKeyStatus);
    }

    [Fact]
    public async Task HttpServer_Stop_DoesNotBlockUiThreadWhenDispatchIsQueued()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteApiDispatcherTests).Assembly);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Task<HttpResponseMessage>? request = null;

        var elapsed = await session.DispatchGuarded(() =>
        {
            var dispatcher = new RemoteApiDispatcher(
                new CuePlayerViewModel(), () => [], new SoundboardWorkspaceViewModel(), null);
            using var server = new RestApiServer();
            var port = GetFreePort();
            Assert.True(server.Start(port, dispatcher, accessToken: null));

            // Keep the UI thread occupied long enough for the listener to queue its UI dispatch, then stop
            // from that same UI thread. A synchronous handler drain waits on itself here (the old 2 s stall).
            request = http.PostAsync($"{server.BaseUrl}/api/v1/cues/stop", content: null);
            Thread.Sleep(100);
            var acceptLoop = PrivateField<Task>(server, "_acceptLoop"); // Stop nulls it; capture first
            var started = Stopwatch.GetTimestamp();
            server.Stop();
            var stopCost = Stopwatch.GetElapsedTime(started);

            // Measured BEFORE this: the drain below must not count towards the regression bound.
            DrainListenerHandlers(server, acceptLoop);
            return stopCost;
        }, CancellationToken.None);

        // Regression guard: the pre-fix bug blocked the UI thread ~2 s (a synchronous handler drain waiting on
        // itself). The fixed path returns near-instantly, so the bound only needs to sit below that 2 s stall.
        // 1500 ms tolerates GC/scheduling jitter on an overloaded shared CI runner (a real 679 ms sample was
        // seen when a normally-12 s assembly took ~7 min) while still catching the regression.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1500), $"Stop blocked the UI thread for {elapsed.TotalMilliseconds:0} ms");
        try
        {
            using var response = await request!.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Cancellation/connection close is the expected outcome; the assertion is that Stop returns promptly.
        }
    }

    [Fact]
    public void RemoteApi_CopyUrls_AreTokenless_ForHeaderAuth()
    {
        // API-01: the token is NEVER embedded in a copied URL (it would land in the clipboard, shell history, or
        // a shared controller config). A token-protected server expects the X-HaPlay-Api-Key header instead; the
        // server still accepts ?key= for manual use, but the app does not generate it.
        var previousBase = RemoteApi.BaseUrl;
        try
        {
            RemoteApi.BaseUrl = "http://localhost:8990";

            var url = RemoteApi.TileTapUrl(2, 4);

            Assert.Equal("http://localhost:8990/api/v1/soundboards/2/4/tap", url);
            Assert.DoesNotContain("key=", url);
            Assert.DoesNotContain("token=", url);
        }
        finally
        {
            RemoteApi.BaseUrl = previousBase;
        }
    }

    /// <summary>
    /// Waits out the REST listener's still-running request handlers, on the UI thread that owns this test's
    /// headless <c>Application</c>.
    /// <para>WHY (a real flake, caught on <c>Status_ReportsCounts</c> in a contended full-solution run):
    /// <see cref="RestApiServer.Stop"/> drains ASYNCHRONOUSLY on purpose - that is the very regression this
    /// test guards - so without this the handler is still alive on a thread-pool thread when the test
    /// returns. Its first act is <c>Dispatcher.UIThread.CheckAccess()</c> (RemoteApiDispatcher.cs:94). Test
    /// isolation is PerTest, so the session unbinds <c>Dispatcher.UIThread</c> the moment this dispatch ends;
    /// a handler that reaches that line afterwards REBINDS UIThread to its own pool thread, and the next
    /// test's application init then dies in the compositor with "The calling thread cannot access this
    /// object because a different thread owns it". Whichever test ran next took the blame.</para>
    /// Deterministic by construction rather than by waiting: the accept loop exits on Stop's cancellation and
    /// (production's own invariant) no handler can be tracked after that, so an empty in-flight set is proof
    /// that nothing else will touch the dispatcher. Pumping is required because a queued
    /// <c>Dispatcher.UIThread.InvokeAsync</c> only completes while the UI thread runs jobs.
    /// </summary>
    /// <summary>Stops a listener and waits out its in-flight handlers, in the ONE order that is safe: capture
    /// the accept loop (Stop nulls the field), stop, then drain. Every listener test must end this way before
    /// its dispatch returns - see <see cref="DrainListenerHandlers"/> for what a surviving handler does to the
    /// next test. Only <see cref="HttpServer_Stop_DoesNotBlockUiThreadWhenDispatchIsQueued"/> spells the three
    /// steps out inline, because it has to time the Stop call by itself.</summary>
    private static void StopAndDrainListener(RestApiServer server)
    {
        var acceptLoop = PrivateField<Task>(server, "_acceptLoop"); // Stop nulls it; capture first
        server.Stop();
        DrainListenerHandlers(server, acceptLoop);
    }

    private static void DrainListenerHandlers(RestApiServer server, Task? acceptLoop)
    {
        var limit = TimeSpan.FromSeconds(10);
        var elapsed = Stopwatch.StartNew();
        while (acceptLoop is { IsCompleted: false } && elapsed.Elapsed < limit)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        while (InFlightHandlerCount(server) > 0 && elapsed.Elapsed < limit)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(acceptLoop is null or { IsCompleted: true }, "the REST accept loop did not exit after Stop");
        Assert.Equal(0, InFlightHandlerCount(server));
    }

    private static int InFlightHandlerCount(RestApiServer server)
    {
        var gate = PrivateField<object>(server, "_inflightGate")!;
        var inflight = PrivateField<HashSet<Task>>(server, "_inflight")!;
        lock (gate)
            return inflight.Count;
    }

    /// <summary>Reads a private <see cref="RestApiServer"/> field - the drain above needs the listener's own
    /// bookkeeping and the type deliberately exposes no completion hook. Fails loudly if a field is renamed
    /// rather than silently skipping the drain (that would bring the flake back).</summary>
    private static T? PrivateField<T>(RestApiServer server, string name)
        where T : class
    {
        var field = typeof(RestApiServer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(field is not null, $"RestApiServer.{name} is gone - update this test's listener drain");
        return (T?)field!.GetValue(server);
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void TileGridIndex_Is1BasedRowMajor_AndSurvivesResize()
    {
        DispatchUi(static () =>
        {
            var workspace = new SoundboardWorkspaceViewModel();
            var board = workspace.Boards[0];

            Assert.Equal(1, board.Tiles[0].GridIndex);
            Assert.Equal(board.Columns + 1, board.Tiles[board.Columns].GridIndex); // row 1, col 0

            board.Columns = 4;
            for (var i = 0; i < board.Tiles.Count; i++)
                Assert.Equal(i + 1, board.Tiles[i].GridIndex);
        });
    }
}
