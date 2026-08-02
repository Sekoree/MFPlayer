using Avalonia;

namespace HaCue2.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HaCue2.App>()
        .UsePlatformDetect()
        // Inter is the sans face the mockup's system-ui stack resolves to on a modern desktop, and
        // embedding it means a booth machine with a minimal font set still renders the shell as drawn.
        // The mono face is deliberately NOT embedded — Themes/Tokens.axaml names a fallback stack, and
        // every desktop in this repo's target set has at least DejaVu Sans Mono.
        .WithInterFont()
        .LogToTrace();
}
