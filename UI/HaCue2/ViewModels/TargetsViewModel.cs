using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
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
        Monitor = runtime.TriggerMonitor;

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

    public IReadOnlyList<LogLine> Monitor { get; }

    /// <summary>The Learn pane's listening latch — amber while it waits for any device to speak.</summary>
    [ObservableProperty]
    private bool _isLearning = true;

    public string LearnTarget => Bindings.Count > 0 ? $"binding {Bindings[0].Fires}" : "no binding selected";
    public string LearnCaught { get; } = "APC mini · note-on 3 · ch 1";

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
