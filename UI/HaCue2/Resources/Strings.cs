using System.Globalization;
using System.Resources;

namespace HaCue2.Resources;

/// <summary>
/// Centralized operator-facing UI text backed by <c>Resources/Strings.resx</c> (F-21, 2026-08-14
/// review; HaPlay's proven pattern). ShellWindow is the migrated exemplar - new user-visible copy
/// goes here, and the <c>RawStringLiteralLintTests</c> ratchet keeps hardcoded AXAML literals from
/// growing while the remaining screens migrate down over time.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("HaCue2.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);

    public static string ShellFileMenu => Get(nameof(ShellFileMenu));
    public static string ShellFileMenuName => Get(nameof(ShellFileMenuName));
    public static string ShellFileNewProject => Get(nameof(ShellFileNewProject));
    public static string ShellFileOpenProject => Get(nameof(ShellFileOpenProject));
    public static string ShellFileOpenRecent => Get(nameof(ShellFileOpenRecent));
    public static string ShellFileSaveAs => Get(nameof(ShellFileSaveAs));
    public static string ShellFileNewWindow => Get(nameof(ShellFileNewWindow));
    public static string ShellFileCloseProject => Get(nameof(ShellFileCloseProject));
    public static string ShellMainViewName => Get(nameof(ShellMainViewName));
    public static string ShellLock => Get(nameof(ShellLock));
    public static string ShellLockTooltip => Get(nameof(ShellLockTooltip));
    public static string ShellSettings => Get(nameof(ShellSettings));
    public static string ShellDiagnostics => Get(nameof(ShellDiagnostics));
    public static string ShellMore => Get(nameof(ShellMore));
    public static string ShellMoreName => Get(nameof(ShellMoreName));
    public static string ShellMoreSettings => Get(nameof(ShellMoreSettings));
    public static string ShellMoreDiagnostics => Get(nameof(ShellMoreDiagnostics));
    public static string ShellOutputInfo => Get(nameof(ShellOutputInfo));
    public static string ShellOutputInfoTooltip => Get(nameof(ShellOutputInfoTooltip));
    public static string ShellProgramCaption => Get(nameof(ShellProgramCaption));
}
