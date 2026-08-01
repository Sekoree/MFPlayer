# HaCue extraction — lessons from the abandoned first attempt (2026-08-01)

The first standalone-HaCue attempt (plan phases P1–P6, ~60 commits) was rolled back to this
branch's base commit `27ceea99` ("HePlay Cue Player extractions plans"), where the cue player is
still part of HaPlay and only the plan + mockups exist. The complete attempt — including every fix
made on 2026-08-01 — is archived on branch **`next-fix-enhance-round`** (its tip is a WIP-archive
commit). Nothing was deleted; cherry-pick from there as needed.

Preferred design reference: `Plans/MockUps/Older/` (kept untracked in the working tree).

## Fixes carried forward into THIS branch

Two fixes from the attempt were framework/app-agnostic bugs that exist independently of the
extraction, so they are applied here directly:

1. **`AudioRouter.OutputPump.DrainLoop` disposed-sink hardening**
   (`MediaFramework/Media/S.Media.Routing/Audio/AudioRouter.OutputPump.cs`).
   A host that disposes a device output (e.g. `PortAudioOutput`) while its pump is still draining
   made every queued chunk throw `ObjectDisposedException` out of `Submit` — one `OutputErrored`
   per chunk, surfacing as "an exception is thrown on close". The pump now reports the disposed
   sink once, then recycles remaining chunks quietly (counters stay truthful for `WaitForIdle`).
   Regression test: `AudioRouterPumpLifecycleTests.DisposedOutput_IsReportedOnce_AndThePumpKeepsDraining`.

2. **`ConverterParameter=0` never matches an int `Count`**
   (HaPlay `CuePlayerView.axaml`, `CueOutputSetupDialog.axaml`; typed `IntZero` resource in
   `Styles/Tokens.axaml`). A literal XAML `ConverterParameter="0"` arrives as a *string*, and
   `ObjectConverters.Equal(int, string)` is never true (`NotEqual`: always true) — the
   "no active cues" / "no routes" / "no placements" empty-state hints were permanently invisible
   and the Stop-All `IsEnabled` gate permanently open. Use `{StaticResource IntZero}` for any
   Count comparison; audit new bindings for the same pattern.

## Gotchas the NEXT extraction attempt must honor

These each cost a debugging session the first time around:

- **Every packaged control theme must be included in the new app's `App.axaml`.** HaCue shipped
  with `<FluentTheme/>` but without `avares://Avalonia.Controls.TreeDataGrid/Themes/Fluent.axaml`
  (and the ColorPicker theme) — the TreeDataGrid had no template and the whole cue list rendered
  as blank space ("adding a cue doesn't show"), while all data-level tests passed. Guard the smoke
  test with a *visual* assertion: `tree.RowsPresenter != null` + a materialized `TreeDataGridRow`.

- **App-exit teardown order: output-line runtimes dispose LAST.** The first attempt disposed the
  `OutputRuntimeService` (stopping PortAudio lines) before the project-audio service quiesced its
  patch-bay pumps → the ObjectDisposedException storm above, on every close with an armed bay.
  Correct order: cue session → bay/audio service → output runtimes → media runtime. Also make
  each exit stage best-effort (report + continue), so one faulting stage cannot strand recovery
  finalize or crash the exiting process.

- **A new desktop app needs crash diagnostics from day one.** HaCue.Desktop had no
  AppDomain/dispatcher/unobserved-task hooks, so field reports arrived as "an exception is thrown"
  with no stack anywhere (the journal only carries Avalonia binding noise). HaPlay's
  `DesktopCrashDiagnostics` is the pattern; the archived HaCue has a lean version
  (`HaCueCrashDiagnostics`, logs to `~/.local/share/mfplayer/hacue/logs/`).

- **The shared Add-output dialog VMs need full host-side seeding.** `AddLocalVideoOutputDialogViewModel`
  requires `InitializeScreens(owner.Screens.All)` *and* `InitializeCloneParents(...)` — without the
  screens call the display picker is empty and `TryCommit` is permanently blocked on "select a
  display". Copy the complete initialization from `OutputManagementViewModel`, not just
  `InitializeExistingOutputNames`.

- **Fluent defaults fight the booth chrome.** Override `ControlCornerRadius` (2) and
  `TextControlThemeMinHeight` (26) at the app level and give TextBox/ComboBox/NumericUpDown a
  compact floor, or the new app reads as two design families. Custom controls must not reference
  WinUI-era `SystemControl*` resource names — they don't exist in Avalonia's Fluent theme and every
  fallback silently fails (the archived HoldButton rendered as an unstyled near-black box).

- **Compositions editing must be discoverable.** The editor (compositions + video-output bindings)
  was reachable only through a button labeled "OUTPUT BINDINGS…" deep in a side panel; per the
  mockup it belongs in project settings ("Compositions"), with the workspace button named for what
  operators look for.

- The archived attempt also contains working implementations worth mining rather than rewriting:
  cue-row live-state tinting via `TreeDataGrid.RowPrepared` + style classes, the per-line output
  strip chips fed from `SnapshotAudioDiagnostics()`, and a headless-Skia screenshot harness for
  comparing the real app against the HTML mockups.
