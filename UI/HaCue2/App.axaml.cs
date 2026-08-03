using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HaCue2.Sample;
using HaCue2.Session;
using S.Media.Core.Audio;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window: the override dictionary has to be on the application for the first
        // layout, or the app visibly re-flows on the operator's first look at it.
        Appearance.Current.Attach(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // OnLastWindowClose, not OnMainWindowClose: the launcher hands over to the shell by opening
            // it and closing itself, and under OnMainWindowClose that hand-off is indistinguishable from
            // the user quitting.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = Environment.GetEnvironmentVariable(StartVariable) == "main"
                ? OpenShell()
                : OpenLauncher();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static LauncherWindow OpenLauncher()
    {
        var vm = new LauncherViewModel();
        var window = new LauncherWindow { DataContext = vm };

        vm.ProjectRequested += _ =>
        {
            OpenShell().Show();
            window.Close();
        };

        // A project the launcher actually loaded or created, rather than the sample.
        vm.ProjectOpened += (project, path) =>
        {
            var shell = new ShellViewModel(project, Machine);

            if (path.Length > 0)
                shell.AdoptPath(path);

            Live(shell).Show();
            window.Close();
        };

        return window;
    }

    private static ShellWindow OpenShell() => Live(new ShellViewModel(SampleProject.Create(), Machine));

    /// <summary>
    /// Opens a shell and starts its engine.
    /// </summary>
    /// <remarks>
    /// Started AFTER the window exists and without awaiting it: opening devices and a decoder takes
    /// long enough to be visible, and an app that shows nothing until the audio interface answers
    /// looks broken. The editor is fully usable in the meantime — the transport simply moves the
    /// cursor until the session is up.
    /// </remarks>
    private static ShellWindow Live(ShellViewModel shell)
    {
        var window = new ShellWindow { DataContext = shell };

        window.Opened += async (_, _) => await shell.StartEngineAsync(Backend);
        window.Closed += async (_, _) => await shell.StopEngineAsync();

        return window;
    }

    /// <summary>The audio backend the engine opens devices with. Set by the desktop head.</summary>
    public static IAudioBackend? Backend { get; set; }

    /// <summary>
    /// What this box has, asked once.
    /// </summary>
    /// <remarks>
    /// Set by the desktop head before the app starts, because WHICH audio backend to enumerate is a
    /// composition-root decision — PortAudio and miniaudio see different devices on the same machine.
    /// Left at <see cref="MachineFacts.Nothing"/> for a preview or a headless capture, which probes
    /// files (no hardware needed) and reports every device as Unknown rather than inventing one.
    /// </remarks>
    public static MachineFacts Machine { get; set; } = MachineFacts.Nothing;
}
