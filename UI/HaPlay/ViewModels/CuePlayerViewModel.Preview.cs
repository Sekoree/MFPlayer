using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// The operator's own listen path: auditioning a cue on a separate device (device enumeration, the automatic
/// vs explicit choice, and its persistence), and the waveform + scrubber the audition is driven with.
/// <para>Kept together because they answer one question - what does the person driving the show hear and see
/// BEFORE the audience does - and because the whole path is deliberately outside the program-audio rules:
/// the master fader, stop-all and Panic never reach it (framework side:
/// <c>SoundingSourceRole.Monitoring</c>). Split out of the root file, 2026-07-30 review §3.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewingSelectedCue))]
    [NotifyPropertyChangedFor(nameof(IsCueScrubberVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(TogglePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekActiveCueFromScrubberCommand))]
    private Guid? _previewingCueId;

    public bool IsPreviewing => PreviewingCueId is not null;

    public bool IsPreviewingSelectedCue =>
        PreviewingCueId is { } id && SelectedCueNode?.Id == id;

    public string PreviewButtonLabel =>
        IsPreviewingSelectedCue ? Strings.StopPreviewCueButton : Strings.PreviewCueButton;

    public ObservableCollection<PreviewAudioDeviceOption> PreviewAudioDevices { get; } = new();

    [ObservableProperty]
    private PreviewAudioDeviceOption? _selectedPreviewAudioDevice;

    // Device-dependence fix #1: distinguishes operator picks (UI selection or a restored project choice)
    // from automatic preselection, so only real choices are persisted and automatic ones stay re-derivable
    // when the configured output lines change.
    private bool _isAutomaticPreviewDeviceSelection;

    /// <summary>True once the operator picked a preview device (or a project restored one) - automatic
    /// derivation from the configured cue output lines then stops overriding the selection.</summary>
    public bool HasExplicitPreviewAudioDeviceChoice { get; private set; }

    partial void OnSelectedPreviewAudioDeviceChanged(PreviewAudioDeviceOption? value)
    {
        if (!_isAutomaticPreviewDeviceSelection && value is not null)
            HasExplicitPreviewAudioDeviceChoice = true;
        OnPropertyChanged(nameof(PreviewAudioDeviceIndex));
    }

    public int? PreviewAudioDeviceIndex => SelectedPreviewAudioDevice?.DeviceIndex;

    public void RefreshPreviewAudioDevices()
    {
        PreviewAudioDevices.Clear();
        PreviewAudioDevices.Add(new PreviewAudioDeviceOption(null, Strings.Format(nameof(Strings.DefaultDeviceLabel))));
        // Runs in the MainViewModel ctor - on a machine without the portaudio native library the
        // enumeration throws DllNotFoundException and takes the whole process down before the first
        // frame. MediaRuntime already degrades to other backends; the preview picker must too.
        if (RuntimeModules.IsPortAudioAvailable)
        {
            foreach (var dev in S.Media.Audio.PortAudio.PortAudioDeviceCatalog.EnumerateOutputDevices())
                PreviewAudioDevices.Add(new PreviewAudioDeviceOption(dev.GlobalDeviceIndex, dev.Name));
        }
        ApplyAutomaticPreviewDeviceSelection();
    }

    /// <summary>Preselects the preview device while the operator has made no explicit choice: the first
    /// configured PortAudio cue output line's device when one resolves, else "Default device" (fix #1 -
    /// preview on a show machine must not implicitly land on the house default when lines are configured).</summary>
    private void ApplyAutomaticPreviewDeviceSelection()
    {
        if (HasExplicitPreviewAudioDeviceChoice && SelectedPreviewAudioDevice is not null)
            return;
        // Index match first (the id the runtime saves), device name second (indices shift across restarts).
        var derived = AvailableOutputs
            .Select(l => l.Definition)
            .OfType<Models.PortAudioOutputDefinition>()
            .Where(d => d.UsesPortAudioBackend)
            .Select(d => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex == d.GlobalDeviceIndex)
                         ?? PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is not null
                             && string.Equals(o.DisplayName, d.DeviceName, StringComparison.Ordinal)))
            .FirstOrDefault(o => o is not null);
        _isAutomaticPreviewDeviceSelection = true;
        try
        {
            SelectedPreviewAudioDevice = derived ?? PreviewAudioDevices.FirstOrDefault();
        }
        finally
        {
            _isAutomaticPreviewDeviceSelection = false;
        }
    }

    /// <summary>The preview-device choice to persist with the project: null while the operator never picked
    /// one (the automatic first-configured-line derivation stays live on load), "" for an explicit
    /// "Default device", else the picked device's name (stable across restarts, unlike its index).</summary>
    public string? BuildPreviewAudioDeviceSnapshot() =>
        !HasExplicitPreviewAudioDeviceChoice ? null
        : SelectedPreviewAudioDevice is not { DeviceIndex: not null } sel ? string.Empty
        : sel.DisplayName;

    /// <summary>Restores a persisted preview-device choice (see <see cref="BuildPreviewAudioDeviceSnapshot"/>).
    /// A persisted device that is no longer present is ignored - the selection falls back to the automatic
    /// derivation instead of pinning a stale name.</summary>
    public void RestorePreviewAudioDevice(string? persistedDeviceName)
    {
        var option = persistedDeviceName switch
        {
            null => null,
            "" => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is null),
            _ => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is not null
                && string.Equals(o.DisplayName, persistedDeviceName, StringComparison.Ordinal)),
        };
        if (option is not null)
        {
            SelectedPreviewAudioDevice = option; // counts as an explicit choice - it round-trips on save
            return;
        }
        HasExplicitPreviewAudioDeviceChoice = false;
        ApplyAutomaticPreviewDeviceSelection();
    }

    private float[]? _selectedCueWaveform;
    private int _selectedCueWaveformRevision;
    private CancellationTokenSource? _waveformCts;

    public float[]? SelectedCueWaveform
    {
        get => _selectedCueWaveform;
        private set { _selectedCueWaveform = value; OnPropertyChanged(); }
    }

    public int SelectedCueWaveformRevision
    {
        get => _selectedCueWaveformRevision;
        private set { _selectedCueWaveformRevision = value; OnPropertyChanged(); }
    }

    public bool HasSelectedCueWaveform =>
        HasSelectedMediaCueWithAudio && SelectedCueWaveform is { Length: > 0 };

    private void ExtractCueWaveform(CueNodeViewModel? cue)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;

        if (cue is not { Kind: CueNodeKind.Media } || !cue.SourceHasAudio)
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        var source = cue.MediaSourceItem;
        var path = source is FilePlaylistItem f ? f.Path : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;
        _ = RunSelectedCueWaveformExtractionAsync(path, ct);
    }

    private async Task RunSelectedCueWaveformExtractionAsync(string path, CancellationToken ct)
    {
        try
        {
            // Progressive display: throttled partial snapshots fill the editor waveform in left-to-right.
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, ct, partial =>
            {
                if (ct.IsCancellationRequested)
                    return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    SelectedCueWaveform = partial;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            });
            if (!ct.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Selection changed after this post was queued: the new cue owns the display.
                    if (ct.IsCancellationRequested)
                        return;
                    SelectedCueWaveform = peaks;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when the selection changes or the view model is disposed.
        }
        catch (Exception ex)
        {
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "Cue waveform extraction failed for {0}: {1}", path, ex.Message);
        }
    }

    /// <summary>Visible when the selected cue is active in the Now Playing panel (Phase 5.5.2).</summary>
    public bool IsCueScrubberVisible =>
        SelectedCueNode is not null
        && (ActiveCues.Any(a => a.CueId == SelectedCueNode.Id) || IsPreviewingSelectedCue);

}
