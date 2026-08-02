using System.Text.Json;
using Avalonia.Threading;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Remote;

/// <summary>HTTP-status-shaped outcome of one remote command.</summary>
public readonly record struct RemoteApiResult(int Status, string Body, string? Allow = null)
{
    // JsonEncodedText (not JsonSerializer) - the app publishes NativeAOT with source-gen-only
    // serialization, and these tiny payloads don't justify a context type.
    public static RemoteApiResult Ok(string message) =>
        new(200, $"{{\"ok\":true,\"message\":\"{JsonEncodedText.Encode(message)}\"}}");

    public static RemoteApiResult Fail(int status, string error) =>
        new(status, $"{{\"ok\":false,\"error\":\"{JsonEncodedText.Encode(error)}\"}}");

    public static RemoteApiResult MethodNotAllowed(string allow) =>
        new(405, $"{{\"ok\":false,\"error\":\"Use {JsonEncodedText.Encode(allow)}.\"}}", allow);
}

/// <summary>
/// Routes remote API paths onto the view models. Transport-agnostic (the HTTP listener and unit
/// tests both call <see cref="ExecuteAsync"/>); every handler hops to the UI thread, validates its
/// target and *kicks off* the command without awaiting playback - transports can block for seconds
/// on prefill, and a remote controller needs the request to return immediately.
///
/// URL scheme (all indices 1-based, matching the UI labels; status is GET, mutations are POST):
///   /api/v1/status
///   /api/v1/endpoints                  (GET: the route table + per-domain counters)
///   /api/v1/cues/go|pause|resume|stop|panic
///   /api/v1/cues/{cue}/go|stop
///   /api/v1/lists                      (GET: the loaded cue lists)
///   /api/v1/lists/{list}/cues/{cue}/go|stop
///   /api/v1/players/{player}/play|pause|toggle|stop|next|prev
///   /api/v1/players/{player}/volume?db=-10
///   /api/v1/players/{player}/hold[?on=true|false]
///   /api/v1/players/{player}/{playlist}/{item}[/play]
///   /api/v1/soundboards/stop
///   /api/v1/soundboards/{board}/{tile}[/tap|play|stop|fade]
///   /api/v1/control/arm|disarm
/// </summary>
public sealed class RemoteApiDispatcher
{
    /// <summary>Bind/auth posture surfaced by /api/v1/status (review P2-7): controllers and
    /// operators can verify whether the API is network-reachable and whether it requires a token
    /// without inspecting the app's settings.</summary>
    public bool LanBindingEnabled { get; init; }

    /// <summary>True when a token is configured (auth "token"); false = open access.</summary>
    public bool TokenConfigured { get; init; }

    private readonly CuePlayerViewModel _cuePlayer;
    private readonly Func<IReadOnlyList<MediaPlayerViewModel>> _players;
    private readonly SoundboardWorkspaceViewModel _soundboard;
    private readonly ControlWorkspaceViewModel? _control;

    /// <summary>Per-domain request/failure totals, surfaced by <c>GET /api/v1/endpoints</c>.</summary>
    private readonly RemoteApiCounters _counters = new();

    public RemoteApiDispatcher(
        CuePlayerViewModel cuePlayer,
        Func<IReadOnlyList<MediaPlayerViewModel>> players,
        SoundboardWorkspaceViewModel soundboard,
        ControlWorkspaceViewModel? control)
    {
        _cuePlayer = cuePlayer;
        _players = players;
        _soundboard = soundboard;
        _control = control;
    }

