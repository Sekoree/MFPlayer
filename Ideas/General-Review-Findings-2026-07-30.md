# Full-repo review findings — 2026-07-30

Scope: the whole `next-fix-enhance-round` branch (HEAD `3994f26a`, 16 commits, 215 files,
+34 628/−1 600 against `master`) — the structural refactor A–E, the cue enhancement/timeline round
before it, the audio backends, the clock layer, and the HaPlay/HaViz UI on top. Companion docs:
`Structural-Refactor-Plan-2026-07-29.md` (whose residual lists are accurate and are NOT repeated
here), `Next-Round-Plan-2026-07-28.md`, `General-Review-Findings-2026-07-27.md`.

This review deliberately looked for what those docs do **not** already name.

## 0. Baseline state — verified, not assumed

As found, BEFORE the fixes in this document's fix log (which add tests, so the counts below no longer match
a current run):

| Check | Result |
|---|---|
| `dotnet build MFPlayer.sln` | clean — **0 warnings**, 0 errors |
| S.Media.Core / Session / Control / Compositor / Players / Backends / NDI / Arch | 1 297 passed, 0 failed |
| HaViz.Core | 20 passed |
| HaPlay | 999 passed, 1 **intermittent** failure (§1) |
| All opt-in `[TimingFact]`s (`MFP_TIMING_TESTS=1`) | HaPlay **1004/1004**, Core 684/684, Players 17/17 |

The `[TimingFact]` gate is worth stating explicitly: every one of them passes on this box, so the
skips are load-shedding for oversubscribed CI, not quarantined breakage.

---

## 1. `HaPlay.Tests` dispatcher flake — root cause pinned, and one anti-finding

**Status: OPEN before this review, FIXED by it (see the fix log at the end).**

The symptom is the one `DispatcherOwnershipGuard` was built to hunt:
`InvalidOperationException: The calling thread cannot access this object because a different thread
owns it`, thrown from `AvaloniaHeadlessPlatform.Initialize` → `Compositor..ctor` →
`DefaultRenderLoop.Add` → `ThrowVerifyAccess`. Reproduced here in **2 of 4** full-assembly runs,
with the victim moving between runs (`UnknownEndpoint_Returns404_AndBadMethod405`, then
`PlayerVolume_SetsClampedMasterVolume`).

### The mechanism

Read in `Reference/Avalonia-12.0.4` (the app runs Avalonia 12.1.0 from NuGet; the relevant code is
unchanged between them — the observed stack matches it frame for frame):

1. `Dispatcher.cs:44`, in the constructor: **`s_uiThread ??= this`**. The first `Dispatcher`
   constructed after a reset becomes the process-wide UI thread — *whichever thread constructs it*.
2. `Dispatcher.ThreadStorage.cs:60-67`: `Dispatcher.UIThread` is `s_uiThread ?? CurrentDispatcher`,
   and `CurrentDispatcher` does `new Dispatcher(null)` when the calling thread has none.
3. Isolation defaults to `PerTest` — the assembly carries no `[assembly: AvaloniaTestIsolation]`,
   and `HeadlessUnitTestSession.GetOrStartForAssembly` falls back to
   `AvaloniaTestIsolationLevel.PerTest`. So `EnsureIsolatedApplication` runs on **every**
   `Dispatch`: `Dispatcher.ResetBeforeUnitTests()` (→ `ResetGlobalState()` → `s_uiThread = null`)
   and then `_appBuilder.SetupUnsafe()`.
4. **Between those two calls** `s_uiThread` is null. Any thread that touches `Dispatcher.UIThread`
   in that window constructs a dispatcher on itself and takes the binding. The session thread's
   `SetupUnsafe()` then reaches `DefaultRenderLoop.Add`, calls `VerifyAccess`, and fails.

### Two conclusions that revise the guard's own notes

- **It is a window, not a culprit test.** HaPlay has 128 `Dispatcher.UIThread` sites and many are
  legitimately reached from background threads (that is what marshalling *is*). So
  "blame whoever just finished" cannot converge — which is exactly what the guard kept
  demonstrating when the blamed test differed on every run. The guard's remarks already suspected
  the access was asynchronous; this is the reason.
- **It self-heals.** The next `Dispatch` calls `ResetBeforeUnitTests()` again and re-binds
  `s_uiThread` correctly. That is why exactly one test dies and the run still completes in 26 s
  instead of wedging — and it is what makes a cheap fix legitimate rather than a papering-over.

### Anti-finding: `PerAssembly` isolation is NOT the fix

