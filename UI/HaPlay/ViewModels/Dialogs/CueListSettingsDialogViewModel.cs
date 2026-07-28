using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>Result returned by the cue list settings dialog. The caller is responsible for
/// applying these onto the active <see cref="CueListEditorViewModel"/> + persisting them as
/// part of the project save.</summary>
public sealed record CueListSettingsDialogResult(
    CueTriggerMode DefaultTriggerMode,
    bool AutoRenumberOnInsert,
    int? StopFadeMs,
    CueFadeCurve StopFadeCurve);

/// <summary>VM for the cue-list settings dialog.</summary>
public sealed partial class CueListSettingsDialogViewModel : ViewModelBase
{
    public CueListSettingsDialogViewModel(
        CueTriggerMode defaultTriggerMode,
        bool autoRenumber,
        int? stopFadeMs,
        CueFadeCurve stopFadeCurve,
        int appDefaultStopFadeMs)
    {
        _defaultTriggerMode = defaultTriggerMode;
        _autoRenumberOnInsert = autoRenumber;
        _stopFadeMs = stopFadeMs;
        _stopFadeCurve = stopFadeCurve;
        AppDefaultStopFadeMs = appDefaultStopFadeMs;
    }

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    /// <summary>Null (the editor cleared) = use the app-settings default; 0 = hard cut.</summary>
    [ObservableProperty]
    private decimal? _stopFadeMs;

    [ObservableProperty]
    private CueFadeCurve _stopFadeCurve;

    /// <summary>The app-settings fallback, shown as the empty field's hint.</summary>
    public int AppDefaultStopFadeMs { get; }

    public CueTriggerMode[] TriggerModes { get; } = System.Enum.GetValues<CueTriggerMode>();

    public CueFadeCurve[] FadeCurves { get; } = System.Enum.GetValues<CueFadeCurve>();

    public string DialogTitle => Strings.CueListSettingsDialogTitle;

    public string StopFadeMsHint =>
        Strings.Format(nameof(Strings.StopFadeMsAppDefaultHintFormat), AppDefaultStopFadeMs);

    public CueListSettingsDialogResult ToResult() => new(
        DefaultTriggerMode,
        AutoRenumberOnInsert,
        StopFadeMs is { } ms ? (int)ms : null,
        StopFadeCurve);
}
