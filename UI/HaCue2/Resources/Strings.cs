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
    public static string DiagWindowTitle => Get(nameof(DiagWindowTitle));
    public static string DiagBrand => Get(nameof(DiagBrand));
    public static string DiagHeader => Get(nameof(DiagHeader));
    public static string DiagClearProblems => Get(nameof(DiagClearProblems));
    public static string DiagCopyReport => Get(nameof(DiagCopyReport));
    public static string DiagClose => Get(nameof(DiagClose));
    public static string DiagCopyReportHint => Get(nameof(DiagCopyReportHint));
    public static string DiagAudioBayHeader => Get(nameof(DiagAudioBayHeader));
    public static string DiagAttention => Get(nameof(DiagAttention));
    public static string DiagColTerminal => Get(nameof(DiagColTerminal));
    public static string DiagColState => Get(nameof(DiagColState));
    public static string DiagColInFlight => Get(nameof(DiagColInFlight));
    public static string DiagColCap => Get(nameof(DiagColCap));
    public static string DiagColEnqueued => Get(nameof(DiagColEnqueued));
    public static string DiagColProcessed => Get(nameof(DiagColProcessed));
    public static string DiagColDropped => Get(nameof(DiagColDropped));
    public static string DiagColLatency => Get(nameof(DiagColLatency));
    public static string DiagColEpoch => Get(nameof(DiagColEpoch));
    public static string DiagColRate => Get(nameof(DiagColRate));
    public static string DiagVideoHeader => Get(nameof(DiagVideoHeader));
    public static string DiagColComposition => Get(nameof(DiagColComposition));
    public static string DiagColFps => Get(nameof(DiagColFps));
    public static string DiagColLayers => Get(nameof(DiagColLayers));
    public static string DiagColLate => Get(nameof(DiagColLate));
    public static string DiagColBackend => Get(nameof(DiagColBackend));
    public static string DiagLogTailHeader => Get(nameof(DiagLogTailHeader));
    public static string DiagColMinimumLevel => Get(nameof(DiagColMinimumLevel));
    public static string TlAddAutomation => Get(nameof(TlAddAutomation));
    public static string TlLaneVolume => Get(nameof(TlLaneVolume));
    public static string TlLaneGainInsert => Get(nameof(TlLaneGainInsert));
    public static string TlLaneOpacity => Get(nameof(TlLaneOpacity));
    public static string TlLanePositionX => Get(nameof(TlLanePositionX));
    public static string TlLanePositionY => Get(nameof(TlLanePositionY));
    public static string TlLaneWidth => Get(nameof(TlLaneWidth));
    public static string TlLaneHeight => Get(nameof(TlLaneHeight));
    public static string TlLaneRotation => Get(nameof(TlLaneRotation));
    public static string TlLaneChromaKey => Get(nameof(TlLaneChromaKey));
    public static string TlLaneSimilarity => Get(nameof(TlLaneSimilarity));
    public static string TlLaneSmoothness => Get(nameof(TlLaneSmoothness));
    public static string TlLaneSpillReduction => Get(nameof(TlLaneSpillReduction));
    public static string TlLaneColourAdjust => Get(nameof(TlLaneColourAdjust));
    public static string TlLaneBrightness => Get(nameof(TlLaneBrightness));
    public static string TlLaneContrast => Get(nameof(TlLaneContrast));
    public static string TlLaneOscValue => Get(nameof(TlLaneOscValue));
    public static string TlLaneMidiControl => Get(nameof(TlLaneMidiControl));
    public static string TlLive => Get(nameof(TlLive));
    public static string TlLiveHint => Get(nameof(TlLiveHint));
    public static string TlFromPlayhead => Get(nameof(TlFromPlayhead));
    public static string TlZoomOut => Get(nameof(TlZoomOut));
    public static string TlZoomIn => Get(nameof(TlZoomIn));
    public static string TlZoomFit => Get(nameof(TlZoomFit));
    public static string TlDuckUnder => Get(nameof(TlDuckUnder));
    public static string TlClose => Get(nameof(TlClose));
    public static string TlCurveTooltip => Get(nameof(TlCurveTooltip));
    public static string TlCurve => Get(nameof(TlCurve));
    public static string TlCurveEditTooltip => Get(nameof(TlCurveEditTooltip));
    public static string TlSelectAll => Get(nameof(TlSelectAll));
    public static string TlSelectAllTooltip => Get(nameof(TlSelectAllTooltip));
    public static string TlCopy => Get(nameof(TlCopy));
    public static string TlCopyTooltip => Get(nameof(TlCopyTooltip));
    public static string TlPaste => Get(nameof(TlPaste));
    public static string TlPasteTooltip => Get(nameof(TlPasteTooltip));
    public static string TlDelete => Get(nameof(TlDelete));
    public static string TlDeleteTooltip => Get(nameof(TlDeleteTooltip));
}