It is the obvious move — `EnsureSharedApplication` never calls `ResetBeforeUnitTests`, so the
window would not exist. **It was tried and it does not work.** With
`[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]` the assembly builds
clean and then the suite **hangs** (killed after minutes, on the first run). The tests depend on the
per-test fresh `Application` — `HeadlessAppTheme` says so in as many words, and the theme-swap and
overlay-layer tests are built on it. Recorded here so it is not rediscovered and retried.

### The fix

Because the corruption self-heals on the next dispatch, a **retry** is a correct fix and not a
band-aid: the second attempt runs a fresh `ResetBeforeUnitTests()` + `SetupUnsafe()` pair, so it
either wins the window or fails for a real reason. That is a small change against the alternative —
tearing down every view model in every test, which is the ~47-`DispatcherTimer` job the guard's
remarks call "a bigger job than one guard".

---

## 2. `IAudioOutputLatency` is dropped by every output decorator

**Status: OPEN before this review, FIXED by it.**

`IAudioOutputLatency` (new this round, `S.Media.Core/Audio/IAudioOutputLatency.cs`) is implemented
by `PortAudioOutput`, `MiniAudioOutput` and `NullClockedAudioOutput` — and forwarded by **none** of
the three output decorators:

| Decorator | `IClockedOutput` | `IPlaybackClock` | `IAudioOutputPlaybackStats` | `IAudioOutputLatency` |
|---|---|---|---|---|
| `MeteringAudioOutput.Wrap` | ✅ | ✅ | — | ❌ |
| `ResamplingAudioOutput.Wrap` | ✅ | ✅ | — | ❌ |
| `AudioEffectOutput.Wrap` | ✅ | ✅ | ✅ | ❌ |
| `SharedAudioOutput.ClientInput` | ✅ | ✅ | — | ❌ |

This is **latent, not live**: the only consumer is `SharedAudioOutput.DownstreamLatencyTicks`, which
probes `_terminal`, and the terminal is always the raw backend output
(`PortAudioOutputRuntime.CreateOutput` → `backend.CreateOutput`, never wrapped). The decorators sit
on the *client* side of the fan-in (`MediaPlayerViewModel.ShowSession.cs:974-976`: lease → effects →
meter → resample).

It matters anyway, for one reason: this is the **same shape as the epoch gotcha the round already
hit and documented** — *"wrappers MUST forward `EpochId`/`Read`, or they silently erase their
device's epochs"*. That one was caught and every wrapper carries the fix with a comment explaining
it. The latency capability, added later, went through the same seam and nobody was watching. A
capability that is invisible at two layers is a trap for whoever next wants latency compensation on
the deck path.

### Why it can be fixed without doubling the matrix

The three `Wrap` methods hand-roll a combinatorial subclass product — 10 classes across 3 files for
3 conditional capabilities, already inconsistent (`Stats` exists only in `AudioEffects`). Adding
`IAudioOutputLatency` as a fourth *conditional* capability would double it again.

It does not have to be conditional. `IClockedOutput` and `IPlaybackClock` must be — a consumer tests
for them to *decide behaviour* (pacing, master-clock promotion), so claiming them over an inner that
lacks them changes what the router does. `IAudioOutputLatency` is different: its own contract says
"**or `TimeSpan.Zero` when unknown**", and its only consumer adds the value only when `> Zero`. So a
decorator can implement it unconditionally with an `Inner is IAudioOutputLatency l ? l.… : Zero`
fallback — no new subclasses, no growth, and the capability stops vanishing.

---

## 3. Two files have outgrown their seams

Not a defect — a maintenance finding, with the seams already marked in the source:

| File | Lines | Existing structure | Marked seams |
|---|---|---|---|
| `UI/HaPlay/ViewModels/CuePlayerViewModel.cs` | 4 479 | already 9 partials (8 061 lines total) | `// ----- Now Playing row seek` (2192), `// ----- Playlist / armed-list group runs` (2932), `// --- Visualizer Now-Playing rows` (4017) |
| `MediaFramework/Media/S.Media.Session/ShowSession.cs` | 4 018 | `VoicePlayer` already extracted along its ownership seam | `effect buses` (292), `dispatcher` (652), `show loading` (702), `soundboard voices` (1523), `transport commands` (2049), `master trim` (2751), `queries` (2987) |

