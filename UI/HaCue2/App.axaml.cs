using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Sample;
using HaCue2.Session;
using HaCue2.Engine;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using HaCue2.ViewModels;
using HaCue2.Views;

namespace HaCue2;

public partial class App : Application
{
    /// <summary>
    /// Set <c>HACUE2_START=main</c> to bypass the launcher and open straight into the sample show.
    /// </summary>
    /// <remarks>
    /// This exists for the screenshot harness and for anyone reviewing the shell, who otherwise has to
    /// click through the launcher on every run. It is not a product feature and does not survive the
    /// point where the launcher actually opens a file.
    /// </remarks>
    public const string StartVariable = "HACUE2_START";

    /// <summary>
    /// Set <c>HACUE2_SMOKE=1</c> to render one real frame and then shut down through the app's NORMAL
    /// teardown. The CI launch gate; see <see cref="WireSmokeSelfExit"/>.
    /// </summary>
    public const string SmokeVariable = "HACUE2_SMOKE";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Touched rather than assigned: the desktop head has already loaded these to pick an audio
        // backend from them, and loading again would discard that choice.
        _ = Settings;
        YouTubeRuntime.Configure(Settings);

        // Before anything that logs, and before the framework is touched: MediaDiagnostics resolves
        // per-category loggers from whatever factory is installed at the moment it is asked, so a
        // session started before this ran would log into a null sink for its whole life.
        AppLogging.Install(Settings);

        // Before any window: the override dictionary has to be on the application for the first
        // layout, or the app visibly re-flows on the operator's first look at it.
        Appearance.Current.Attach(this);
        Appearance.Current.Adopt(Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // OnLastWindowClose, not OnMainWindowClose: the launcher hands over to the shell by opening
            // it and closing itself, and under OnMainWindowClose that hand-off is indistinguishable from
            // the user quitting.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = OpenNamedProject(desktop.Args)
                                 ?? (Environment.GetEnvironmentVariable(StartVariable) == "main"
                                     ? OpenShell()
                                     : (Window)OpenLauncher());
            desktop.Exit += (_, _) =>
            {
                YouTubeRuntime.Shutdown();
                AppLogging.Current?.Dispose();
            };
            WireSmokeSelfExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// CI launch gate: with <c>HACUE2_SMOKE=1</c> the app renders its first real frame and then shuts
    /// itself down through the NORMAL teardown path (ShutdownRequested → the Exit handler above), so the
    /// gate covers startup wiring AND clean exit rather than "a process ran".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The AOT publish alone could never catch this class of fault: it proves the app COMPILES and
    /// links AOT-clean, and says nothing about whether it starts. A missing native, a composition-root
    /// failure or a GL context that will not create all ship green through a publish gate. HaPlay has
    /// had this gate since NXT-15; the flagship did not.
    /// </para>
    /// <para>
    /// Pair it with <c>HACUE2_START=main</c> in CI so the smoke opens the SHELL rather than the
    /// launcher - the launcher is a small window that would prove very little.
    /// </para>
    /// <para>
    /// The watchdog hard-exits 2 rather than hanging, so a wedged launch fails the gate instead of
    /// burning the runner's timeout. Exit 0 means a frame really rendered and teardown really ran.
    /// </para>
    /// <para>
    /// A hand-kept mirror of HaPlay's <c>WireSmokeSelfExit</c> - the apps deliberately share no
    /// Avalonia assembly to extract it into. Keep the two in lockstep, INCLUDING the two log lines:
    /// CI greps for the "first frame rendered" breadcrumb, and the watchdog line is the only
    /// diagnosis a wedged launch leaves behind (a bare exit 2 says nothing).
    /// </para>
    /// </remarks>
    private static void WireSmokeSelfExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.GetEnvironmentVariable(SmokeVariable) is not ("1" or "true"))
            return;
        if (desktop.MainWindow is not { } window)
            return;