    public async Task<RemoteApiResult> ExecuteAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken cancellationToken = default)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        if (index < segments.Length && segments[index].Equals("api", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index < segments.Length && segments[index].Equals("v1", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index >= segments.Length)
            return RemoteApiResult.Fail(404, "Unknown endpoint.");

        var domain = segments[index].ToLowerInvariant();
        var rest = segments[(index + 1)..];
        query ??= new Dictionary<string, string>();

        var allow = ApplicationMethodFor(domain, rest.Length);
        if (!string.Equals(method, allow, StringComparison.OrdinalIgnoreCase))
            return RemoteApiResult.MethodNotAllowed(allow);

        cancellationToken.ThrowIfCancellationRequested();

        // VM access has UI-thread affinity; tests already run on the headless UI thread.
        if (Dispatcher.UIThread.CheckAccess())
            return Counted(domain, Handle(domain, rest, query));
        return Counted(domain, await Dispatcher.UIThread.InvokeAsync(
            () => Handle(domain, rest, query),
            DispatcherPriority.Normal,
            cancellationToken));
    }

    /// <summary>Records a dispatched request against its domain and passes the result through.</summary>
    /// <remarks>Wraps the RESULT rather than the call so both the on-thread and marshalled paths count
    /// identically - and so a 404/405 is counted as a failure, which is the case an operator is trying to
    /// diagnose when a controller "does nothing".</remarks>
    private RemoteApiResult Counted(string domain, RemoteApiResult result)
    {
        _counters.Record(domain, result.Status);
        return result;
    }

    /// <summary>Value for HTTP <c>Allow</c>: the one legal application method plus OPTIONS.</summary>
    public static string AllowedMethodsFor(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        if (index < segments.Length && segments[index].Equals("api", StringComparison.OrdinalIgnoreCase))
            index++;
        if (index < segments.Length && segments[index].Equals("v1", StringComparison.OrdinalIgnoreCase))
            index++;
        var domain = index < segments.Length ? segments[index].ToLowerInvariant() : string.Empty;
        var rest = index + 1 < segments.Length ? segments.Length - index - 1 : 0;
        return ApplicationMethodFor(domain, rest) + ", OPTIONS";
    }

    /// <summary>The one legal application method - delegated to the route table, which is the single
    /// source of truth for both the request gate and the <c>Allow</c> header.</summary>
    private static string ApplicationMethodFor(string domain, int restSegments) =>
        RemoteApiRoutes.MethodFor(domain, restSegments);

    private RemoteApiResult Handle(string domain, string[] rest, IReadOnlyDictionary<string, string> query) =>
        domain switch
        {
            "status" => HandleStatus(),
            "endpoints" => HandleEndpoints(),
            "cues" => HandleCues(rest),
            "lists" => HandleLists(rest),
            "players" => HandlePlayers(rest, query),
            "soundboards" => HandleSoundboards(rest),
            "control" => HandleControl(rest),
            _ => RemoteApiResult.Fail(404, $"Unknown endpoint '{domain}'."),
        };

    private RemoteApiResult HandleStatus()
    {
        var boards = _soundboard.Boards.Count;
        var players = _players().Count;
        var lan = LanBindingEnabled ? "true" : "false";
        var auth = TokenConfigured ? "token" : "open";
        return new RemoteApiResult(200,
            $"{{\"ok\":true,\"app\":\"HaPlay\",\"players\":{players},\"soundboards\":{boards},\"lan\":{lan},\"auth\":\"{auth}\"}}");
    }

    /// <summary>
    /// <c>GET /api/v1/endpoints</c> - the route table and per-domain counters.
    /// </summary>
    /// <remarks>
    /// The API could not describe itself: dispatch is a nested switch over string literals and the only
    /// statement of the surface was a class comment. A controller had to be written against documentation
    /// that nothing checked. The counters answer the other question an operator actually asks when a cue
    /// does not fire - whether the request arrived at all.
    /// </remarks>
    private RemoteApiResult HandleEndpoints()
    {
        var routes = RemoteApiRoutes.All.Select(r =>
            $"{{\"method\":\"{r.Method}\",\"path\":{Quote(r.Pattern)},\"summary\":{Quote(r.Summary)}}}");
        var counters = _counters.Snapshot().Select(c =>
            $"{{\"domain\":{Quote(c.Domain)},\"requests\":{c.Requests},\"failures\":{c.Failures}}}");
        return new RemoteApiResult(200,
            $"{{\"ok\":true,\"routes\":[{string.Join(",", routes)}],\"counters\":[{string.Join(",", counters)}]}}");
    }

    private RemoteApiResult HandleCues(string[] rest)
    {
        // Two segments = per-cue addressing: /cues/{cue}/go|stop (never ambiguous with the
        // transport verbs below, which are always a single segment).
        if (rest.Length == 2)
            return HandleCueByReference(rest[0], rest[1]);

        if (rest.Length != 1)
            return RemoteApiResult.Fail(404, "Cue endpoint: /cues/go|pause|resume|stop|panic or /cues/{cue}/go|stop.");

        switch (rest[0].ToLowerInvariant())
        {
            case "go":
                if (!_cuePlayer.GoCommand.CanExecute(null))
                    return RemoteApiResult.Fail(409, "Nothing to fire (no fireable cues).");
                _ = _cuePlayer.GoCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("go");
            case "pause":
                if (_cuePlayer.CurrentCueNode is null)
                    return RemoteApiResult.Fail(409, "No active cue to pause.");
                if (!_cuePlayer.IsTransportPaused)
                    _cuePlayer.PauseCommand.Execute(null);
                return RemoteApiResult.Ok("paused");
            case "resume":
                if (_cuePlayer.CurrentCueNode is null)
                    return RemoteApiResult.Fail(409, "No active cue to resume.");
                if (_cuePlayer.IsTransportPaused)
                    _cuePlayer.PauseCommand.Execute(null);
                return RemoteApiResult.Ok("resumed");
            case "stop":
                _cuePlayer.StopCommand.Execute(null);
                return RemoteApiResult.Ok("stopped");
            case "panic":
                _cuePlayer.PanicCommand.Execute(null);
                return RemoteApiResult.Ok("panic");
            default:
                return RemoteApiResult.Fail(404, $"Unknown cue command '{rest[0]}'.");
        }
    }

    /// <summary>Per-cue transport: <c>{cue}</c> is the operator-facing cue NUMBER resolved in the
    /// selected list first (the labels the operator reads, like every other address here), falling
    /// back to the cue's Guid id - and then to a number in the other loaded lists, since the
    /// cross-list merged session makes every list's cues fireable. <c>/go</c> fires through the exact operator-selected GO path (pre-waits,
    /// group semantics, jump-chain reset, Now-Playing - identical to a manual fire); <c>/stop</c>
    /// stops that one cue when it is running.
    /// <para>The fire is kicked off without awaiting playback (the class contract), so <c>/go</c>
    /// pre-checks <see cref="CuePlayerViewModel.CanFireCue"/> synchronously: a cue that resolves to
    /// nothing (empty group, playlist group with no items) answers 409 instead of a 200 the caller
    /// would read as "it played".</para></summary>
    private RemoteApiResult HandleCueByReference(string cueRef, string verb)
    {
        var cue = _cuePlayer.FindCueByReference(cueRef);
        if (cue is null)
            return RemoteApiResult.Fail(404,
                $"Unknown cue '{cueRef}' - no cue number or id matches in any loaded cue list.");

        return FireOrStopCue(cue, cueRef, verb);
    }

    /// <summary>
    /// List-scoped addressing: <c>/lists</c> enumerates the loaded cue lists, and
    /// <c>/lists/{list}/cues/{cue}/go|stop</c> addresses a cue <em>within</em> one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unambiguous counterpart to <c>/cues/{cue}</c>. That one resolves a bare cue number in the
    /// operator's SELECTED list first and only then in the others, which means the same request can hit a
    /// different cue after the operator clicks another tab. Fine for a person at the desk; not fine for a
    /// show-control system, which knows which list it means.
    /// </para>
    /// <para>
    /// <c>POST /lists/{list}/go</c> - "fire whatever is next in that list" - is deliberately NOT here. It
    /// needs a standby pointer per list, and HaPlay keeps exactly one (for the selected list) written from
    /// ten places across the visible transport. That is a feature, not a route, and guessing its semantics
    /// (does a remote GO move the pointer the operator later sees?) would be worse than not shipping it.
    /// </para>
    /// </remarks>
    private RemoteApiResult HandleLists(string[] rest)
    {
        if (rest.Length == 0)
            return ListInventory();

        // {list}/cues/{cue}/{verb}
        if (rest.Length != 4 || !string.Equals(rest[1], "cues", StringComparison.OrdinalIgnoreCase))
            return RemoteApiResult.Fail(404, "List endpoint: /lists or /lists/{list}/cues/{cue}/go|stop.");

        if (_cuePlayer.FindCueListByReference(rest[0]) is null)
            return RemoteApiResult.Fail(404, $"Unknown cue list '{rest[0]}'.");

        // Split the two 404s: "no such list" and "no such cue in that list" are different fixes, and one
        // message covering both sends the caller looking in the wrong place.
        var cue = _cuePlayer.FindCueInList(rest[0], rest[2]);
        if (cue is null)
            return RemoteApiResult.Fail(404, $"Cue list '{rest[0]}' has no cue '{rest[2]}'.");

        return FireOrStopCue(cue, rest[2], rest[3]);
    }

    /// <summary>The loaded cue lists, so a caller can discover what it may address.</summary>
    private RemoteApiResult ListInventory()
    {
        var rows = _cuePlayer.CueLists.Select(list =>
            $"{{\"name\":{Quote(list.Name)},\"id\":\"{list.RuntimeId}\",\"cues\":{list.Nodes.Count}}}");
        return new RemoteApiResult(200, $"{{\"ok\":true,\"lists\":[{string.Join(",", rows)}]}}");
    }

    private static string Quote(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The shared go/stop tail of both cue-addressing forms, so they cannot drift apart.</summary>
    private RemoteApiResult FireOrStopCue(CueNodeViewModel cue, string cueRef, string verb)
    {
        switch (verb.ToLowerInvariant())
        {
            case "go":
                if (!_cuePlayer.CanFireCue(cue))
                    return RemoteApiResult.Fail(409,
                        $"Cue '{cueRef}' has nothing to fire (an empty group, or a playlist group with no items).");
                _ = _cuePlayer.FireTriggeredCueSafeAsync(cue, nameof(Strings.CueRemoteFiredStatusFormat));
                return RemoteApiResult.Ok($"go {DescribeCue(cue)}");
            case "stop":
                return _cuePlayer.TryStopCue(cue)
                    ? RemoteApiResult.Ok($"stop {DescribeCue(cue)}")
                    : RemoteApiResult.Fail(409, $"Cue '{cueRef}' is not playing.");
            default:
                return RemoteApiResult.Fail(404, $"Unknown cue command '{verb}' - use go or stop.");
        }
    }

    private static string DescribeCue(CueNodeViewModel cue) =>
        string.IsNullOrWhiteSpace(cue.Number) ? cue.Label : cue.Number;

    private RemoteApiResult HandlePlayers(string[] rest, IReadOnlyDictionary<string, string> query)
    {
        if (rest.Length == 0)
            return RemoteApiResult.Fail(404, "Player endpoint: /players/{player}/…");

        var players = _players();
        var player = ResolvePlayer(players, rest[0]);
        if (player is null)
            return RemoteApiResult.Fail(404, $"Unknown player '{rest[0]}' ({players.Count} available).");

        if (rest.Length == 1)
            return RemoteApiResult.Fail(404, "Missing player command.");

        // Numeric second segment = playlist addressing: /{player}/{playlist}/{item}[/play]
        if (int.TryParse(rest[1], out var playlistNumber))
            return HandlePlaylistItem(player, playlistNumber, rest);

        switch (rest[1].ToLowerInvariant())
        {
            case "play":
                _ = player.PlayCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("play");
            case "pause":
                _ = player.PauseCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("pause");
            case "toggle":
                _ = player.TogglePlayPauseCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("toggle");
            case "stop":
                _ = player.StopCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("stop");
            case "next":
                _ = player.NextTrackCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("next");
            case "prev" or "previous":
                _ = player.PreviousTrackCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("previous");
            case "volume":
                if (!query.TryGetValue("db", out var dbText)
                    || !double.TryParse(dbText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var db))
                    return RemoteApiResult.Fail(400, "volume requires ?db=<-60..12>.");
                player.MasterVolumeDb = Math.Clamp(db, -60.0, 12.0);
                return RemoteApiResult.Ok($"volume {player.MasterVolumeDb:0.#} dB");
            case "hold":
                player.HoldFallbackVideo = query.TryGetValue("on", out var onText)
                    ? IsTruthy(onText)
                    : !player.HoldFallbackVideo;
                return RemoteApiResult.Ok(player.HoldFallbackVideo ? "hold on" : "hold off");
            default:
                return RemoteApiResult.Fail(404, $"Unknown player command '{rest[1]}'.");
        }
    }

    private static RemoteApiResult HandlePlaylistItem(MediaPlayerViewModel player, int playlistNumber, string[] rest)
    {
        if (rest.Length < 3 || !int.TryParse(rest[2], out var itemNumber))
            return RemoteApiResult.Fail(404, "Playlist endpoint: /players/{player}/{playlist}/{item}[/play].");
        if (rest.Length > 4 || (rest.Length == 4 && !rest[3].Equals("play", StringComparison.OrdinalIgnoreCase)))
            return RemoteApiResult.Fail(404, $"Unknown playlist item command '{rest[^1]}'.");

        if (playlistNumber < 1 || playlistNumber > player.PlaylistTabs.Count)
            return RemoteApiResult.Fail(404, $"Playlist {playlistNumber} not found ({player.PlaylistTabs.Count} available).");
        var tab = player.PlaylistTabs[playlistNumber - 1];
        if (itemNumber < 1 || itemNumber > tab.Items.Count)
            return RemoteApiResult.Fail(404, $"Item {itemNumber} not found ({tab.Items.Count} in playlist).");

        var item = tab.Items[itemNumber - 1];
        player.SelectedPlaylistTab = tab;
        _ = player.PlayPlaylistItemAsync(item);
        return RemoteApiResult.Ok($"playing {item.DisplayName}");
    }

    private RemoteApiResult HandleSoundboards(string[] rest)
    {
        if (rest.Length == 1 && rest[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _ = _soundboard.StopAllCommand.ExecuteAsync(null);
            return RemoteApiResult.Ok("stopping all");
        }

        if (rest.Length is < 2 or > 3
            || !int.TryParse(rest[0], out var boardNumber)
            || !int.TryParse(rest[1], out var tileNumber))
            return RemoteApiResult.Fail(404, "Soundboard endpoint: /soundboards/{board}/{tile}[/tap|play|stop|fade] or /soundboards/stop.");

        if (boardNumber < 1 || boardNumber > _soundboard.Boards.Count)
            return RemoteApiResult.Fail(404, $"Soundboard {boardNumber} not found ({_soundboard.Boards.Count} available).");
        var board = _soundboard.Boards[boardNumber - 1];
        if (tileNumber < 1 || tileNumber > board.Tiles.Count)
            return RemoteApiResult.Fail(404, $"Tile {tileNumber} not found ({board.Tiles.Count} on board).");
        var tile = board.Tiles[tileNumber - 1];
        if (!tile.IsBound)
            return RemoteApiResult.Fail(409, $"Tile {tileNumber} has no sound bound.");

        var verb = rest.Length == 3 ? rest[2].ToLowerInvariant() : "tap";
        switch (verb)
        {
            case "tap":
                _ = _soundboard.TriggerTileAsync(tile);
                return RemoteApiResult.Ok($"tap {tile.DisplayName}");
            case "play":
                _ = _soundboard.PlayTileAsync(tile);
                return RemoteApiResult.Ok($"play {tile.DisplayName}");
            case "stop":
                if (_soundboard.StopSoundCallback is { } stop)
                    _ = stop(tile.Id);
                return RemoteApiResult.Ok($"stop {tile.DisplayName}");
            case "fade":
                if (_soundboard.FadeOutSoundCallback is { } fade)
                    _ = fade(tile.Id);
                return RemoteApiResult.Ok($"fade {tile.DisplayName}");
            default:
                return RemoteApiResult.Fail(404, $"Unknown tile command '{verb}'.");
        }
    }

    private RemoteApiResult HandleControl(string[] rest)
    {
        if (_control is null)
            return RemoteApiResult.Fail(503, "Control system not available.");
        if (rest.Length != 1)
            return RemoteApiResult.Fail(404, "Control endpoint: /control/arm|disarm.");

        switch (rest[0].ToLowerInvariant())
        {
            case "arm" or "enable":
                if (!_control.IsArmed)
                    _ = _control.ToggleArmCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("arming");
            case "disarm" or "disable":
                if (_control.IsArmed)
                    _ = _control.ToggleArmCommand.ExecuteAsync(null);
                return RemoteApiResult.Ok("disarming");
            default:
                return RemoteApiResult.Fail(404, $"Unknown control command '{rest[0]}'.");
        }
    }

    /// <summary>Players address by 1-based index or by name (case-insensitive).</summary>
    private static MediaPlayerViewModel? ResolvePlayer(IReadOnlyList<MediaPlayerViewModel> players, string key)
    {
        if (int.TryParse(key, out var number))
            return number >= 1 && number <= players.Count ? players[number - 1] : null;
        return players.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTruthy(string value) =>
        value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
