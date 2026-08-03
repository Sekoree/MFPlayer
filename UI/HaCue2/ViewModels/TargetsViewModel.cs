using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Engine;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// Screen 11 — Action endpoints · Trigger inputs · Remote API (register item 24).
/// </summary>
/// <remarks>
/// The wire monitor appears on every tab, filtered to that tab's direction, because "did it actually
/// send / did it actually arrive" is the question on all three.
/// </remarks>
public partial class TargetsViewModel : ObservableObject
{
    private readonly HaCueProject _project;
    private readonly ShowRuntime _runtime;
    private readonly ProjectJournal? _journal;

    public TargetsViewModel(HaCueProject project, ShowRuntime runtime, ProjectJournal? journal = null)
    {
        _project = project;
        _runtime = runtime;
        _journal = journal;

        EndpointsTab = $"ACTION ENDPOINTS · {project.ActionEndpoints.Count}";
        TriggersTab = $"TRIGGER INPUTS · {project.TriggerInputs.Count}";
        Tabs = [EndpointsTab, TriggersTab, RemoteTab];
        _selectedTab = TriggersTab;

        Endpoints = TargetPresentation.Endpoints(project, runtime);
        _selectedEndpoint = Endpoints.FirstOrDefault();
        Sources = TargetPresentation.Sources(project, runtime);
        _selectedSource = Sources.FirstOrDefault();

        _port = (project.Settings.RemoteApi?.Port ?? 8420).ToString();
        _serverState = project.Settings.RemoteApi?.Enabled == true ? "on" : "off";
        _lanMode = project.Settings.RemoteApi?.LanAllowed == true ? "allowed" : "local only";
    }

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal ?? new ProjectJournal(_project);

    public bool HasNoEndpoints => Endpoints.Count == 0;
    public bool HasNoSources => Sources.Count == 0;

