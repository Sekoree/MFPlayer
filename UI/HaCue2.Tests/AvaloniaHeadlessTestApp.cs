using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(HaCue2.Tests.AvaloniaHeadlessTestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HaCue2.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// The headless stand-in for <c>HaCue2.App</c>.
/// </summary>
/// <remarks>
/// <para>
/// It carries the SAME two style slots the real application declares — Simple in slot 0 for control
/// infrastructure, the booth bundle over it — because several controls the app hosts declare required
/// template parts and throw out of <c>OnApplyTemplate</c> when a bare fallback template does not supply
/// them. A test that realises a real view would fail on that, and the failure looks like a bug in the
/// view rather than a missing theme.
/// </para>
/// <para>
/// It does NOT subclass the real <c>App</c>: that class opens a launcher or a shell window from
/// <c>OnFrameworkInitializationCompleted</c> and loads machine settings off disk. A test app has to be
/// the app's THEME without being its composition root.
/// </para>
/// </remarks>
internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://HaCue2/"))
        {
            Source = new Uri("avares://HaCue2/Themes/BoothDark.axaml"),
        });

        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }
}
