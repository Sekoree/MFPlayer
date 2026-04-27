using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using S.Media.Avalonia;
using S.Media.Core;
using S.Media.NDI;
using SPlayer.Core.ViewModels;
using SPlayer.Core.Views;

namespace SPlayer.Core;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureSPlayerLogging();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
            desktop.Exit += (_, _) => mainVm.Dispose();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Routes core, playback, NDI, and Avalonia video logging to a single console + trace (see Program.LogToTrace).</summary>
    private static void ConfigureSPlayerLogging()
    {
        var min = LogLevel.Information;
#if DEBUG
        min = LogLevel.Debug;
#endif
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(min);
            b.AddFilter("S.Media", min);
            b.AddFilter("SPlayer", min);
            b.AddFilter("Avalonia", LogLevel.Warning);
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
                o.IncludeScopes = true;
            });
        });
        MediaCoreLogging.Configure(factory);
        NDIMediaLogging.Configure(factory);
        AvaloniaVideoLogging.Configure(factory);
    }
}