        var exited = 0;
        window.Opened += (_, _) =>
            // RequestAnimationFrame fires after a compositor frame actually renders - "a window opened"
            // is not the same claim, and it is the weaker one.
            Avalonia.Controls.TopLevel.GetTopLevel(window)?.RequestAnimationFrame(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    // The shell deliberately starts its engine after showing the window. AOT startup
                    // can still be inside SDL/EGL probing when the first Avalonia frame lands, and
                    // tearing native libraries down underneath that probe is a process crash rather
                    // than a useful launch result. Await the real startup boundary; the watchdog still
                    // turns a wedged engine into an exit 2.
                    if (window is ShellWindow shellWindow)
                        await shellWindow.StartupTask.ConfigureAwait(true);

                    if (Interlocked.Exchange(ref exited, 1) != 0)
                        return;
                    // The artifact gate (F-06) greps these lines from STDOUT - the app's logging
                    // pipeline is ring + file only (no console sink), so Console.WriteLine is the only
                    // way a CI runner ever sees them; MediaDiagnostics keeps them in the file log too.
                    // `enumerated=true` proves the audio backend NATIVE actually loaded and initialized
                    // in the exact uploaded directory - the reason the best-effort build-test smoke
                    // could never be promoted to gating.
                    var enumerated =
                        $"HACUE2_SMOKE: audio backend enumerated={(Machine.DevicesEnumerated ? "true" : "false")} " +
                        $"outputs={Machine.Devices?.Outputs.Count ?? 0} hostApis={Machine.Devices?.HostApis.Count ?? 0}";
                    const string settled = "HACUE2_SMOKE: first frame rendered and startup settled - shutting down (exit 0)";
                    Console.WriteLine(enumerated);
                    Console.WriteLine(settled);
                    MediaDiagnostics.LogInformation(enumerated);
                    MediaDiagnostics.LogInformation(settled);
                    // TryShutdown, NOT Shutdown: the forced path skips ShutdownRequested, and the smoke
                    // has to exercise the app's real teardown.
                    desktop.TryShutdown(0);
                }));

        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref exited, 1) != 0)
                return;
            // Console too: without a console sink this line would otherwise exist only in the file
            // log, and a wedged CI launch would leave a bare exit 2 with no diagnosis in the job log.
            const string wedged = "HACUE2_SMOKE: no frame rendered within 45s - hard exit 2";
            Console.WriteLine(wedged);
            MediaDiagnostics.LogWarning(wedged);
            Environment.Exit(2);
        });
    }

    /// <summary>
    /// Opens a project in its own shell window - the one route every "open" in the app takes.
    /// </summary>
    /// <remarks>
    /// Public because the File menu needs it too: switching project means a NEW window rather than
    /// swapping the document under a running shell, which owns a session, a journal and a dozen
    /// projections built around one project.
    /// </remarks>
    public static void ShowProject(HaCueProject project, string path) => Open(project, path).Show();

    /// <summary>
    /// Opens a project that has just been CREATED, which is the one that has to be given a home.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ShowProject"/> rather than inferred from an empty path: the two callers
    /// that pass one are the New-project prompt and the sample-show harness, and asking the harness
    /// where to save would put a modal in front of every screenshot run.
    /// </remarks>
    public static void ShowNewProject(HaCueProject project, string path = "") =>
        Open(project, path, isNew: path.Length == 0).Show();

    /// <summary>Returns to the launcher - what "close project" leaves the operator looking at.</summary>
    public static void ShowLauncher() => OpenLauncher().Show();

    private static LauncherWindow OpenLauncher()
    {
        var vm = new LauncherViewModel(Settings, Machine);
        var window = new LauncherWindow { DataContext = vm };

        // Every route into the shell now goes through here - a recent, a recovered autosave, a file
        // the operator picked, or a new project. There is no longer a path that opens the sample
        // whatever was clicked.
        vm.ProjectOpened += (project, path) =>
        {
            // A new project is the only route that arrives with no path - a recovered autosave is
            // adopted under its ORIGINAL file's, so it has somewhere to go already.
            Open(project, path, isNew: path.Length == 0).Show();
            window.Close();
        };

        return window;
    }

    /// <summary>Opens a project in the shell and records it as the most recent.</summary>
    private static ShellWindow Open(HaCueProject project, string path, bool isNew = false)
    {
        var shell = new ShellViewModel(project, Machine, path, Settings);

        if (path.Length > 0)
            NoteOpened(shell, path);

        // Recorded on the FIRST save too, not only at open: a project created in this session and
        // saved from the picker below would otherwise be missing from the launcher's recents until the
        // next time somebody opened it by hand - which is the one time they cannot, because it is not
        // in the list.
        shell.Saved += saved => NoteOpened(shell, saved);

        return Live(shell, isNew);
    }

    private static void NoteOpened(ShellViewModel shell, string path)
    {
        Settings.NoteOpened(path, shell.Project.Title, Summarize(shell.Project), DateTimeOffset.Now);
        AppSettingsStore.Save(Settings);
    }

    /// <summary>The one-line contents the launcher shows without opening anything.</summary>
    private static string Summarize(HaCueProject project)
    {
        var cues = project.AllCues().Count();
        var lists = project.CueLists.Count;
        var outs = project.AudioPatch.LogicalChannels.Count;

        return $"{cues} cue{(cues == 1 ? "" : "s")} · {lists} list{(lists == 1 ? "" : "s")} "
               + $"· {outs} logical out{(outs == 1 ? "" : "s")}";
    }

    private static ShellWindow OpenShell() => Open(SampleProject.Create(), "");

    /// <summary>
    /// Opens a project named on the command line, straight into the shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a file manager does when a <c>.hacue2proj</c> is double-clicked, what a desktop entry's
    /// <c>%f</c> passes, and what a booth start-up script wants - none of which had any route in: the
    /// app always opened the launcher and made somebody click through it.
    /// </para>
    /// <para>
    /// A path that cannot be opened falls through to the LAUNCHER rather than failing: the operator
    /// still gets a window they can pick a different show from, which is the same rule the launcher's
    /// own file picker follows. Reading the file synchronously is deliberate - this runs before any
    /// window exists, and there is nothing yet to show a spinner on.
    /// </para>
    /// </remarks>
    private static ShellWindow? OpenNamedProject(IReadOnlyList<string>? args)
    {
        var named = args?.FirstOrDefault(argument =>
            !argument.StartsWith('-')
            && argument.EndsWith(ProjectFiles.Extension, StringComparison.OrdinalIgnoreCase));

        if (named is null)
            return null;

        try
        {
            var (project, result) = ProjectFiles.OpenAsync(named).GetAwaiter().GetResult();
            return project is null ? null : Open(project, result.Path);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            MediaDiagnostics.LogError(failure, "HaCue2: opening the project named on the command line failed");
            return null;
        }
    }

    /// <summary>
    /// The machine-scope settings, loaded once at start-up.
    /// </summary>
    /// <remarks>
    /// Read before any window so the operator's theme and density are in place for the FIRST layout -
    /// applying them afterwards makes the app visibly re-flow on the first look at it.
    /// </remarks>
    private static AppSettings? _settings;

    /// <summary>
    /// The machine's settings.
    /// </summary>
    /// <remarks>
    /// Set by the desktop head, which must read them BEFORE this class runs: which audio backend to
    /// open is one of them, and the backend is chosen at the composition root. Loaded lazily when
    /// nothing set them - a preview or a headless capture has no head - and never loaded twice, so a
    /// second read cannot discard the choice the backend was picked from.
    /// </remarks>
    public static AppSettings Settings
    {
        get => _settings ??= AppSettingsStore.Load();
        set => _settings = value;
    }

    /// <summary>
    /// Opens a shell and starts its engine.
    /// </summary>
    /// <remarks>
    /// Started AFTER the window exists and without awaiting it: opening devices and a decoder takes
    /// long enough to be visible, and an app that shows nothing until the audio interface answers
    /// looks broken. The editor is fully usable in the meantime - the transport simply moves the
    /// cursor until the session is up.
    /// </remarks>
    private static ShellWindow Live(ShellViewModel shell, bool isNew = false)
    {
        var window = new ShellWindow { DataContext = shell };
        var shutdownStarted = false;
        var shutdownComplete = false;

        window.Opened += (_, _) =>
            window.StartupTask = StartShellAsync();

        async Task StartShellAsync()
        {
            try
            {
                // BEFORE the autosave and the engine. Autosave writes beside the project file, so a
                // show still waiting to be told where it lives has nowhere to put its recovery copy -
                // which is exactly the window a crash during the first hour would fall into.
                if (isNew)
                    await window.AskWhereToSaveAsync();

                shell.StartAutosave();
                await shell.StartEngineAsync(Backend);
            }
            catch (Exception failure)
            {
                shell.FileMessage = $"the show engine could not start - {failure.Message}";
            }
        }

        window.Closing += async (_, closing) =>
        {
            if (shutdownComplete)
                return;

            closing.Cancel = true;
            if (shutdownStarted)
                return;

            shutdownStarted = true;
            shell.StopAutosave();
            try
            {
                await shell.StopEngineAsync();
            }
            finally
            {
                shutdownComplete = true;
                window.Close();
            }
        };

        return window;
    }

    /// <summary>The audio backend the engine opens devices with. Set by the desktop head.</summary>
    public static IAudioBackend? Backend { get; set; }

    /// <summary>
    /// What this box has, asked once.
    /// </summary>
    /// <remarks>
    /// Set by the desktop head before the app starts, because WHICH audio backend to enumerate is a
    /// composition-root decision - PortAudio and miniaudio see different devices on the same machine.
    /// Left at <see cref="MachineFacts.Nothing"/> for a preview or a headless capture, which probes
    /// files (no hardware needed) and reports every device as Unknown rather than inventing one.
    /// </remarks>
    public static MachineFacts Machine { get; set; } = MachineFacts.Nothing;
}