    /// <summary>Re-reads the endpoints and inputs after a dialog added one.</summary>
    public void Refresh()
    {
        Endpoints = TargetPresentation.Endpoints(_project, _runtime);
        Sources = TargetPresentation.Sources(_project, _runtime);

        OnPropertyChanged(nameof(Endpoints));
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(Bindings));
        OnPropertyChanged(nameof(HasNoEndpoints));
        OnPropertyChanged(nameof(HasNoSources));
        OnPropertyChanged(nameof(LearnTargets));
        OnPropertyChanged(nameof(LearnTarget));
        OnPropertyChanged(nameof(CanBind));
        OnPropertyChanged(nameof(LearnConflict));
        OnPropertyChanged(nameof(Bindings));
        OnPropertyChanged(nameof(HasBindings));
        OnPropertyChanged(nameof(BindingsHeader));
        OnPropertyChanged(nameof(Monitor));
    }

    public const string RemoteTab = "REMOTE API";

    public string EndpointsTab { get; }
    public string TriggersTab { get; }
    public IReadOnlyList<string> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEndpointsPane))]
    [NotifyPropertyChangedFor(nameof(IsTriggersPane))]
    [NotifyPropertyChangedFor(nameof(IsRemotePane))]
    [NotifyPropertyChangedFor(nameof(MonitorHint))]
    private string _selectedTab;

    public bool IsEndpointsPane => SelectedTab == EndpointsTab;
    public bool IsTriggersPane => SelectedTab == TriggersTab;
    public bool IsRemotePane => SelectedTab == RemoteTab;

    public string MonitorHint => SelectedTab switch
    {
        var tab when tab == EndpointsTab => "outbound only on this tab",
        RemoteTab => "remote calls only on this tab",
        _ => "inbound only on this tab",
    };

    // ── action endpoints ──────────────────────────────────────────────────────────────────────
    public IReadOnlyList<TriggerSourceRow> Endpoints { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEndpointName))]
    [NotifyPropertyChangedFor(nameof(EndpointHost))]
    [NotifyPropertyChangedFor(nameof(EndpointPort))]
    [NotifyPropertyChangedFor(nameof(TestMessage))]
    [NotifyPropertyChangedFor(nameof(HasEndpointSelected))]
    private TriggerSourceRow? _selectedEndpoint;

    private ActionEndpoint? Endpoint => SelectedEndpoint is null
        ? null
        : _project.ActionEndpoints.FirstOrDefault(item => item.Id == SelectedEndpoint.Id);

    public string SelectedEndpointName => Endpoint?.Name ?? "no endpoint selected";

    public bool HasEndpointSelected => Endpoint is not null;

    /// <summary>Where the endpoint sends. Editable: an action cue is useless pointed at nothing.</summary>
    public string EndpointHost
    {
        get => Endpoint?.Host ?? "";
        set
        {
            if (Endpoint is not { } endpoint || endpoint.Host == value)
                return;

            Write(endpoint, "host", () => endpoint.Host, text => endpoint.Host = text, value,
                $"“{endpoint.Name}” host {value}");
        }
    }

    public string EndpointPort
    {
        get => Endpoint?.Port.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            if (Endpoint is not { } endpoint)
                return;

            // Refused rather than coerced. A port of 0 is not "unset" to a socket, it is "pick one for
            // me", and an action cue that quietly sent to an arbitrary port would be worse than one
            // that reported a bad number.
            if (!int.TryParse(value, out var port) || port is < 1 or > 65_535)
            {
                OnPropertyChanged(nameof(EndpointPort));
                return;
            }

            if (endpoint.Port == port)
                return;

            Write(endpoint, "port", () => endpoint.Port, number => endpoint.Port = number, port,
                $"“{endpoint.Name}” port {port}");
        }
    }

    /// <summary>
    /// Register item 24 — each endpoint stores its own test payload.
    /// </summary>
    /// <remarks>
    /// Reads and writes the SELECTED endpoint's own message. It used to be loaded once from the first
    /// endpoint in the project and never written back, so typing a payload and pressing SEND TEST sent
    /// the stored one — the box proved nothing about the desk and lied about what it had proved.
    /// </remarks>
    public string TestMessage
    {
        get => Endpoint?.TestMessage ?? "";
        set
        {
            if (Endpoint is not { } endpoint || endpoint.TestMessage == value)
                return;

            Write(endpoint, "testMessage", () => endpoint.TestMessage,
                text => endpoint.TestMessage = text, value, $"“{endpoint.Name}” test message");
        }
    }

    /// <summary>Writes one endpoint field through the journal, when there is one.</summary>
    private void Write<T>(
        ActionEndpoint endpoint, string field, Func<T> read, Action<T> write, T value, string label)
    {
        if (_journal is null)
        {
            write(value);
        }
        else
        {
            _journal.Do(new SetValueCommand<T>(endpoint.Id, field, "targets", read, write, value, label));
            _journal.CloseGroup();
        }

        RaiseEndpointFields();
        Refresh();
    }

    private void RaiseEndpointFields()
    {
        OnPropertyChanged(nameof(EndpointHost));
        OnPropertyChanged(nameof(EndpointPort));
        OnPropertyChanged(nameof(TestMessage));
        OnPropertyChanged(nameof(SelectedEndpointName));
        OnPropertyChanged(nameof(HasEndpointSelected));
    }

    // ── trigger inputs ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<TriggerSourceRow> Sources { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bindings))]
    [NotifyPropertyChangedFor(nameof(BindingsHeader))]
    private TriggerSourceRow? _selectedSource;

    public string BindingsHeader => $"Bindings on {SelectedSource?.Name ?? "—"}";

    public IReadOnlyList<BindingRow> Bindings
    {
        get
        {
            var input = SelectedSource is null
                ? null
                : _project.TriggerInputs.FirstOrDefault(item => item.Id == SelectedSource.Id);

            return input is null ? [] : TargetPresentation.Bindings(_project, input);
        }
    }

    public IReadOnlyList<LogLine> Monitor => _runtime.TriggerMonitor;

    public bool HasBindings => Bindings.Count > 0;

    /// <summary>Which binding REMOVE acts on.</summary>
    [ObservableProperty]
    private int _selectedBindingIndex = -1;

    // ── Learn (register item 24) ───────────────────────────────────────────────────────────────
    // External input runs, but until this landed a TriggerBinding could not be constructed anywhere in
    // the app: the runtime worked and the authoring surface did not exist. Learn is how a binding gets
    // made without hand-writing a pattern — the wire monitor already prints exactly the text a binding
    // holds, so "what you just pressed" and "what gets bound" are the same string by construction.

    /// <summary>The Learn pane's listening latch — amber while it waits for any device to speak.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LearnState))]
    private bool _isLearning;

    /// <summary>What was caught while listening, frozen so a later message does not move the target.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBind))]
    [NotifyPropertyChangedFor(nameof(LearnConflict))]
    [NotifyPropertyChangedFor(nameof(LearnState))]
    private string _learnCaught = "";

    /// <summary>
    /// The repeat filter a newly bound input gets, in ms.
    /// </summary>
    /// <remarks>
    /// A controller that sends a note on every scan tick would otherwise fire a cue as fast as the
    /// wire allows. 250 ms is the default because it is longer than a bounce and shorter than a
    /// deliberate second press.
    /// </remarks>
    [ObservableProperty]
    private string _learnNoRepeat = "250 ms";

    /// <summary>The parsed filter, or the default when the box says something unusable.</summary>
    private int NoRepeatMs
    {
        get
        {
            var digits = new string([.. LearnNoRepeat.Where(char.IsAsciiDigit)]);
            return int.TryParse(digits, out var ms) && ms is >= 0 and <= 10_000 ? ms : 250;
        }
    }

    /// <summary>The remote token, masked — it is never rendered in full.</summary>
    public string RemoteTokenMask =>
        _project.Settings.RemoteApi is null ? "machine setting · see Settings" : "•••••••• · see Settings";

    public string LearnState => IsLearning
        ? "● waiting for input — press something on any device"
        : LearnCaught.Length > 0
            ? "caught — press BIND to keep it"
            : "idle — press LEARN, then a button on your controller";

    /// <summary>Starts listening. The next message that arrives is the candidate.</summary>
    public void BeginLearn()
    {
        LearnCaught = "";
        IsLearning = true;
    }

    public void CancelLearn()
    {
        IsLearning = false;
        LearnCaught = "";
    }

    /// <summary>
    /// Called on every observed message while the pane is open.
    /// </summary>
    /// <remarks>
    /// The FIRST message ends the listen rather than the last: a fader sends a stream, and a Learn that
    /// kept updating would bind whatever the operator's hand did on the way back.
    /// </remarks>
    public void Observe(string described)
    {
        if (!IsLearning || described.Length == 0)
            return;

        LearnCaught = described;
        IsLearning = false;
    }

    /// <summary>The parameters a control surface may ride, when a session is running.</summary>
    /// <remarks>
    /// Read from the running host rather than a list authored here: the registry is what a binding
    /// actually writes through, so a picker built from anything else could offer a parameter nothing
    /// would accept.
    /// </remarks>
    private IReadOnlyList<S.Control.ParameterTarget> Parameters =>
        Host is { } host ? ShowParameters.Describe(host.Parameters) : [];

    /// <summary>The running show, when there is one. Set by the shell.</summary>
    public ShowHost? Host { get; set; }

    /// <summary>
    /// What the new binding will act on — transport verbs, parameters, then every cue.
    /// </summary>
    /// <remarks>
    /// Parameters sit between the verbs and the cues because a continuous control is almost always
    /// meant for one: somebody who has just moved a fader is not looking for a cue to fire.
    /// </remarks>
    public IReadOnlyList<string> LearnTargets =>
    [
        "transport · go", "transport · stop", "transport · pause", "transport · panic",
        .. Parameters.Select(target => $"parameter · {target.DisplayName}"),
        .. _project.AllCues().Select(cue => $"Q{CuePresentation.Number(cue.Number)} · {cue.Label}"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBind))]
    private int _learnTargetIndex;

    public string LearnTarget => SelectedSource is { } source
        ? $"on {source.Name}"
        : "select a source first";

    public bool CanBind =>
        _journal is not null && SelectedSource is not null && LearnCaught.Length > 0;

    /// <summary>
    /// Whether the caught input already fires something on this source.
    /// </summary>
    /// <remarks>
    /// Shown BEFORE the bind, not after: rebinding a note that already fires a cue is how a show loses
    /// one silently, and the operator finds out at the worst possible moment.
    /// </remarks>
    public string LearnConflict
    {
        get
        {
            if (LearnCaught.Length == 0 || Input is not { } input)
                return "";

            var clash = input.Bindings.FirstOrDefault(binding =>
                string.Equals(binding.Input, LearnCaught, StringComparison.OrdinalIgnoreCase));

            if (clash is null)
                return $"{LearnCaught} is free";

            var fires = clash.TargetCueId is { } id && _project.FindCue(id) is { } cue
                ? cue.Label
                : clash.ParameterId;

            return $"{LearnCaught} already fires “{fires}” — binding will replace it";
        }
    }

    /// <summary>The selected source as a document object.</summary>
    private TriggerInputDefinition? Input => SelectedSource is null
        ? null
        : _project.TriggerInputs.FirstOrDefault(item => item.Id == SelectedSource.Id);

    /// <summary>
    /// Creates the binding — the one path in the app that constructs a <c>TriggerBinding</c>.
    /// </summary>
    /// <remarks>
    /// Journaled, and it REPLACES a clashing binding rather than adding a second on the same input:
    /// two bindings on one note both firing is almost never what somebody meant, and the conflict line
    /// above says so before the button is pressed.
    /// </remarks>
    public void Bind()
    {
        if (_journal is null || Input is not { } input || LearnCaught.Length == 0)
            return;

        var binding = new TriggerBinding
        {
            Input = LearnCaught,
            NoRepeatMs = NoRepeatMs,
        };

        var verbs = new[] { "go", "stop", "pause", "panic" };
        var parameters = Parameters;

        if (LearnTargetIndex < verbs.Length)
        {
            binding.Target = TriggerTarget.Transport;
            binding.ParameterId = verbs[LearnTargetIndex];
        }
        else if (LearnTargetIndex - verbs.Length < parameters.Count)
        {
            var target = parameters[LearnTargetIndex - verbs.Length];

            binding.Target = TriggerTarget.Parameter;
            binding.ParameterId = target.Id;
            // The parameter's OWN range, so the trigger layer scales the controller into decibels
            // rather than into an arbitrary 0..1 the writer would then have to reinterpret.
            binding.RangeMin = target.Minimum;
            binding.RangeMax = target.Maximum;
            // A fader sends a stream; a repeat filter on one would make it lurch.
            binding.NoRepeatMs = 0;
        }
        else
        {
            var cues = _project.AllCues().ToList();
            var at = LearnTargetIndex - verbs.Length - parameters.Count;

            if (at < 0 || at >= cues.Count)
                return;

            binding.Target = TriggerTarget.Cue;
            binding.TargetCueId = cues[at].Id;
        }

        using (_journal.Composite($"bind {LearnCaught}", "targets"))
        {
            foreach (var clash in input.Bindings
                .Where(item => string.Equals(item.Input, LearnCaught, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                _journal.Do(new RemoveItemCommand<TriggerBinding>(
                    input.Bindings, clash, "targets", "replace the existing binding"));
            }

            _journal.Do(new AddItemCommand<TriggerBinding>(
                input.Bindings, binding, input.Bindings.Count, "targets", $"bind {LearnCaught}"));
        }

        CancelLearn();
        Refresh();
    }

    /// <summary>Removes one binding. The only way back out of a mis-bind other than undo.</summary>
    public void RemoveBinding(int index)
    {
        if (_journal is null || Input is not { } input || index < 0 || index >= input.Bindings.Count)
            return;

        _journal.Do(new RemoveItemCommand<TriggerBinding>(
            input.Bindings, input.Bindings[index], "targets", "remove binding"));
        _journal.CloseGroup();

        Refresh();
    }

    /// <summary>
    /// Sends an endpoint's own configured test payload (register item 24).
    /// </summary>
    /// <remarks>
    /// The endpoint's OWN message, not a generic ping: a ping proves the socket is open, which is not
    /// the question. The question is whether the desk understood you.
    /// </remarks>
    public async Task SendTestAsync(ActionSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        if (Endpoint is not { } endpoint)
            return;

        var probe = new ActionCueNode
        {
            Label = $"test · {endpoint.Name}",
            EndpointId = endpoint.Id,
            Address = endpoint.TestMessage.Length > 0 ? endpoint.TestMessage : "/hacue2/test",
        };

        // The address may carry arguments after a space, the way the field is written.
        var parts = probe.Address.Split(' ', 2);
        probe.Address = parts[0];
        probe.Arguments = parts.Length > 1 ? parts[1] : "";

        TestResult = await sender.SendAsync(probe, endpoint).ConfigureAwait(true) ?? "sent";
        OnPropertyChanged(nameof(TestResult));
    }

    /// <summary>What the last test send did.</summary>
    public string TestResult { get; private set; } = "";

    // ── remote API ────────────────────────────────────────────────────────────────────────────

    /// <summary>The running server, when the project turned it on. Null otherwise.</summary>
    public RemoteApiServer? Remote { get; set; }

    /// <summary>
    /// The API's own route table, with live call counts.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="RemoteApiRoutes"/> rather than authored, so the tab cannot list a route the
    /// server does not serve — which was exactly what the sample table did, including one route that
    /// had never existed.
    /// </remarks>
    public IReadOnlyList<EndpointRow> Routes =>
    [
        .. RemoteApiRoutes.All
            .OrderBy(route => route.Domain, StringComparer.Ordinal)
            .ThenBy(route => route.Pattern, StringComparer.Ordinal)
            .Select(route => new EndpointRow(
                route.Method,
                route.Pattern,
                route.Summary,
                route.Calls.ToString(System.Globalization.CultureInfo.CurrentCulture))),
    ];
    public IReadOnlyList<string> ServerStates { get; } = ["on", "off"];
    public IReadOnlyList<string> LanModes { get; } = ["allowed", "local only"];

    [ObservableProperty] private string _serverState;
    [ObservableProperty] private string _lanMode;
    [ObservableProperty] private string _port;

    public string LastCall => Remote?.LastCall is { Length: > 0 } call ? call : "no calls yet";
    /// <summary>Where it is answering, or why it is not.</summary>
    public string ServedAt => Remote is { IsRunning: true, Address: { Length: > 0 } address }
        ? $"served at {address}"
        : _project.Settings.RemoteApi is { Enabled: true }
            ? "not listening — see the status bar"
            : "off — turn it on in project settings";
}
