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

        _testMessage = project.ActionEndpoints.FirstOrDefault()?.TestMessage ?? "";
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
    private TriggerSourceRow? _selectedEndpoint;

    private ActionEndpoint? Endpoint => SelectedEndpoint is null
        ? null
        : _project.ActionEndpoints.FirstOrDefault(item => item.Id == SelectedEndpoint.Id);

    public string SelectedEndpointName => Endpoint?.Name ?? "no endpoint selected";
    public string EndpointHost => Endpoint?.Host ?? "—";
    public string EndpointPort => Endpoint?.Port.ToString() ?? "—";

    /// <summary>Register item 24 — each endpoint stores its own test payload.</summary>
    [ObservableProperty]
    private string _testMessage;

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

    /// <summary>What the new binding will fire — every cue in the show, plus the transport verbs.</summary>
    public IReadOnlyList<string> LearnTargets =>
    [
        "transport · go", "transport · stop", "transport · pause", "transport · panic",
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
            NoRepeatMs = 250,
        };

        var verbs = new[] { "go", "stop", "pause", "panic" };

        if (LearnTargetIndex < verbs.Length)
        {
            binding.Target = TriggerTarget.Transport;
            binding.ParameterId = verbs[LearnTargetIndex];
        }
        else
        {
            var cues = _project.AllCues().ToList();
            var at = LearnTargetIndex - verbs.Length;

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
    public IReadOnlyList<EndpointRow> Routes { get; } = SampleShow.RemoteEndpoints;
    public IReadOnlyList<string> ServerStates { get; } = ["on", "off"];
    public IReadOnlyList<string> LanModes { get; } = ["allowed", "local only"];

    [ObservableProperty] private string _serverState;
    [ObservableProperty] private string _lanMode;
    [ObservableProperty] private string _port;

    public string LastCall { get; } = "POST /cues/go · 10.0.1.7 · 13:58";
    public string ServedAt { get; } = "served at http://10.0.1.5:8420";
}
