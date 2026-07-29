using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Control;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.Services;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

/// <summary>
/// Fallback MIDI device resolution (ambiguous/missing port picks).
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- Fallback MIDI device resolution ----------------------------------------------------
    // When a configured MIDI device cannot be confidently matched to a current port (ambiguous or
    // missing), let the user pick the live port and persist that choice into the device binding.

    [RelayCommand(CanExecute = nameof(CanResolveMIDIDevices))]
    private async Task ResolveMIDIDevicesAsync()
    {
        if (await ResolveMIDIDevicesCoreAsync(announceWhenResolvedOrEmpty: true).ConfigureAwait(true))
            StatusMessage = "MIDI device bindings resolved." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private bool CanResolveMIDIDevices() => IsMIDIAvailable;

    /// <summary>
    /// Enumerates current MIDI ports, prompts the user to resolve any ambiguous/missing bindings, and writes
    /// the chosen ports back into the config. Returns true when at least one binding was updated.
    /// </summary>
    private async Task<bool> ResolveMIDIDevicesCoreAsync(bool announceWhenResolvedOrEmpty)
    {
        if (!IsMIDIAvailable)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = MIDIUnavailableStatus;
            return false;
        }

        var catalog = MIDICatalogProvider();
        if (catalog is null)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device catalog is unavailable.";
            return false;
        }

        var requests = ControlMIDIDeviceResolver.BuildRequests(_config, catalog.Inputs, catalog.Outputs);
        if (requests.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "All enabled MIDI devices resolve to a current port.";
            return false;
        }

        var selections = await MIDIResolutionPrompt(requests).ConfigureAwait(true);
        if (selections is null || selections.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device resolution cancelled.";
            return false;
        }

        _config = ControlMIDIDeviceResolver.ApplySelections(_config, selections);
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        NotifySummary();
        return true;
    }

    private static ControlMIDIPortCatalog? EnumerateMIDIPorts() =>
        ControlMIDIPortCatalogProvider.TryEnumerate();

    private static async Task<IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>?> DefaultPromptAsync(
        IReadOnlyList<ControlMIDIResolutionRequest> requests)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var dialog = new RebindMissingControlMIDIDevicesDialog
        {
            DataContext = new RebindMissingControlMIDIDevicesDialogViewModel(requests),
        };
        return await dialog.ShowDialog<IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>?>(owner)
            .ConfigureAwait(true);
    }

    private static async Task<string?> DefaultProfileImportPathPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import control profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Control profile JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<string?> DefaultProfileExportDirectoryPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export control profiles",
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
    }

    private static Window? TryGetOwnerWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (Application.Current?.ApplicationLifetime is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    // ----- Always-on device input --------------------------------------------------------------
    // MIDI ports and OSC listeners belong to a ControlInputSession whose lifetime is "configured and
    // enabled", NOT "armed": per-cue triggers and MIDI learn consume InputObserved with the mapping
    // engine disarmed. Arming attaches the mapping engine to the SAME session (ref-counted open), so
    // disarming can never close a port a trigger binding still needs, and arming never double-opens one.

    /// <summary>Test seam (like <see cref="MIDICatalogProvider"/>): the real session opens MIDI ports and
    /// binds UDP sockets, so it only auto-starts under a desktop lifetime; headless tests inject their own.</summary>
    internal Func<ControlSystemConfig, ControlInputSession>? DeviceInputSessionFactory { get; set; }

    private bool CanRunDeviceInput =>
        DeviceInputSessionFactory is not null
        || Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    /// <summary>Brings the always-on input session in line with the current config. Fire-and-forget from
    /// the config-change path; a no-op while armed, where the running session holds leases on it and edits
    /// already carry "re-arm to apply".</summary>
    private void SyncDeviceInputSession()
    {
        if (_inputShutdown || IsArmed || !CanRunDeviceInput)
            return;

        _ = SyncDeviceInputSessionAsync(rethrowOpenFailure: false);
    }

    private async Task SyncDeviceInputSessionAsync(bool rethrowOpenFailure)
    {
        var config = _config;
        var signature = DeviceInputSignature(config);
        await _inputSyncGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // Re-check under the gate, not only in the synchronous wrapper: DisposeAsync sets the flag and
            // then tears the session down while holding THIS gate, so a sync queued before that (a config
            // edit, a disarm, a project switch) would otherwise resume afterwards and create + start a
            // brand-new session nothing ever disposes - leaked MIDI ports / UDP sockets for the rest of the
            // process, and on a project switch the previous project's devices come back up.
            if (_inputShutdown)
                return;

            if (!string.Equals(signature, _inputSignature, StringComparison.Ordinal)
                || (_inputSession is null && signature.Length > 0))
            {
                await TearDownDeviceInputSessionAsync().ConfigureAwait(true);
                _inputSignature = signature;
                // ...and again after that await: tearing the old session down can take a while (its own
                // stop/dispose gate, a listener still unbinding), and the workspace can go away meanwhile.
                if (_inputShutdown)
                    return;

                if (signature.Length > 0)
                {
                    var created = DeviceInputSessionFactory?.Invoke(config) ?? CreateDeviceInputSession(config);
                    created.InputObserved += OnDeviceInputObserved;
                    _inputSession = created;
                }
            }

            // A previous open may have failed (port in use, device unplugged); retry rather than
            // leaving the operator with silently dead triggers.
            var session = _inputSession;
            if (_inputShutdown || session is null || session.IsOpen)
                return;

            try
            {
                await session.StartAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                var message = Strings.Format(nameof(Strings.ControlDeviceInputFailedStatusFormat), ex.Message);
                StatusMessage = message;
                ToastCenter.Warn(message);
                if (rethrowOpenFailure)
                    throw;
            }
        }
        finally
        {
            _inputSyncGate.Release();
        }
    }

    private ControlInputSession CreateDeviceInputSession(ControlSystemConfig config) =>
        new(
            config,
            monitor: null, // the monitor pane attaches its buffer while armed; disarmed input is event-only
            midiSessionFactory: IsMIDIAvailable
                ? sink => new ControlSystemMIDIDeviceSessionManager(config, sink)
                : null);

    private void OnDeviceInputObserved(ControlMonitorRecord record) => InputObserved?.Invoke(record);

    private async Task TearDownDeviceInputSessionAsync()
    {
        var session = _inputSession;
        _inputSession = null;
        if (session is null)
            return;

        session.InputObserved -= OnDeviceInputObserved;
        try
        {
            await session.DisposeAsync().ConfigureAwait(true);
        }
        catch
        {
            // Teardown must never throw; the devices are going away regardless.
        }
    }

    /// <summary>
    /// Identity of everything the live input session <em>behaves on</em>. An unchanged signature keeps the
    /// open ports across unrelated config edits; a changed one rebuilds the session so the edit takes effect.
    /// </summary>
    /// <remarks>
    /// The session captures the config by reference at construction, so this has to cover every field its
    /// managers read - not just the ones that decide which ports get opened. In particular
    /// <see cref="ControlOSCListenerManager"/> resolves an incoming message to a device by the device's
    /// <c>OSCHost</c>/<c>OSCPort</c>/<c>OSCListenerId</c>, and both managers stamp raw wire bytes into
    /// monitor records per <see cref="ControlMonitorOptions.IncludeRawBytes"/>. Leaving those out meant an
    /// operator could edit an OSC device's host/port (the UI says "Re-arm to apply"), disarm and re-arm, and
    /// get the pre-edit snapshot back - scripts firing for the wrong device until HaPlay restarted.
    /// </remarks>
    private static string DeviceInputSignature(ControlSystemConfig config)
    {
        if (!ControlInputSession.HasConfiguredDevices(config))
            return string.Empty;

        var midi = config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled)
            .OrderBy(d => d.Id)
            .Select(d => string.Join(
                '|',
                d.Id,
                d.ProfileId,
                d.Binding.MIDIInputDeviceId,
                d.Binding.MIDIInputDeviceName,
                d.Binding.MIDIOutputDeviceId,
                d.Binding.MIDIOutputDeviceName));
        var osc = config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.OSC && d.IsEnabled)
            .OrderBy(d => d.Id)
            .Select(d => string.Join(
                '|',
                d.Id,
                d.ProfileId,
                d.Binding.OSCHost,
                d.Binding.OSCPort,
                d.Binding.OSCLocalPort,
                d.Binding.OSCListenerId));
        var listeners = config.OSCListeners
            .Where(l => l.IsEnabled)
            .OrderBy(l => l.Id)
            .Select(l => $"{l.Id}|{l.LocalPort}");
        return string.Join(
            ';',
            midi.Concat(osc).Concat(listeners).Append($"rawBytes={config.Monitor.IncludeRawBytes}"));
    }

    private async Task ArmInternalAsync()
    {
        // Give the user a chance to bind ambiguous/missing MIDI devices to live ports before opening
        // sessions. No-op in tests/headless (no owner window, or no enabled MIDI bindings to resolve).
        await ResolveMIDIDevicesCoreAsync(announceWhenResolvedOrEmpty: false).ConfigureAwait(true);

        ControlSystemRuntimeSession? pendingSession = null;
        UdpControlOSCSender? pendingOSC = null;
        try
        {
            // Arm maps the SHARED input session when device input runs here; a failed open surfaces as a
            // failed arm. Without one (headless/no factory) the runtime session owns its devices as before.
            if (CanRunDeviceInput)
                await SyncDeviceInputSessionAsync(rethrowOpenFailure: true).ConfigureAwait(true);

            var armedConfig = _config with { IsArmed = true };
            var monitor = new ControlMonitorBuffer(Math.Max(1, _config.Monitor.MaxVisibleMessages));
            var osc = new UdpControlOSCSender(armedConfig);
            var input = _inputSession;
            // Owned fallback only when no shared session runs here; otherwise the input session owns
            // the MIDI ports (input AND output) and this run just borrows its sender.
            var ownedMIDI = input is null
                ? new ControlSystemMIDIDeviceSessionManager(armedConfig, monitor)
                : null;
            var midi = input?.MIDISender ?? ownedMIDI;
            var session = new ControlSystemRuntimeSession(
                armedConfig,
                CreateSourceProvider(),
                osc,
                midi,
                monitor: monitor,
                midiSessions: ownedMIDI,
                inputSession: input);
            pendingSession = session;
            pendingOSC = osc;
            await session.StartAsync().ConfigureAwait(true);

            _monitorBuffer = monitor;
            _oscSender = osc;
            _midiSender = midi;
            _session = session;
            pendingSession = null;
            pendingOSC = null;
            _lastRenderedVersion = -1;
            _lastX32CacheVersion = -1;
            StatusMessage = $"Armed - {ListenerCount} listener(s), {DeviceCount} device(s), {ScriptCount} script(s).";
        }
        catch (Exception ex)
        {
            if (pendingSession is not null)
            {
                try
                {
                    await pendingSession.DisposeAsync().ConfigureAwait(true);
                }
                catch
                {
                    // best effort cleanup after failed arm
                }
            }

            pendingOSC?.Dispose();
            await DisarmInternalAsync().ConfigureAwait(true);
            StatusMessage = $"Failed to arm: {ex.Message}";
        }
    }

    private async Task DisarmInternalAsync()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;

        if (session is not null)
        {
            try
            {
                await session.StopAsync().ConfigureAwait(true);
                await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Disarming must never throw; the session is being torn down regardless.
            }
        }

        osc?.Dispose();
        // Device input outlives the mapping engine: pick up any config edits made while armed (the
        // shared session's ports stayed open across the disarm - the workspace still holds a reference).
        SyncDeviceInputSession();
    }

    private void StopSessionFireAndForget()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;
        if (session is null && osc is null)
            return;

        _ = Task.Run(async () =>
        {
            if (session is not null)
            {
                try
                {
                    await session.StopAsync().ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best effort teardown on project switch
                }
            }

            osc?.Dispose();
        });
    }

    private IControlScriptSourceProvider CreateSourceProvider() =>
        // Resolve helper scripts (imports) against the project folder, or the scratch cache while unsaved,
        // so a script's `require` of a sibling file works before the project has ever been saved.
        Directory.Exists(EffectiveScriptRoot)
            ? new FileSystemControlScriptSourceProvider(EffectiveScriptRoot)
            : new InMemoryControlScriptSourceProvider(new Dictionary<string, string>());

    [RelayCommand]
    private void ClearMonitor()
    {
        _monitorBuffer?.Clear();
        MonitorEntries.Clear();
        _lastRenderedVersion = -1;
    }

    [RelayCommand]
    private async Task SendTestOSCAsync()
    {
        var osc = _oscSender;
        var monitor = _monitorBuffer;
        if (osc is null || monitor is null)
        {
            StatusMessage = "Arm the control system before sending test OSC.";
            return;
        }

        var host = TestOSCHost.Trim();
        var address = TestOSCAddress.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "OSC test host is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            StatusMessage = "OSC test address is required.";
            return;
        }

        if (!int.TryParse(TestOSCPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            StatusMessage = "Invalid OSC port.";
            return;
        }

        var args = ParseOSCArgs(TestOSCArgs);
        try
        {
            await osc.SendAsync(host, port, address, args).ConfigureAwait(true);
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.OSC,
                Result = ControlMonitorResult.Sent,
                RemoteHost = host,
                RemotePort = port,
                Endpoint = $"{host}:{port}",
                Address = address,
                OSCArguments = args.Select(ControlMonitorOSCArgumentRecord.FromOSCArgument).ToList(),
                Message = "test send",
            });
        }
        catch (Exception ex)
        {
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.OSC,
                Result = ControlMonitorResult.Failed,
                RemoteHost = host,
                RemotePort = port,
                Address = address,
                Message = "test send",
                ErrorMessage = ex.Message,
            });
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedX32CommandRow))]
    private void UseSelectedX32CommandForTestSend()
    {
        var row = SelectedX32CommandRow;
        if (row is null)
            return;

        TestOSCHost = row.Host;
        TestOSCPort = row.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TestOSCAddress = row.Address;
        TestOSCArgs = string.Empty;
        StatusMessage = $"Prepared '{row.CommandName}' for test send.";
    }

    [RelayCommand(CanExecute = nameof(CanRequestSelectedX32Command))]
    private async Task RequestSelectedX32CommandAsync()
    {
        UseSelectedX32CommandForTestSend();
        await SendTestOSCAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunManualScriptsAsync()
    {
        var session = _session;
        if (session is null)
        {
            StatusMessage = "Arm the control system first.";
            return;
        }

        try
        {
            await session.EventQueue.DispatchManualAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manual run error: {ex.Message}";
        }
    }
}