In both cases the split pattern is established and the root file has become the catch-all that
everything not obviously belonging elsewhere lands in. The `VoicePlayer` extraction (review Part-5
#2) is the precedent for `ShowSession`; the 9 existing partials are the precedent for
`CuePlayerViewModel`.

---

## 4. Checked and found sound

Recorded so the same ground is not re-covered:

- **Epoch contract (plan D) holds end to end.** All three clock-forwarding decorators
  (`MeteringAudioOutput`, `ResamplingAudioOutput`, `AudioEffects`) forward `EpochId` *and* `Read`,
  each with the comment explaining why. Every `IPlaybackClock` implementation was enumerated and
  checked; the only gaps are the two the refactor plan already names
  (`ClockedNativeAudioOutput` has no ABI channel for an epoch, `TransportTimeline` reports `Single`
  and is truthful).
- **`CompositePlaybackClock`** — the commit-vs-project split is correct: member reads take the same
  coherent snapshot but discard the blend advance, while epoch identity and the high-water are
  resolved idempotently, so a member read cannot allocate a second id for one (winner, epoch) pair.
- **`SessionClock`** — the CAS rebase loop is bounded and every exit is monotone.
- **`MidiTimecode`** — drop-frame arithmetic verified both directions (`FrameNumber`,
  `FromFrameNumber` with the 17982/1798 re-insertion, `FramesPerDay` = 2 589 408), `IsValid`'s
  rejection of the two non-existent labels, `SnapDropFrame`'s deliberate asymmetry with it, and the
  splice fingerprint's wrapped-distance guard across midnight.
- **`PlacementResolver`** — the clip-against-canvas pass is applied to the placed image after the
  fit (one-sided), not by clamping the dest rect, so a layer is cut off rather than shrunk; the
  wholly-off-canvas branch collapses the crop instead of sampling a stray line.
- **`TimelineDuckMath`** — footprint length ≥ 2·ramp holds by construction so `t1 ≤ t2` always; the
  borrowed-restore rule really does keep re-apply idempotent for edge-clamped dips, and the one
  non-idempotent case (a dip swallowing the whole clip) is the documented one.
- **`CueSchedulerService`** — the arm/reload baseline correctly retires the previous day's recurring
  occurrence without emitting a spurious "missed" status.
- **Claim discipline** — `TransportVoice.TryClaimFadeOut` / `VoicePlayer.TryClaimStop` agree
  (earlier deadline supersedes, later becomes a bounded waiter), and `RunStopFadeAsync` exits early
  once every claimed voice is retired, so a teardown mid-fade cannot leave a ramp running.
- **`NullClockedAudioOutput`**, **`NdiAudioSubmitRing`** — drain models, overflow policies and
  stop/dispose idempotence all check out.

**Minor, found here and fixed by the §3 restructure rather than on its own account:**
`TransportVoice.ReleaseAsync` disposed `_fadeOutCts` without cancelling it first, unlike its three
siblings (`CancelClipWork`/`CancelClipFade`/`CancelReleaseRamp`, which all cancel then dispose), so a
claimed stop ramp went on marshalling no-op steps onto the dispatcher against a retired voice. Bounded —
`RunStopFadeAsync` exits once every claimed voice is retired, and .NET tolerates `Task.Delay` on a
disposed-not-cancelled token — so it was hygiene, not a defect. It disappeared when the two stop-claim
copies were merged into `SoundingStopClaim`, which is the more interesting point: the asymmetry was only
visible once the duplicate was put next to it.

**Not covered in depth**, given the volume: the GL compositor paths, and `TimelineCanvas`'s
rendering code (1 117 lines) beyond the pure math already extracted out of it.

---

## Fix log — 2026-07-30

### 1. Dispatcher flake (§1) — FIXED, and measured

`HeadlessDispatchExtensions` gained `DispatchGuarded` (sync) alongside the existing `DispatchAsync`, both
routed through a retry loop that re-attempts **only** `IsHeadlessAppInitRace`. Every raw
`session.Dispatch(` in the assembly (37 files) now goes through one of them.

The discriminator is deliberately narrow, and the narrow part is the stack frame, not the message: it
requires `EnsureIsolatedApplication` **or** `SetupUnsafe` in the trace. That is what distinguishes "the
harness could not stand the app up" (body not yet run — app init precedes `action()` in `DispatchCore`, so a
retry cannot execute a test body twice) from "the test body itself touched the wrong thread" (retrying would
hide a real defect behind a flake). `HeadlessDispatchRetryTests` pins both directions by throwing from
methods NAMED for the real frames, so the assertions exercise the same string the production check reads.

**Measured, not assumed:**

