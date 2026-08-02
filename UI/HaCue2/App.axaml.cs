using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        return window;
    }

    private static ShellWindow OpenShell() => new() { DataContext = new ShellViewModel() };
}
