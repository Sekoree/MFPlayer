using HaCue2.Core.Model;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>
/// The audition rig (register item 15): monitoring, which never reaches the program mix.
/// </summary>
/// <remarks>
/// Kept apart from the transport on purpose. A preview is not in <c>ShowState.Sounding</c> and never
/// appears in the Active list — an operator glancing at Active during a show must see what the audience
/// can hear and nothing else — and code that lived beside the transport verbs would eventually be
/// wired into one of them.
/// </remarks>
public sealed partial class ShowHost
{
    private Guid _previewing;
    private IVideoOutput? _auditionWindow;

    /// <summary>The cue currently being auditioned, or null.</summary>
    /// <remarks>
    /// A preview is deliberately NOT in <see cref="ShowState.Sounding"/> and never appears in the
    /// Active list: it is monitoring, not program. An operator glancing at Active during a show must
    /// see what the audience can hear, and nothing else.
    /// </remarks>
    public Guid? Previewing
    {
        get
        {
            lock (_gate)
                return _previewing == Guid.Empty ? null : _previewing;
        }
    }

    /// <summary>
    /// Auditions a cue through the rig, replacing whatever was previewing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint is the rig's LINE, so the preview takes that line's own channel count — never a
    /// hardcoded stereo pair (D8). Null names the bay's default monitor terminal, which is what makes
    /// audition work on a one-interface rig nobody has configured.
    /// </para>
    /// <para>
    /// One at a time by construction: the framework's preview player replaces the current preview, and
    /// an operator auditioning two cues at once is hearing neither.
    /// </para>
    /// </remarks>
    public async Task<bool> PreviewAsync(Guid cueId)
    {
        if (_project.FindCue(cueId) is not MediaCueNode media)
            return false;

        if (media.MediaPath.Length == 0)
        {
            Report($"“{media.Label}” has no media to audition");
            return false;
        }

        await EnsureAuditionSurfaceAsync().ConfigureAwait(false);

        var endpoint = _project.Audition.AudioLineId?.ToString();
        var levelDb = _project.Audition.LevelDb;
        if (_project.Audition.DuckWhenProgramSounds && SoundingIds().Count > 0)
            levelDb -= 12;
        var gain = levelDb <= GainRange.SilenceFloorDb
            ? 0f
            : (float)Math.Pow(10, levelDb / 20);

        try
        {
            if (!await _session.PreviewCueAsync(cueId.ToString(), endpoint, gain).ConfigureAwait(false))
            {
                Report($"“{media.Label}” could not be auditioned");
                return false;
            }
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            // A rig pointing at a line this machine did not open. Reported by name rather than thrown:
            // the operator can pick another line, and the show is unaffected either way.
            Report($"the audition rig could not be reached — {failure.Message}");
            return false;
        }

        lock (_gate)
            _previewing = cueId;

        return true;
    }

    /// <summary>
    /// Sends one logical output to the audition monitor, or clears the solo.
    /// </summary>
    /// <remarks>
    /// Toggling: soloing the line that is already soloed clears it, because the button is the same
    /// button and an operator pressing it twice is asking for the monitor back.
    /// </remarks>
    /// <returns>Why it could not, or null on success.</returns>
    public string? SoloToMonitor(Guid channelId) =>
        _bay.Solo(_project, _bay.SoloedChannelId == channelId ? null : channelId);

    /// <summary>Which logical output the monitor is carrying instead of its own patch, or null.</summary>
    public Guid? SoloedChannelId => _bay.SoloedChannelId;

    /// <summary>Stops the audition. Never touches the program — that is the whole point of the rig.</summary>
    public async Task StopPreviewAsync()
    {
        await _session.StopPreviewAsync().ConfigureAwait(false);

        lock (_gate)
            _previewing = Guid.Empty;
    }

    /// <summary>
    /// Brings the audition canvas up, or takes it down, to match the rig.
    /// </summary>
    /// <remarks>
    /// Done lazily on the first audition rather than at start-up: a video surface costs a window, most
    /// cues are audio, and an operator who never previews a video cue should never see one appear.
    /// </remarks>
    private async Task EnsureAuditionSurfaceAsync()
    {
        var rig = _project.Audition;

        if (rig.Surface == AuditionSurface.None)
        {
            if (_auditionWindow is not null)
                await TearDownAuditionSurfaceAsync().ConfigureAwait(false);

            return;
        }

        if (_auditionWindow is not null)
            return;

        // Sized to the rig, or to the biggest composition in the show — the monitor should not be
        // smaller than the thing it is monitoring.
        var width = rig.SurfaceWidth > 0
            ? rig.SurfaceWidth
            : _project.Compositions.Select(item => item.Width).DefaultIfEmpty(1280).Max();

        var height = rig.SurfaceHeight > 0
            ? rig.SurfaceHeight
            : _project.Compositions.Select(item => item.Height).DefaultIfEmpty(720).Max();

        try
        {
            await _session.EnableAuditionCompositionAsync(
                new AuditionCompositionSpec(width, height)).ConfigureAwait(false);

            var window = new SDL3GLVideoOutput("HaCue2 · Audition", width, height);

            if (await _session.AttachAuditionOutputAsync(window).ConfigureAwait(false))
            {
                _auditionWindow = window;
            }
            else
            {
                window.Dispose();
                Report("the audition surface could not be attached");
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // No display, no GL, no window manager. Audio auditioning still works, which is the half
            // that matters most — so this is reported and stepped past, not thrown.
            Report($"the audition surface could not be opened — {failure.Message}");
        }
    }

    private async Task TearDownAuditionSurfaceAsync()
    {
        await _session.DetachAuditionOutputAsync().ConfigureAwait(false);
        await _session.DisableAuditionCompositionAsync().ConfigureAwait(false);

        (_auditionWindow as IDisposable)?.Dispose();
        _auditionWindow = null;
    }
}
