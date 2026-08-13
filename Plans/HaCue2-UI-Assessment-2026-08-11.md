# HaCue2 UI assessment - 2026-08-11

Asked: are parts of the UI not properly wired up, and what could be improved?

**Summary: the UI is in far better shape than the planning docs claim, and the one real problem was not
a broken control - it was that nothing could have told you if there had been one.**

Scope: 39 `.axaml` files (7 804 lines) and 17 470 lines of view models under `UI/HaCue2`.

---

## 1. The finding worth acting on: view bindings were never compile-checked

Every file under `Controls/` carried `x:CompileBindings="True"`. **None of the 22 files under `Views/`
did** - and `Views/` is where the actual screens live.

That is the whole "is it wired up?" question, and it was unanswerable by construction. Without compiled
bindings, a binding path is resolved by reflection at runtime; a typo, or a view-model property that
gets renamed, produces a silent no-op. The control renders empty, Avalonia writes a binding error to a
trace sink nobody is watching, and the build stays green. **A whole class of "this control does
nothing" bug was undetectable.**

All 22 views already declared `x:DataType`, so the fix was one attribute per file.

### What turning it on found: nothing broken

The build succeeds with compiled bindings on all 22 views, **zero errors**. So every binding path in
every screen already resolves correctly - the exposure was to *future* breakage, not to existing
breakage.

### Verified the guard actually bites

A guard that compiles clean proves nothing on its own, so it was tested by deliberately breaking one
binding (`CuesView.axaml`, `PauseLabel` → `PauseLabelTypoThatDoesNotExist`):

```
error AVLN2000: Unable to resolve property or method of name
'PauseLabelTypoThatDoesNotExist' on type 'HaCue2.ViewModels.CuesViewModel'
```

Restored afterwards. From now on a renamed view-model property that orphans a control **fails the
build** instead of silently blanking it. Compiled bindings are also faster (no per-binding reflection),
though that was not the reason to do it.

Verified afterwards: app launches and self-exits cleanly (2.4 s), `HaCue2.Tests` 383 green.

---

## 2. The planning docs' UI claims are stale, like the rest of them

`HaCue2-Framework-Gap-Analysis.md` reported, as of 2026-08-03: **"21 controls still inert (was 41); 56
fields still hardcoded (was 62)."** Re-measured today:

| Claim | Today |
|---|---|
| 21 inert controls | **0** controls carry `IsEnabled="False"` anywhere in the UI |
| 56 hardcoded fields | **0** TODO / FIXME / "not implemented" / "coming soon" markers anywhere in `UI/HaCue2` |

The only occurrences of the word "hardcoded" left are *comments describing what a field used to be*
before it was wired (`AudioViewModel.cs:827`, `VideoViewModel.cs:964`, `VideoView.axaml.cs:61`).

Also checked and clean:

- **Buttons with no `Command` and no `Click`** - 24 of them, and every one is a flyout host
  (`<Button.Flyout>` / `<Button.ContextFlyout>`), which is correct markup, not a dead control.
- **Empty event handlers** in view code-behind - none.
- **Commands with a constant-false `CanExecute`** - none.
- **Views with no code-behind partner** - none.

---

## 3. Observations, not defects

### 3a. Accessibility coverage is targeted rather than blanket

`AccessibilityAndResponsiveTests` asserts that the *primary transport and custom editors* expose
automation names ("Go current cue list", "Panic; hold to stop everything", "Cue list tree"). That is a
deliberate bar and it is met.

Against a *blanket* bar it would read differently: of 390 interactive controls in `Views/`, 153 have no
`AutomationProperties.Name`, no literal `Content` and no `ToolTip.Tip`. That number is **not** a list of
failures - many are text boxes inside a labelled `HeaderedContentControl`, where the visible header is
the label - but it is the gap between "the transport is accessible" and "the app is". Worth a decision
about which one is intended, rather than an automatic fix.

### 3b. Four view models are getting large

| File | Lines |
|---|---|
| `InspectorViewModel.cs` | 3 469 |
| `VideoViewModel.cs` | 2 567 |
| `CuesViewModel.cs` | 2 437 |
| `AuxiliaryViewModels.cs` | 1 963 |

`InspectorViewModel` is the biggest file in the entire repository. The inspector is genuinely a
many-shaped surface (one pane per cue kind), so the size is *earned* rather than accidental - but it is
the same shape `ShowSession` had before it was source-split into `ShowSession.*.cs` partials, and that
split worked well. `InspectorViewModel.<CueKind>.cs` would be the same move, and it is mechanical.

`AuxiliaryViewModels.cs` is the different case: a 1 963-line file named for the fact that it holds
several unrelated things. Settings, the override ledger and the cache panes all live there. That one is
worth splitting on names, not size.

### 3c. Screen complexity concentrates in two places

Bindings per view: `InspectorPane` 203, `VideoView` 182, `CuesView` 127, `SettingsWindow` 113,
`AudioView` 101. The first two are where any future UI work will be most expensive, and - until today -
were also the two least protected against a silent binding break.

---

## 4. Recommendation

1. **Keep compiled bindings on** (done). It is the only change here, and it converts an entire bug
   class from invisible to impossible.
2. **Decide the accessibility bar** - transport-only (current, and defensible for a booth tool) or
   whole-app. Do not close the 153 automatically; most need a human deciding what the control is
   *called*.
3. **Split `InspectorViewModel` into per-cue-kind partials** when it is next touched, the way
   `ShowSession` was. Not urgent, and not worth a dedicated pass.
4. **Stop trusting the gap analysis's UI numbers.** Both of its UI metrics now read zero.
