using System;
using Avalonia;

namespace MultiViewTest;

internal static class Program
{
    internal static string[] LaunchArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        LaunchArgs = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