| | before | after |
|---|---|---|
| Full-assembly runs | 4 | **32** (18 + a 14-run instrumented soak) |
| Failed runs | **2** | **0** |
| Races absorbed | n/a | **43** (~3 per run) |
| …of which needed a *second* retry | n/a | **14** |

That last row matters: `MaxAttempts = 3` is load-bearing, not padding — with two attempts, 14 of those
would still have failed.

**Anti-finding recorded in code**, so the obvious move is not retried: `PerAssembly` isolation is on
`IsHeadlessAppInitRace`'s remarks as *tried, hangs the suite*.

**One trap avoided.** The first attempt reported the notice via `Console.WriteLine` and measured "0 retries"
across 18 runs — because xunit does not surface `Console` from a static helper, so nothing was listening.
Verified with a probe test (a `Console.WriteLine` from a passing test produces no output at all), then
replaced with the opt-in `HAPLAY_DISPATCH_RETRY_LOG=<path>` file sink — which is where the 43 above come
from. **A metric with no proven channel is not a measurement**, and this one would have read as "the fix
works and the race never happens", which is the opposite of true.

**Lint kept honest.** `HeadlessDispatchLintTests` now scans `.Dispatch(`, `.DispatchGuarded(` **and**
`.DispatchAsync(`. Without that, renaming the call sites would have silently removed the whole assembly from
the vacuous-assertion lint; and `.DispatchAsync(` had in fact never been scanned at all (`.Dispatch(` does
not match it), so this closes a pre-existing hole. `DispatchGuarded` is also in the async-lambda-trap list —
it has the identical `Func<TResult>` overload trap — while `DispatchAsync` stays out of it, being the cure.
Validated both ways with a five-shape probe file: all five caught with distinct messages, then removed.

### 2. `IAudioOutputLatency` forwarding (§2) — FIXED

Implemented unconditionally on the base of `MeteringAudioOutput`, `ResamplingAudioOutput` and
`AudioEffectOutput`, plus `SharedAudioOutput.ClientInput`, all delegating through a new
`AudioOutputLatency.Of(...)` helper that carries the rationale in one place. The conditional capability
matrix (`IClockedOutput` × `IPlaybackClock` × `IAudioOutputPlaybackStats`) is untouched and did not grow.

`ClientInput`'s value is its bus backlog + `DownstreamLatencyTicks()` — exactly the lead `ReportAudible`
subtracts, so the clock and the latency report cannot disagree. It is reported **raw**, not through
`SmoothLeadTicks`: the low-pass exists to steady a monotonic clock, whereas a caller asking how far its
submissions are from the speaker wants the current answer. `SharedAudioOutput.DownstreamLatencyTicks` now
uses the shared helper and lost its local try/catch.

Covered by `MeteringAudioOutputTests`: latency survives the real production chain (effects → meter →
resample), reports Zero rather than being absent over an output that has none (while `IClockedOutput` /
`IPlaybackClock` correctly stay absent), and degrades to Zero from a throwing output.

### 3. File splits (§3) — DONE

| File | before | after (root) | new partials |
|---|---|---|---|
| `ShowSession.cs` | 4 018 | **1 160** | Transport, Stops, Completion, Levels, LiveEdits, Queries, Taps, TransportVoices (309–582 each) |
| `CuePlayerViewModel.cs` | 4 479 | **1 722** | Execution, NowPlaying, Playlists, Preview, SelectionProperties, LiveEditWatch (225–695 each) |

Both roots now carry a `<list type="table">` map of where everything went, so the next person does not have
to grep for it. The split lines are the seams the files already marked with `// ---` comments.

**The restructure that came with it** (the reason this was not a pure move): splitting the stop paths into
their own file put `TransportVoice`'s fade-out claim and `VoicePlayer.VoiceHandle`'s stop claim side by side
for the first time, and they were the same protocol written twice — with three drifts already:

- one disposed the superseded `CancellationTokenSource`, the other deliberately did not (and had reasoned it
  through in a comment);
- `TransportVoice.ReleaseAsync` disposed its claim **without cancelling it first**, unlike its three sibling
  claims — so a stop ramp kept marshalling no-op steps onto the dispatcher against a retired voice (see §4;
  bounded, so hygiene rather than a bug, and merging the copies fixes it for free);
- the bounded "wait for whoever owns the release" helper existed twice, identical but for the noun in its
  warning.

All three now live once, in `SoundingStopClaim`. A protocol two stop domains must implement identically
under concurrency is not something to keep in sync by hand.
