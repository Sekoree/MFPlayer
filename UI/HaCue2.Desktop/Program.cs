using Avalonia;
using HaCue2.Machine;
using HaCue2.Session;
using S.Media.Audio.PortAudio;

namespace HaCue2.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        // The composition root picks the backend. PortAudio matches what HaPlay checks against, so a
        // show authored in one and opened in the other reports the same devices present.
        // One backend instance: the device list the status pass checks against and the one the engine
        // opens outputs on must be the same, or a line can read "present" and then fail to open.
        var backend = new PortAudioBackend();

        HaCue2.App.Machine = new MachineFacts(new AudioDevices(backend));
        HaCue2.App.Backend = backend;

        // Visualizer cues render projectM on their own offscreen GL context, which keeps preset loads
        // off the composition's pump and lets a renderer survive a composition rebuild. It is an
        // OPTIONAL improvement: when the context cannot be created the frame-blit surface falls back
        // to the compositor's own GL thread, so this is set unconditionally rather than gated on a
        // probe that would only tell us the same thing later.
        S.Media.Visualizer.ProjectM.ProjectMVisualSource.OffscreenGlContextFactory =
            S.Media.Present.SDL3.SDL3OffscreenGlContext.TryCreate;

        return Build();
    }

    private static AppBuilder Build() => AppBuilder.Configure<HaCue2.App>()
        .UsePlatformDetect()
        // Inter is the sans face the mockup's system-ui stack resolves to on a modern desktop, and
        // embedding it means a booth machine with a minimal font set still renders the shell as drawn.
        // The mono face is deliberately NOT embedded — Themes/Tokens.axaml names a fallback stack, and
        // every desktop in this repo's target set has at least DejaVu Sans Mono.
        .WithInterFont()
        .LogToTrace();
}
