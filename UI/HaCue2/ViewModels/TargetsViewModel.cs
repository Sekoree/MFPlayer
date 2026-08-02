using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>
/// Screen 11 — Action endpoints · Trigger inputs · Remote API (register item 24).
/// </summary>
/// <remarks>
/// The wire monitor appears on every tab, filtered to that tab's direction, because "did it actually
/// send / did it actually arrive" is the question on all three and splitting it into one shared panel
/// somewhere else would mean reading a mixed stream to answer a single-direction question.
/// </remarks>
public partial class TargetsViewModel : ObservableObject
{
    public const string EndpointsTab = "ACTION ENDPOINTS · 3";
    public const string TriggersTab = "TRIGGER INPUTS · 3";
    public const string RemoteTab = "REMOTE API";

    public IReadOnlyList<string> Tabs { get; } = [EndpointsTab, TriggersTab, RemoteTab];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEndpointsPane))]
    [NotifyPropertyChangedFor(nameof(IsTriggersPane))]
    [NotifyPropertyChangedFor(nameof(IsRemotePane))]
    [NotifyPropertyChangedFor(nameof(MonitorHint))]
    private string _selectedTab = TriggersTab;

    public bool IsEndpointsPane => SelectedTab == EndpointsTab;
    public bool IsTriggersPane => SelectedTab == TriggersTab;
    public bool IsRemotePane => SelectedTab == RemoteTab;

    public string MonitorHint => SelectedTab switch
    {
        EndpointsTab => "outbound only on this tab",
        RemoteTab => "remote calls only on this tab",
        _ => "inbound only on this tab",
    };

    // ── action endpoints ──────────────────────────────────────────────────────────────────────
    public IReadOnlyList<TriggerSourceRow> Endpoints { get; } = SampleShow.ActionEndpoints;

    [ObservableProperty]
    private TriggerSourceRow? _selectedEndpoint = SampleShow.ActionEndpoints[0];

    /// <summary>Register item 24 — each endpoint stores its own test payload.</summary>
    [ObservableProperty]
    private string _testMessage = "/eos/cue/1/fire";

    // ── trigger inputs ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<TriggerSourceRow> Sources { get; } = SampleShow.TriggerSources;

    [ObservableProperty]
    private TriggerSourceRow? _selectedSource = SampleShow.TriggerSources[0];

    public IReadOnlyList<BindingRow> Bindings { get; } = SampleShow.ApcBindings;
    public IReadOnlyList<LogLine> Monitor { get; } = SampleShow.TriggerMonitor;

    /// <summary>The Learn pane's listening latch — amber while it waits for any device to speak.</summary>
    [ObservableProperty]
    private bool _isLearning = true;

    public string LearnTarget { get; } = "binding Q16 · Loop to 12 if held";
    public string LearnCaught { get; } = "APC mini · note-on 3 · ch 1";

    // ── remote API ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<EndpointRow> Routes { get; } = SampleShow.RemoteEndpoints;
    public IReadOnlyList<string> ServerStates { get; } = ["on", "off"];
    public IReadOnlyList<string> LanModes { get; } = ["allowed", "local only"];

    [ObservableProperty]
    private string _serverState = "on";

    [ObservableProperty]
    private string _lanMode = "allowed";

    [ObservableProperty]
    private string _port = "8420";

    public string LastCall { get; } = "POST /cues/go · 10.0.1.7 · 13:58";
    public string ServedAt { get; } = "served at http://10.0.1.5:8420";
}
