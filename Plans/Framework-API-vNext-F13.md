# Framework API vNext — the F-13 semantic traps (DECIDED + EXECUTED)

**Owner decisions, 2026-08-14: option A on both items, executed the same day.** `IMediaClock.Stop()`
is deleted (its one real caller - ClipCompositionRuntime's slave-clock teardown - now calls
`Pause()`, the behavior it always had); the registry surface is renamed
`SelectAndOpenVideo/SelectAndOpenAudio` with unchanged semantics (60 references migrated;
`S.Abi`'s plugin-level `TryOpenVideo/TryOpenAudio` keep their names - they are genuinely
null-returning and a different contract). The options analysis is kept below for the record.

2026-08-14, closing out review F-13. Two of its four items already landed
(`AttachConsoleCancelKeyPress` returns an unsubscribe handle; `MediaRegistry` disposal failures
reach diagnostics). This is the design for the remaining two, which change public API shape.

**The fact that changes the calculus:** the packages are not publicly consumable and public
installability is explicitly not a goal (owner decision on review F-01, recorded in
`Doc/Packages.md`). There are **no external consumers** — every caller is in this repository, so a
"breaking" change costs exactly one internal migration commit, and the compat-shim machinery the
review cautiously suggested (obsolete-marked forwards, migration docs) buys nothing today.

---

## 1. `IMediaClock.Stop()` is exactly `Pause()`

`MediaClock.Stop` delegates to `Pause` with the comment "semantics may diverge later" — the
conventional transport expectation (stop = halt **and return to zero**) silently doesn't hold, and
a separate `Reset()` exists beside it. Callers today: **three**, all in Routing teardown paths
(`SharedAudioOutput.Dispose`, `ProgramBusSource.Dispose`/`InvalidateFromOwner`) where the position
after the call is irrelevant — they want `Pause()` semantics and would be *wrong* to reset (a
`Reset` raises `PositionChanged(Zero)` into listeners mid-teardown).

### Options

| | Change | Cost | Risk |
|---|---|---|---|
| **A (recommended)** | Delete `Stop()` from `IMediaClock`/`MediaClock`. The three teardown callers become `Pause()` (behavior-identical today). Callers who want transport-stop compose `Pause(); Reset();` — or we add nothing until a real consumer needs an atomic form. | 4 lines | none — no semantics change anywhere |
| B | Redefine `Stop()` = atomic `Pause` + `Reset`; migrate the three teardown callers to `Pause()`. | same 4 lines + a new atomic implementation + tests | `Reset` inside `Stop` raises `PositionChanged(Zero)` — every future `Stop` caller inherits a notification the current one doesn't emit; an atomic form needs the driver-transition gate held across both halves |
| C | Keep + document (status quo). | 0 | the trap stays armed for the first future consumer |

**Recommendation: A.** A method whose only honest documentation is "this is exactly that other
method" should not exist on the interface. Deleting it is the one option that cannot mislead
anyone, and reintroducing a real atomic `Stop` later (option B's shape) remains open if a consumer
ever needs it — designed then, with its notification semantics chosen deliberately.

## 2. `TryOpenVideo` / `TryOpenAudio` throw on a claimed-but-failed open

The contract is deliberate and documented: `false` = *no provider claims this URI* ("nothing here
can play this"), **throw** = *the selected provider failed* (a genuine open failure with real
diagnostic detail). The trap is purely the **`Try` prefix**, which by .NET convention promises
no-throw outcome semantics. ~15 internal call sites (tools, `AudioClip`, the YouTube module's
pinned-provider path) rely on the current split; `OpenAsync(MediaOpenRequest)` already exists as
the blessed atomic path and always throws with the provider's real failure.

### Options

| | Change | Cost | Risk |
|---|---|---|---|
| **A (recommended)** | Rename to `SelectAndOpenVideo/Audio(...)` (same signature, same semantics — the bool *is* selection, and the name finally says so). Mechanical rename of ~15 call sites. `TryOpenImage` keeps its name (it genuinely is no-throw). | one mechanical commit | none — behavior untouched |
| B | Make `Try*` truly no-throw: catch provider failures, return `false` + an `out MediaOpenFailure?` carrying the exception/detail. | signature change at every site + a new failure type | call sites that today let the provider's exception propagate (with its message reaching the operator) must each be revisited; the "no provider" vs "provider failed" split gets easier to ignore |
| C | Docs-only steering to `OpenAsync`. | 0 | the convention violation stays |

**Recommendation: A.** The semantics are right — the review itself only faulted the *shape*.
Renaming keeps the valuable two-state distinction, fixes the convention violation permanently, and
costs one mechanical pass while there are zero external consumers to shim for.

---

## Execution (once the owner picks)

One commit per item, migration + tests together (warnings-as-errors means there is no partial
state). `Doc/MediaFramework-Quickstart.md` and package READMEs updated in the same commit —
`NativeTruthDocTests`-style doc assertions are not needed here; the compiler enforces the rename.
