using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Playback;
using System.Collections.Specialized;
using System.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// The bridge between EDITING a cue and the cue already playing: the property/collection watchers on the
/// selected node, the rules deciding which edits are pushable live rather than needing a re-fire, the pushes
/// themselves (text, video placement, audio routes), and the standby invalidation for edits that are not.
/// <para>Split out of the root file (2026-07-30 review §3). It is a single closed question - "this property
/// just changed; does the running clip need to know?" - and it was previously spread through 300 lines of
/// the root between selection state and drawer properties.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    private CueNodeViewModel? _watchedSelectedCueForProbe;

    private void OnSelectedCueProbeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem)
            or nameof(CueNodeViewModel.SourceCapabilitiesKnown)
            or nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture)
            or nameof(CueNodeViewModel.SourceFrameRateNum)
            or nameof(CueNodeViewModel.SourceFrameRateDen))
        {
            OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
            OnPropertyChanged(nameof(HasSelectedTextCue));
            OnPropertyChanged(nameof(HasSelectedStaticCue));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
            OnPropertyChanged(nameof(IsPreviewingSelectedCue));
            OnPropertyChanged(nameof(PreviewButtonLabel));
            OnPropertyChanged(nameof(IsCueScrubberVisible));
            RefreshVideoFrameRateMismatchWarning();
            SyncCueScrubberFromActiveSelection();
            TogglePreviewCommand.NotifyCanExecuteChanged();
            SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(CueNodeViewModel.SourceHasAudio))
                ExtractCueWaveform(_watchedSelectedCueForProbe);
            RefreshMultiEditSelectionState(resetSelectedItems:
                e.PropertyName is not nameof(CueNodeViewModel.MediaSourceItem));
        }
        else if (e.PropertyName is nameof(CueNodeViewModel.HasSubtitleTracks))
        {
            RefreshMultiEditSelectionState();
        }
    }

    private CueNodeViewModel? _preRollWatchedCue;

    /// <summary>Tracks the selected media/visualizer cue so that in-place edits to routes and placements
    /// can reach the live runtime. Media edits also re-warm standby pre-roll.</summary>
    private void WatchSelectedCueForPreRoll(CueNodeViewModel? value)
    {
        var next = value is { Kind: CueNodeKind.Media or CueNodeKind.Visualizer } ? value : null;
        if (ReferenceEquals(_preRollWatchedCue, next))
            return;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged -= OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged -= OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged -= OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        }

        _preRollWatchedCue = next;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged += OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged += OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged += OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
        }
    }

    private void OnWatchedCuePreRollPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The cue master level is baked into the routed gains: apply it to the running clip live
        // (same path as a per-route gain tweak) and let the stale-standby refresh re-prepare.
        if (e.PropertyName is nameof(CueNodeViewModel.LevelDb))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (e.PropertyName is nameof(CueNodeViewModel.StartOffsetMs)
            or nameof(CueNodeViewModel.EndOffsetMs)
            or nameof(CueNodeViewModel.Loop)
            or nameof(CueNodeViewModel.EndBehavior)
            or nameof(CueNodeViewModel.DurationMs)        // image/text duration drives the hold window
            or nameof(CueNodeViewModel.MediaSourceItem)   // text restyle replaces the source -> re-render
            or nameof(CueNodeViewModel.AudioTrackIndex)   // track change is part of the prepared-cue key
            or nameof(CueNodeViewModel.VideoTrackIndex))  // ditto for the video stream selection
            OnWatchedCueEdited();

        // A text/style edit replaces the TextPlaylistItem source; if that cue is playing, re-render its frame in
        // place so the change shows immediately (the deferred document rebuild otherwise only lands on the next
        // fire - see MainViewModel's reload deferral, which keeps the running cue from being torn down mid-edit).
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem))
            PushActiveTextUpdate();
    }

    private static readonly Microsoft.Extensions.Logging.ILogger LiveTextTrace =
        S.Media.Core.Diagnostics.MediaDiagnostics.CreateLogger("HaPlay.LiveText");

    private void PushActiveTextUpdate()
    {
        var watched = _preRollWatchedCue;
        var isText = watched?.MediaSourceItem is TextPlaylistItem;
        var isActive = watched is not null && _activeCueIds.Contains(watched.Id);
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(LiveTextTrace,
            "PushActiveTextUpdate: watched={Watched} isText={IsText} isActive={IsActive} hasCallback={HasCb} activeCount={Count}",
            watched?.Id, isText, isActive, UpdateActiveCueTextCallback is not null, _activeCueIds.Count);

        if (watched is { } cue
            && isText
            && UpdateActiveCueTextCallback is { } callback
            && isActive
            && cue.ToModel() is MediaCueNode model)
            _ = callback(cue.Id, model);
    }

    private void OnWatchedCueRouteCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        PushActiveAudioRoutesUpdate();
        // Add/Remove route commands already suggest a refresh, but a programmatic edit might not.
        OnWatchedCueEdited();
    }

    private void OnWatchedCuePlacementCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        OnWatchedCueEdited();
    }

    private void RebindItemSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var item in e.OldItems.OfType<ObservableObject>())
                item.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<ObservableObject>())
                item.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
    }

    private void OnWatchedRouteOrPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // LineRef is a resolved UI reference, not part of the cue's cache key - ignore it so a mere
        // output-line resolution doesn't churn pre-roll.
        if (e.PropertyName is nameof(CueAudioRouteViewModel.SourceChannel)
            or nameof(CueAudioRouteViewModel.OutputLineId)
            or nameof(CueAudioRouteViewModel.OutputChannel)
            or nameof(CueAudioRouteViewModel.GainDb)
            or nameof(CueAudioRouteViewModel.Muted))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (sender is CueVideoPlacementViewModel placement
            && IsVideoPlacementProperty(e.PropertyName))
        {
            if (IsLiveEditableVideoPlacementProperty(e.PropertyName))
                PushActiveVideoPlacementUpdate(placement);
            RefreshVideoFrameRateMismatchWarning();
        }
    }

    private static bool IsVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.CompositionId)
            or nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyColorHex)
            or nameof(CueVideoPlacementViewModel.ChromaKeySimilarity)
            or nameof(CueVideoPlacementViewModel.ChromaKeySmoothness)
            or nameof(CueVideoPlacementViewModel.ChromaKeySpill)
            or nameof(CueVideoPlacementViewModel.ColorAdjustEnabled)
            or nameof(CueVideoPlacementViewModel.ColorAdjustBrightness)
            or nameof(CueVideoPlacementViewModel.ColorAdjustContrast);

    private static bool IsLiveEditableVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyColorHex)
            or nameof(CueVideoPlacementViewModel.ChromaKeySimilarity)
            or nameof(CueVideoPlacementViewModel.ChromaKeySmoothness)
            or nameof(CueVideoPlacementViewModel.ChromaKeySpill)
            or nameof(CueVideoPlacementViewModel.ColorAdjustEnabled)
            or nameof(CueVideoPlacementViewModel.ColorAdjustBrightness)
            or nameof(CueVideoPlacementViewModel.ColorAdjustContrast);

    /// <summary>Maps a cue-wide placement index to the placement's index AMONG THE CUE'S PLACEMENTS ON
    /// THE SAME COMPOSITION - the order the visualizer executor attached that composition's surface
    /// layers in, and therefore the index the live hot-update API addresses (#26 multi-placement).</summary>
    private static int VisualizerPlacementIndexOnComposition(CueNodeViewModel cue, int placementIndex)
    {
        var compositionId = cue.VideoPlacements[placementIndex].CompositionId;
        var indexOnComposition = 0;
        for (var i = 0; i < placementIndex; i++)
            if (cue.VideoPlacements[i].CompositionId == compositionId)
                indexOnComposition++;
        return indexOnComposition;
    }

    private void PushActiveVideoPlacementUpdate(CueVideoPlacementViewModel placement)
    {
        if (_preRollWatchedCue is not { } cue)
            return;

        var index = cue.VideoPlacements.IndexOf(placement);
        if (index < 0)
            return;

        // A visualizer is a persistent composition surface, not an active ShowSession clip. Its persistent
        // runtime latch outlives a finite Now-Playing row, and its layer has its own hot-update API.
        if (cue.Kind == CueNodeKind.Visualizer)
        {
            if (_runningVisualizers.ContainsKey(cue.Id)
                && UpdateActiveVisualizerPlacementCallback is { } visualizerCallback)
                _ = visualizerCallback(cue.Id, VisualizerPlacementIndexOnComposition(cue, index), placement.ToModel());
            return;
        }

        // Not running yet: the edited placement lives only in the cue model, and the backing ShowSession
        // document a GO fires from is NOT rebuilt on placement edits (only structural changes reload it).
        // Flag it stale so the next fire reloads with the current placement - otherwise the cue fires with
        // the placement captured at the last reload and the new geometry only takes hold once the operator
        // nudges it again (which then takes the live path below). A running cue is updated live instead.
        if (!_activeCueIds.Contains(cue.Id))
        {
            CueClipModelStaleCallback?.Invoke();
            return;
        }

        if (UpdateActiveCueVideoPlacementCallback is not { } callback)
            return;

        _ = callback(cue.Id, index, placement.ToModel());
    }

    private void PushActiveAudioRoutesUpdate()
    {
        if (_preRollWatchedCue is not { } cue
            || UpdateActiveCueAudioRoutesCallback is not { } callback
            || !_activeCueIds.Contains(cue.Id))
            return;

        var routes = cue.AudioRoutes.Select(route => route.ToModel()).ToArray();
        _ = callback(cue.Id, routes, cue.LevelDb);
    }

    /// <summary>An edit-relevant change to the watched (selected) cue: immediately flag its warm
    /// standby <see cref="PreparedCueState.Stale"/> so the badge reflects the drift, then request a
    /// debounced pre-roll refresh that re-prepares it.</summary>
    private void OnWatchedCueEdited()
    {
        if (_preRollWatchedCue is { } cue)
            CueStandbyInvalidated?.Invoke(this, cue.Id);
        SuggestPreRollRefresh();
    }

    /// <summary>Raised with a cue id when an in-place edit drifts that cue's warm standby out of date.
    /// The host marks the engine's prepared entry stale; the following refresh re-prepares it.</summary>
    public event EventHandler<Guid>? CueStandbyInvalidated;

    private void RefreshVideoFrameRateMismatchWarning()
    {
        OnPropertyChanged(nameof(VideoFrameRateMismatchWarning));
        OnPropertyChanged(nameof(HasVideoFrameRateMismatchWarning));
    }

    private string? BuildVideoFrameRateMismatchWarning()
    {
        if (SelectedVideoCue is not { Kind: CueNodeKind.Media } node || !node.SourceHasVideo)
            return null;
        if (!CueFrameRatePolicy.IsKnown(node.SourceFrameRateNum, node.SourceFrameRateDen))
            return null;
        if (SelectedCueList is null)
            return null;

        foreach (var placement in node.VideoPlacements)
        {
            var comp = SelectedCueList.Compositions.FirstOrDefault(c => c.Id == placement.CompositionId);
            if (comp is null)
                continue;
            if (!CueFrameRatePolicy.RatesMismatch(
                    node.SourceFrameRateNum, node.SourceFrameRateDen,
                    comp.FrameRateNum, comp.FrameRateDen))
                continue;

            var srcFps = FormatProbeFps(node.SourceFrameRateNum, node.SourceFrameRateDen);
            var canvasFps = FormatProbeFps(comp.FrameRateNum, comp.FrameRateDen);
            return Strings.Format(
                nameof(Strings.VideoFrameRateMismatchWarningFormat),
                srcFps,
                canvasFps,
                comp.DisplayName);
        }

        return null;
    }

    private static string FormatProbeFps(int num, int den)
    {
        if (den <= 0)
            return "?";
        var fps = num / (double)den;
        return fps >= 100 ? fps.ToString("0.#") : fps.ToString("0.###");
    }
}
