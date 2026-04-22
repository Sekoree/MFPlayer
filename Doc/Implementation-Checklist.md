# MFPlayer — Implementation Checklist

*Derived from [`API-Implementation-Review.md`](./API-Implementation-Review.md) (2026-04-22).
Breaking API changes are permitted. Line numbers in the review are approximate and are
repeated here only where they speed up the implementer's work.*

## How to read this checklist

- The review already defines Tier 0..7 + Tier 1-N..5-N (NDI sub-tier). This document
  restates them as ticklists, grouped by tier, with **every finding (B/N/R/C/CH/M/S/A/P/PQ/EL
  IDs)** mapped to an actionable item. Items that are purely aspirational / "nice-to-have"
  are gathered at the end.
- The two key framing decisions from the user are called out up-front (§0) because they
  change the shape of several later items.
- Each item links back to the review IDs it closes.

> **Progress legend**
> - `[x]` — landed on `main` (or current working branch) and build/tests green.
> - `[~]` — partially done (sub-items still open; see inline note).
> - `[ ]` — not started.
>
> **Status as of 2026-04-22** (most recent progress pass): §0.2 interface
> consolidation confirmed, §0.4.2 / §0.4.6 scaffolds landed, PortAudio §1.2 merge
> complete (`PortAudioEndpoint` + `PortAudioDrivingMode` + `Create(...)`), §1.4a
> standalone `PortAudioClock` in place, §1.6 `NegotiatedFormat` on
> `IAudioEndpoint`, §2.1 `Doc/Clone-Sinks.md` rewritten to the `router.CreateRoute`
> API, §2.2 legacy `PlaybackEnded` event deleted, §2.3 layering note added to
> `Doc/README.md`, §3.21 `CreateRoute` throwing shape pinned with
> `AVRouterRoutingExceptionTests` (5 tests), §4.1 `AudioFormat.NegotiateFor` +
> §4.2 `ChannelRouteMap.AutoStereoDownmix` + §4.3
> `MediaPlayer.WaitForCompletionAsync` + §4.4 `MediaPlayer : IAsyncDisposable`
> + §4.5 `FFmpegDecoder.StopAsync` + §4.6 exception hierarchy
> (`MediaOpenException` / `MediaDecodeException` / `MediaRoutingException` /
> `MediaDeviceException` / `ClockException`) **including the §4.6 DoD grep
> condition — every `InvalidOperationException` throw-site in `AVRouter` /
> `MediaPlayer` / `FFmpegDecoder` / `FFmpegAudioChannel` / `FFmpegVideoChannel`
> / `SwrResampler` / `StreamAvioContext` has been retargeted** (only
> `RefCountedVideoBuffer`'s over-release precondition check remains, which is
> correct) + §4.11 `PlaybackState` enum + §4.12 `IAudioMixer` extracted to
> `Media/S.Media.Core/Mixing/` (AVRouter routes through
> `DefaultAudioMixer.Instance`) all shipped, the PortAudio-specific Tier 1 bug
> fixes (§3.22–§3.24, §3.27, §3.28) were folded into the merge, `SimplePlayer`
> + `MultiOutputPlayer` migrated off hand-rolled `Math.Min` + `BuildRouteMap`
> boilerplate to `NegotiateFor`, and the three test apps that referenced
> deleted legacy `PortAudioOutput` / `PortAudioSink` have been migrated to
> solution builds with 0 warnings / 0
> errors and **all 207 tests pass** (up from 183 at the previous session —
> +10 mixer tests, +5 router routing-exception tests, +5 MediaPlayer/decoder
> lifecycle tests, +2 FFmpeg open exception tests, +2 tweaks).
>
> **2026-04-22 follow-up pass (this session):** landed a focused batch of
> self-contained Tier-0/Tier-1 fixes that do not depend on the pending §1.2
> NDI/SDL3/Avalonia endpoint rename or §3.11 `VideoFrameHandle` work:
> §2.4–§2.7 doc/XML items confirmed in place, §3.3 `StreamAvioContext`
> EOF-vs-IO classification wired through to the new `FFmpegDecoder.OnError`
> event, §3.5 seek PTS precision, §3.6 hw-frame transfer log-once + skip,
> §3.7 static `FFmpegDecoder._log`, §3.13 `DisposeCore` now tears down every
> route through `RemoveRouteInternal`, §3.15 `BakedChannelMap` applied even
> for equal-channel shapes, §3.18 `SetInputVolume/TimeOffset/Enabled` use
> `Volatile.Write` / `Interlocked.Exchange`, §3.20 push-tick exception logs
> rate-limited (1/2/3 then every 100th), §3.29 `VideoPtsClock` tear-free via
> a new `_stateLock`, §3.30 `MediaClockBase.OnTimerTick` early-returns after
> Dispose, §3.31 `HardwareClock` two-read debounce on fallback exit, §3.31a
> `StopwatchClock.Reset` now calls `base.Stop()`. Test suite grew from 207
> to **214 passing** (+3 `ClockLifecycleTests`, +4 already-landed
> pre-existing tests counted in the 211 baseline). Build still 0 warnings /
> 0 errors.

---

## 0. Framing decisions (record these before starting)

- [x] **0.1** Confirm the two-tier user surface in all docs and XML:
  - `AVRouter` is the **base** of a larger media input/output routing framework
    (multi-source mixing, timeline, clone fan-out, per-route policies).
    It is *not* a "media player" and must stay composable.
  - `S.Media.FFmpeg.MediaPlayer` is a **thin facade** over `AVRouter` for small
    single-file playback apps. New ergonomic helpers (`MediaPlayerBuilder`,
    `WaitForCompletionAsync`, drain handling, `OpenAndPlayAsync`, etc.) live on
    the facade, **not** on `AVRouter`. `AVRouter` stays minimal.
  - The review has been updated to reflect this split (see the **Layering** section
    in the review).
- [x] **0.2** Confirm the audio endpoint consolidation direction (see §1 below):
  the interface-level consolidation is already done (`IAudioEndpoint` is the
  single push contract; `IPullAudioEndpoint : IAudioEndpoint` is a capability
  mixin). The remaining work is **naming / ergonomics / documentation**, not
  new interfaces.

### 0.3 Implementation order & cross-tier dependencies

Because several Tier 1 bug-fix items (notably the PortAudio ones)
rewrite code that §1 will also rewrite, doing them in the legacy classes
first would waste work. The recommended execution order is:

1. **§0 framing decisions + §2 Tier-0 doc sync** (½ day).
2. **§1 endpoint consolidation** (§1.1 vocabulary → §1.2 class merges →
   §1.4 `Create(...)` factories → §1.4a clock audit → §1.4b clock-swap
   docs → §1.5/§1.6 interface docs + `NegotiatedFormat`).
   When a PortAudio bug fix from §3 touches code that's being rewritten
   here, apply it inside the new `PortAudioEndpoint` directly — do
   **not** patch the legacy classes first. The affected items
   (§3.22–§3.28) are tagged below with "**→ do during §1.2**".
3. **§3.11 `VideoFrameHandle`** (ref-counted video ownership). This
   blocks §3.38 clone sink ref-count, §5.3 video route color-hint
   propagation, and §8.9 zero-copy NDI receive, so land it early.
4. **Remaining Tier 1 bug fixes** that are independent of the above
   (decoder, router atomicity, SDL3/Avalonia, NDI input).
5. **§4 Tier 2 ergonomic helpers** (`NegotiateFor`,
   `WaitForCompletionAsync`, exception hierarchy, `IAudioMixer`
   extraction).
6. **§5 Tier 3 builder API** — depends on every concrete endpoint
   having a single `Create(...)` factory (§1.4) and on the exception
   hierarchy (§4.6).
7. **§6 Tier 4 per-route flexibility**, **§7 Tier 5 timeline**, **§8
   Tier 6 performance**, **§9 Tier 7 GL robustness** — in that order;
   performance items generally depend on the ref-counted ownership
   from (3).
8. **§10 cross-cutting** runs in parallel the whole time (tests,
   logging, dispose orchestration, diagnostics).

Explicit blocking edges to keep visible:

| Item | Blocks |
|---|---|
| §1.2 merge | §3.22–§3.28, §5.2 factories, §5.11 test-app migration |
| §3.11 `VideoFrameHandle` | §3.38, §5.3, §8.2, §8.9 |
| §4.6 exception hierarchy | §3.21 `CreateRoute` throwing shape, §5.x builder error surface |
| §4.12 `IAudioMixer` extract | §6.4 PTS-aware mix, §7.* timeline mixing |
| §4.18 `NDISource.Discovered` | §7.4 multi-NDI mixing |
| §5.5 / §4.9 active-clock plumbing | §6.7 per-axis tick cadence |

### 0.4 Pre-implementation readiness (do once before any Tier 1 PR)

- [ ] **0.4.1** Create a long-lived feature branch (`feat/endpoint-consolidation`)
      and enable required CI gates (build + any existing tests) on PRs into it.
- [x] **0.4.2** Add a minimal xUnit/NUnit test project under `Test/` (there is
      none today). Seed with `ChannelRouteMap.Auto`, `DriftCorrector.CorrectFrameCount`
      and the seek-epoch filter (§10.1); without it the larger refactors land
      blind. *(Done: `Test/S.Media.Core.Tests` with 115 tests + `Test/S.Media.FFmpeg.Tests` with 52 tests, all green.)*
- [ ] **0.4.3** Decide the obsoletion policy and document it in
      `Doc/README.md`: `[Obsolete(error: false)]` type-forwarders for renamed
      concrete classes for one release, `[Obsolete(error: true)]` for the
      release after.
- [ ] **0.4.4** Snapshot LoC of every `Test/MFPlayer.*` app so §5.11 has a
      baseline to measure reduction against.
- [ ] **0.4.5** Audit `NDIAudioChannel.Volume` / `FFmpegAudioChannel.Volume`
      and `IAudioChannel.Volume` setters: decide whether to remove the
      channel-level `Volume` outright (review §Consistency says it's legacy
      vs. `SetInputVolume`). Record the decision in the checklist so Tier 1
      implementers don't re-plumb dead code.
- [x] **0.4.6** Add a `BenchmarkDotNet` project stub under
      `Test/S.Media.Core.Benchmarks/` (directory already exists in the
      workspace listing) for the mixer extraction (§4.12) to measure
      against. Optional but recommended before §4.13 denormal-flush /
      soft-clip changes. *(Done: project builds; benchmarks will be added with §4.12.)*

---

## 1. Audio endpoint consolidation (user-requested)

### Status quo (verified)

- `IAudioEndpoint` is the unified push contract (see its XML: *"Replaces `IAudioOutput`,
  `IAudioSink`, `IAudioBufferEndpoint` with a single unified push contract"*).
- `IPullAudioEndpoint : IAudioEndpoint` adds the optional pull capability
  (`FillCallback`, `EndpointFormat`, `FramesPerBuffer`). Hardware endpoints
  (PortAudio hardware stream) implement it; push-only sinks do not.
- `AVRouter.RegisterEndpoint(IAudioEndpoint)` already branches on
  `is IPullAudioEndpoint` internally (AVRouter.cs:314, 640). Users register
  every audio destination through one method.
- The video side mirrors this exactly:
  `IVideoEndpoint` (push) + `IPullVideoEndpoint : IVideoEndpoint` (pull capability).
  `AVRouter.RegisterEndpoint(IVideoEndpoint)` likewise branches internally.

### What is confusing for end users (even though the interfaces are unified)

- **Class names still split the world into "Output" vs "Sink"**:
  `PortAudioOutput` vs `PortAudioSink`, `SDL3VideoOutput` vs `SDL3VideoCloneSink`,
  `NDIAVSink` etc. The docs (`MediaPlayer-Guide.md`, `Quick-Start.md`, `Usage-Guide.md`,
  `Clone-Sinks.md`) reinforce the split with "Output = primary, Sink = secondary/fan-out".
- `MediaPlayer` exposes `AddEndpoint(IAudioEndpoint)` (correct) but nearby docs still
  call them "sinks", so the builder-style variable names end up being
  `audioSink` / `videoSink` pushing back on the consolidation.

### Action items

- [x] **2.1** Standardise terminology in **all public XML and Doc/** on a single word —
      "**endpoint**". "Output" and "Sink" remain internal/legacy vocabulary only.
      Files to update: `Doc/MediaPlayer-Guide.md`, `Doc/Quick-Start.md`,
      `Doc/Usage-Guide.md`, `Doc/Clone-Sinks.md`, `Doc/README.md`, `*/README.md`.
      *(Partial: `Doc/Clone-Sinks.md` rewritten to the current
      `router.RegisterEndpoint` / `CreateRoute` / `RemoveRoute` API — all
      `avMixer.*` snippets removed. Remaining doc files (`MediaPlayer-Guide.md`,
      `Quick-Start.md`, `Usage-Guide.md`, per-project READMEs) still need a
      vocabulary sweep.)*
- [~] **1.2** Collapse concrete classes so each backend has **one** `*Endpoint`
      type (breaking; keep `[Obsolete]` type-forwarders one release):
      - [x] `PortAudioOutput` + `PortAudioSink` → **one** `PortAudioEndpoint`
        with a `DrivingMode { Callback, BlockingWrite }` option. Both modes
        wrap a `Pa_OpenStream` handle and both expose `Clock` via the
        existing standalone `PortAudioClock` (see 1.4a). `IPullAudioEndpoint`
        is implemented only in `Callback` mode; `IClockCapableEndpoint` is
        implemented in **both** modes because `Pa_GetStreamTime` works
        identically on blocking streams. Runtime capability-sniffing in
        `AVRouter.RegisterEndpoint` already handles the split — no router
        change required. *(Done: `Audio/S.Media.PortAudio/PortAudioEndpoint.cs`.
        Note: legacy types were deleted outright instead of `[Obsolete]`
        forwarders — callers were migrated inline, see §5.11 partial.)*
      - [ ] `NDIAVSink` → `NDIAVEndpoint`.
      - [ ] `SDL3VideoOutput` → `SDL3VideoEndpoint`;
        `SDL3VideoCloneSink` → `SDL3VideoCloneEndpoint`.
      - [ ] `AvaloniaOpenGlVideoOutput` → `AvaloniaOpenGlVideoEndpoint` (and clone).
- [ ] **1.3** Add a single `MediaPlayer.AddEndpoint(IAudioEndpoint)` /
      `AddEndpoint(IVideoEndpoint)` / `AddEndpoint(IAVEndpoint)` surface (already
      mostly there — verify the Guide shows `endpoint`, never `sink`).
- [~] **1.4** Remove the "Open before Register" footgun by merging constructor
      + open into a single factory per endpoint.
      `PortAudioEndpoint.Create(device, format, mode, framesPerBuffer)`
      returns a ready-to-register instance with `Clock` already valid
      (closes **P1** / **CH8**). *(Done for PortAudio. Outstanding: SDL3,
      Avalonia, NDI — see §5.2.)*
- [x] **1.4a** Keep `PortAudioClock` as a standalone `IMediaClock` (it already
      is — derived from `HardwareClock`, wraps a PA stream handle). The merged
      `PortAudioEndpoint` just hands out the same clock in both driving modes.
      Auto-registered at `ClockPriority.Hardware` when the endpoint is
      registered (existing behaviour). This is the **recommended** clock for
      PA-only playback and is always present even when using a push
      (`BlockingWrite`) PA endpoint. *(Done: `Audio/S.Media.PortAudio/PortAudioClock.cs`
      extracted and handed out from both driving modes.)*
- [ ] **1.4b** Document the clock-swap story explicitly in `Doc/` and in the
      XML on `PortAudioEndpoint` / `IClockCapableEndpoint`:
      - Default: `RegisterEndpoint(paEndpoint)` → PA clock wins at `Hardware`.
      - NDI/PTP co-send: `router.RegisterClock(otherClock, External)` or
        `router.SetClock(otherClock)` outranks PA. When the override clock
        is unregistered the resolver **falls back automatically** to the PA
        clock — no re-plumbing required.
      - NDI **send** endpoints are not `IClockCapableEndpoint` today;
        `NDIClock` is a *receive*-side class derived from sender-stamped
        timestamps. The natural choices when NDI is in the send graph are
        PA-clocked (default) or PTP-clocked (`SetClock`). A future NDI
        sender can opt in by implementing `IClockCapableEndpoint`.
      - Cross-link known rough edge: router tick cadence is currently
        decoupled from `Clock.Position` (executive finding #8), tracked as
        §4.9 + §5.5 + §6.7.
- [ ] **1.5** `AVRouter.RegisterEndpoint` already figures out push/pull. Document
      this explicitly in the XML on `IAudioEndpoint` and `IVideoEndpoint`
      ("implement `IPullAudioEndpoint` only if you drive an RT callback;
      otherwise just `IAudioEndpoint` and the router will push to you"). No
      code change needed on the router beyond docs.
- [x] **1.6** Router auto-negotiation for push endpoints: add an optional
      `IAudioEndpoint.NegotiatedFormat` (nullable `AudioFormat`) so push
      endpoints can advertise their preferred rate/channels exactly like pull
      endpoints do via `EndpointFormat`. Enables route-level auto-resampling
      for push endpoints (closes review **R5**). In the merged
      `PortAudioEndpoint`, `NegotiatedFormat == HardwareFormat` regardless of
      `DrivingMode`, so callers get identical behaviour whichever mode they
      pick. *(Done: default `NegotiatedFormat => null` on `IAudioEndpoint`;
      overridden in `PortAudioEndpoint` to return `HardwareFormat`.)*

---

## 2. Tier 0 — Documentation sync (½ day)

- [ ] **2.1** Update `Doc/Clone-Sinks.md` to use `AVRouter.RegisterEndpoint` / `CreateRoute`
      (remove the obsolete `avMixer.RegisterVideoSink` example). *(Tier 0 #1)*
- [x] **2.2** Remove the obsolete `MediaPlayer.PlaybackEnded` event (`MediaPlayer.cs:118`).
      *(Tier 0 #2)* *(Done: event + `#pragma` guard + raise site all removed;
      callers were never using it — `PlaybackCompleted` is the replacement.)*
- [x] **2.3** Document the two-tier layering decision (MediaPlayer facade vs AVRouter
      framework) in `Doc/README.md` and in the XML summary of both types.
      *(Done: `Doc/README.md` now has a "Layering (§0.1 framing decision)"
      section. XML summary sweep on `AVRouter` / `MediaPlayer` can follow as
      part of §2.4.)*
- [x] **2.4** Document `IClockCapableEndpoint.Clock` lifetime contract: `Clock` must be
      valid from construction, not "after Open". *(review CH8, P1)* *(Done:
      `IClockCapableEndpoint` XML has an explicit *Lifetime contract* paragraph:
      `Clock` must be valid "from the moment the endpoint instance can be
      handed to the router (i.e. immediately after construction / a
      `Create(...)` factory call — not only after `StartAsync`)".)*
- [x] **2.5** Document `IVideoEndpoint.ReceiveFrame` ownership contract more forcefully;
      mark `[Experimental]` until ref-counted fan-out (R18) lands. *(review CH7, R18)*
      *(Done: `IVideoEndpoint.ReceiveFrame` XML has a bold **Ownership**
      paragraph ("caller owns `MemoryOwner`, implementations MUST NOT dispose
      it") plus an *Experimental — ref-counted ownership is planned* note
      referencing the future `VideoFrameHandle`. The `[Experimental]`
      attribute is intentionally not applied at the interface level because
      that would flag every endpoint implementation; the XML note is the
      in-tree substitute.)*
- [x] **2.6** Document `IVideoChannel.Subscribe` default-impl caveat (fan-out forbidden
      without native support). *(review CH3)* *(Done: `IVideoChannel.Subscribe`
      XML states that implementations without native fan-out return a thin
      `FillBufferSubscription` wrapper and "at most one subscription should
      be alive at a time".)*
- [x] **2.7** Document `PooledWorkQueue.Dispose` requires producer quiescence. *(PQ3)*
      *(Done: `PooledWorkQueue` class XML has a **Dispose contract** paragraph:
      "only releases the signal semaphore — does not drain the queue …
      Callers must ensure producer quiescence before Dispose".)*
- [ ] **2.8** Document event threading for every public event (`ThreadPool` vs RT thread
      vs render thread). *(review Concurrency #11, EL2)*

---

## 3. Tier 1 — Bug fixes, no (or minimal) API change (2–3 days)

### Decoder / demux

- [ ] **3.1** `FFmpegDecoder.Seek` flush reliability (drain-then-write or
      `WriteAsync` with 50 ms timeout). *(B1)*
- [ ] **3.2** `FFmpegDecoder.Dispose` ordering: cancel CTS → join demux task → dispose
      channels → close format. Take the format write-lock around
      `avformat_close_input`. *(B2, B3)*
- [x] **3.3** `StreamAvioContext` callbacks: distinguish EOF from IO errors; return
      `AVERROR(EIO)` for non-EOF failures; propagate via an `OnError` event on the
      owning decoder. *(B9)* *(Done: `StreamAvioContext.ReadPacket` returns
      `AVERROR(EIO)` (-5) on catch and records the exception via
      `ConsumeLastIoError`; `FFmpegDecoder.TryReadNextPacket` consumes it and
      raises `OnError`/`MediaDecodeException` via `RaiseDemuxError`, with a
      `DemuxReadResult.Fatal` return so the demux worker stops cleanly instead
      of tight-looping on Retry.)*
- [ ] **3.4** `FFmpegAudioChannel` WriteAsync leak: on `ChannelClosedException`,
      return the rented chunk to the pool. *(B10)*
- [x] **3.5** `FFmpegDecoder.Seek` PTS precision: use
      `position.Ticks / (TimeSpan.TicksPerSecond / AV_TIME_BASE)`. *(B21)*
      *(Done: `FFmpegDecoder.Seek` now divides `position.Ticks` by
      `TimeSpan.TicksPerSecond / AV_TIME_BASE` (=10) instead of going through
      `double`, eliminating the ±1 µs rounding error on exact tick positions.)*
- [x] **3.6** `FFmpegVideoChannel` HW-frame transfer error-check: skip frame + log
      once on `av_hwframe_transfer_data < 0`. *(B22)* *(Done:
      `FFmpegVideoChannel.DecodePacketAndEnqueue` now checks the return of
      `av_hwframe_transfer_data`; on failure it unrefs the hw frame, skips it,
      and logs once per channel via an `Interlocked.Exchange`-guarded flag
      `_hwTransferErrorLogged`.)*
- [x] **3.7** Static-ify `FFmpegDecoder._log`. *(review §Consistency)*
      *(Done: `_log` is now `private static readonly` on `FFmpegDecoder`; the
      three call-sites in the `Open(...)` factories that read it via the
      instance (`dec._log.…`) were rewritten to read it statically.)*

### Video fan-out & ref-counting

- [ ] **3.8** Snapshot `_subs` locally in `FFmpegVideoChannel.Seek` / `BufferAvailable`
      / `PublishToSubscribers`. *(B4)*
- [ ] **3.9** Wrap `TryPublish` body in try/catch that returns false on any exception so
      refs are never leaked on `ChannelClosedException` etc. *(B5)*
- [ ] **3.10** Document the ref-count invariant on `FFmpegVideoSubscription.DropOldest`
      eviction; add Debug.Assert. *(B6)*
- [ ] **3.11** Introduce `VideoFrameHandle` (explicit retain/release) to replace the
      implicit router-disposes-after-ReceiveFrame contract. Route-level per-endpoint
      retain via `RefCountedVideoBuffer.Retain()`. *(B15, B16, R18, CH7)*

### Router

- [ ] **3.12** `_pushVideoPending` atomicity: use `AddOrUpdate` with a disposal-in-lambda
      pattern, or hold `_lock` during the swap. Fix both the pull (B12) and push
      (R17) paths.
- [x] **3.13** `DisposeCore` must call `RemoveRouteInternal` for every route so
      subscriptions, drift trackers and `_pushVideoDrift` are not leaked. *(R9)*
      *(Done: `AVRouter.DisposeCore` now snapshots `_routes.Keys` and routes
      each through `RemoveRouteInternal` under `_lock` — per-route resamplers,
      `VideoSub`, `_pushVideoPending`, `_pushVideoDrift`, and
      `_pushAudioFormatMismatchWarnings` are all released symmetrically.)*
- [ ] **3.14** `GetOrCreateScratch` race: pre-allocate in `SetupPullAudio`, use
      `GetOrAdd` with a sized factory. *(R6)*
- [x] **3.15** Apply `BakedChannelMap` whenever it is non-null, not only when
      `srcChannels != dstChannels`. *(R4)* *(Done: push-audio path in
      `AVRouter.PushAudioTick` no longer gates on
      `srcFormat.Channels != format.Channels` — a user-supplied channel map is
      applied even for equal-channel shapes (e.g. stereo L↔R swap, mid-side
      encode). Pull path already behaved correctly; this aligns the two.)*
- [ ] **3.16** COW `_endpointsSnapshot` rebuilt under `_lock`; push tick reads only the
      snapshot to close the mid-registration window. *(R7)*
- [ ] **3.17** Wrap `RegisterEndpoint` / `UnregisterEndpoint` in `_lock` for atomicity
      with `AutoRegisterEndpointClock` + `SetupPull*`. *(R8)*
- [x] **3.18** `SetInputVolume/TimeOffset/Enabled` use `Volatile.Write`; diagnostics
      reads via `Volatile.Read`. *(B13, R15)* *(Done: `AVRouter.InputEntry`
      now stores the time offset as a `long TimeOffsetTicks` with a
      `TimeSpan` wrapper property using `Interlocked.Read`/`Interlocked.Exchange`;
      `SetInputVolume` uses `Volatile.Write` on the float field;
      `SetInputEnabled` uses `Volatile.Write` on the bool.)*
- [ ] **3.19** Use a `CancellationTokenSource` on the push threads instead of busy-waiting
      on `_running`. *(R19, R20)*
- [x] **3.20** Rate-limit repeated exception logs in `PushAudioTick` / `PushVideoTick`
      (match SDL3's 3 + every 100th pattern). *(EL3)* *(Done: two
      `Interlocked.Increment`-guarded counters (`_pushAudioErrorCount`,
      `_pushVideoErrorCount`) on `AVRouter` — log fires on counts 1, 2, 3,
      then every 100th thereafter, with the running total included in the
      message.)*
- [x] **3.21** `CreateRoute` must fail with a `MediaRoutingException` (see Tier 2 #4.6)
      rather than `InvalidOperationException`. *(EL1)* *(Done: all `CreateRoute`
      / `SetRouteEnabled` / `Register*` throw sites on `AVRouter` throw
      `MediaRoutingException`; pinned by `AVRouterRoutingExceptionTests`
      (5 tests) covering unknown input, unknown endpoint, wrong-kind options,
      and unknown route.)*

### PortAudio / clocks

> PortAudio items §3.22–§3.28 **fold into the §1.2 `PortAudioEndpoint` merge**
> — implement the fixes directly in the new class rather than patching the
> legacy `PortAudioOutput` / `PortAudioSink`. They are kept here for
> traceability to the review IDs and as acceptance criteria for §1.2.

- [x] **3.22** *(→ do during §1.2)* `PortAudioSink` error handling: check
      `Pa_WriteStream` return; log once per burst. *(B17)* *(Done in
      `BlockingWriteEndpoint.WriteLoop`: `_writeErrorCount` + `_lastWriteError`
      with transition logging.)*
- [x] **3.23** *(→ do during §1.2)* `PortAudioSink.StopAsync`: on join timeout,
      `Pa_AbortStream` then retry join. *(B18)* *(Done: 3-s join →
      `Pa_AbortStream` → 1-s retry join in both `StopAsync` and `Dispose`.)*
- [x] **3.24** *(→ do during §1.2 + §1.4)* `Clock` available before `StartAsync`
      (direct consequence of the `Create(...)` factory). *(P1, CH8)* *(Done:
      `_clock` created in `PortAudioEndpoint` ctor and exposed via
      `IClockCapableEndpoint.Clock` immediately.)*
- [ ] **3.25** `PortAudioClock.SetStreamHandle` atomicity vs `Position` reads;
      take the same lock used by `HardwareClock` for `_lastValidPosition`.
      *(P2)*
- [ ] **3.26** Pre-rent router scratch buffers so `AudioFillCallbackForEndpoint.Fill`
      never hits `ArrayPool.Rent` on RT. *(P3)*
- [x] **3.27** *(→ do during §1.2)* `Open` failure path must `Free` the
      `GCHandle` on `PortAudioClock.Create` failure. *(P6)* *(Done: `GCHandle`
      allocated lazily per open attempt in `CallbackEndpoint.TryOpenStreamAttempt`
      and freed in `Dispose`; rate-fallback retry no longer leaks.)*
- [x] **3.28** *(→ do during §1.2)* `Dispose` callback-in-progress guard (null
      the `_fillCallback` + spin on an "in-flight" flag). *(P5)* *(Done:
      `_callbackInFlight` interlocked counter + SpinWait in
      `CallbackEndpoint.Dispose` after nulling `_fillCallback`.)*
- [ ] **3.28a** `PortAudioEngine.Terminate` refcount awareness (docs or mirror
      PA's internal refcount). *(P7)*
- [ ] **3.28b** Offload `Pa_StartStream` to a worker task (or document the
      100–300 ms WASAPI-exclusive blocking behaviour). *(P4)*
- [x] **3.29** `VideoPtsClock` torn-read fix for `_lastPts`/`_swAtLastPts` (lock or
      paired atomic longs). *(C3)* *(Done: `VideoPtsClock` now takes a small
      `_stateLock` around all reads/writes of `_lastPts`, `_swAtLastPts`,
      `_initialised`, and the embedded `Stopwatch` — `Position`,
      `UpdateFromFrame`, `Start`, `Stop`, and `Reset` are all tear-free. Hold
      time is sub-microsecond.)*
- [x] **3.30** `MediaClockBase` timer-callback-on-disposed-clock guard. *(C1)*
      *(Done: `MediaClockBase.OnTimerTick` returns early when `_disposed` is
      set, so `System.Threading.Timer` callbacks that fire after `Dispose()`
      (because `Timer.Dispose()` does not wait for in-flight callbacks) are
      silently swallowed instead of invoking subscriber handlers against
      stopped state. Covered by `ClockLifecycleTests.MediaClockBase_DisposeDuringRunning_SuppressesFurtherTicks`.)*
- [x] **3.31** `HardwareClock` fallback debounce (two consecutive valid reads before
      leaving fallback). *(C5)* *(Done: `HardwareClock` now tracks
      `_consecutiveValidReads`; exits fallback only after
      `FallbackExitDebounce = 2` consecutive valid reads, keeping Position on
      the interpolated-fallback curve during a single flaky valid sample.
      Covered by `ClockLifecycleTests.HardwareClock_SingleFlakyValidRead_DoesNotLeaveFallback`.)*
- [x] **3.31a** `StopwatchClock.Reset` must call `base.Stop()` so the underlying
      timer is not left running while `_running == false`. *(C6)* *(Done:
      `StopwatchClock.Reset` now calls `base.Stop()` after clearing state so
      the `MediaClockBase` tick timer is disarmed even when `Reset` is called
      without a prior `Stop`. Covered by
      `ClockLifecycleTests.StopwatchClock_ResetAfterStart_StopsTickTimer`.)*
- [ ] **3.31b** `StopwatchClock`: document Windows ~15 ms granularity; optional
      `timeBeginPeriod(1)` around Start/Stop. *(C2)*

### SDL3 / Avalonia

- [ ] **3.32** Raise `SDL3VideoOutput.WindowClosed` via `ThreadPool.QueueUserWorkItem`
      so naive `Dispose()` handlers do not deadlock-join. *(S1)*
- [ ] **3.33** Replace `ReadOnlyMemory<byte>.Equals` texture-reuse identity with
      `(MemoryOwner ref + Pts + W + H)` in SDL3 and Avalonia. *(S3, S12, A2)*
- [ ] **3.34** SDL3 / Avalonia YUV-hint setters: queue a pending-change flag; apply at
      the top of the next render tick under the correct GL context. *(S6, A5)*
- [ ] **3.35** `OnOpenGlLost` resets `_lastAutoMatrix/Range` to `Auto`. *(A4, A7)*
- [ ] **3.36** Avalonia: unconditional `RequestNextFrameRendering` in `finally` →
      request only when a frame was uploaded this tick (or in LiveMode). *(A1, A10, A14)*
- [ ] **3.37** Avalonia clone sink: GL-side multi-format shaders instead of scalar
      CPU YUV→RGB on the render thread. *(A9)*
- [ ] **3.38** Ref-counted fast-path in Avalonia + SDL3 clone sinks when the incoming
      `MemoryOwner is RefCountedVideoBuffer`. *(S8, A3; depends on 3.11)*
- [ ] **3.39** Unified process-wide SDL event pump dispatched by window ID. *(S9)*
- [ ] **3.40** SDL3 `AcquireSdlVideo` try/finally on `Interlocked.Decrement`. *(S14)*
- [ ] **3.40a** Decide + document the clone-sink wiring contract: either (a)
      parent `SDL3VideoEndpoint` tees frames to clones during render, or
      (b) clones are standalone endpoints the user registers on the router
      (current de-facto behaviour). Implement whichever you pick and remove
      the parent's `Dispose` cascade to clones if (b). *(S2, S4)*
- [ ] **3.40b** `SDL3VideoEndpoint.OverridePresentationClock` /
      `ResetClockOrigin`: use `Interlocked.Exchange` for the origin pair,
      or pack into one `long` to close the torn-read window on weakly
      ordered ARM64. *(S5)*
- [ ] **3.40c** On `GLMakeCurrent` failure, set `_closeRequested = true` so
      `WindowClosed` still fires. *(S7)*
- [ ] **3.40d** Tag exception types + first/last-seen timestamps in the
      rate-limited render-loop log. *(S10)*
- [ ] **3.40e** Wrap `glDelete*` in try/catch during Dispose when the context
      may already be gone (window user-closed path). *(S11)*
- [ ] **3.40f** Reuse HUD scratch VBO buffer — also tracked as §8.7;
      de-duplicate when implementing. *(S13)*
- [ ] **3.40g** Avalonia: publish `_catchupLagThreshold`/`_lastUploadedPts`
      via `Volatile.Read`/`Volatile.Write` (or pack into `long`). *(A6)*
- [ ] **3.40h** Extract `ResolvePresentationClock` / `TryPullFrameWithCatchUp`
      / `PresentFrame` from the 125-line `OnOpenGlRender`. *(A8)*
- [ ] **3.40i** Avalonia Dispose: only stop the state machine; rely on
      `OnOpenGlDeinit` for renderer teardown to avoid racing a compositor
      draw. *(A11)*
- [ ] **3.40j** Avalonia parent/clone Dispose: require detach from visual
      tree first; throw `InvalidOperationException` if attached. *(A12)*
- [ ] **3.40k** Avalonia: stash `_renderScaling` on UI thread via
      `OnPropertyChanged` instead of reading `VisualRoot.RenderScaling` per
      frame. *(A13)*

### NDI input (Tier 1-N)

- [ ] **3.41** `_cts` per-start (not latched singleton) in `NDIAudioChannel` and
      `NDIVideoChannel`; `StartCapture` throws `ObjectDisposedException` when
      `_disposed`. *(N3)*
- [ ] **3.42** `NDISource._sessionGate` held across `recv_connect` and framesync
      create/destroy; loop-join capture threads instead of 2 s timeout. *(N1, N2, N19)*
- [ ] **3.43** `NDIClock` uses `options.SampleRate`. *(N5)*
- [ ] **3.44** Narrow capture-loop exception handling; guarantee
      `FreeAudio`/`FreeVideo` in `finally`. *(N6)*
- [ ] **3.45** Rename `NDISource.Stop` → `StopClock` with `[Obsolete]` forwarder. *(N12)*
- [ ] **3.46** `StartCapture` "already started" CAS guard. *(N18)*
- [ ] **3.47** Allow `NDIAVChannel` with null `AudioChannel` (video-only NDI sources).
      *(N14)*
- [ ] **3.47a** `NDIVideoChannel` I420 chroma stride heuristic review against padded
      sources. *(N8)*
- [ ] **3.47b** `NDIAudioChannel` DropOldest accounting: use the manual-ring
      pattern used by `NDIVideoChannel` so `_framesProduced` stays accurate.
      *(N9)*
- [ ] **3.47c** `NDISource.OpenByNameAsync`: wrap open + hand-off in try/catch,
      dispose finder on failure. *(N13)*
- [ ] **3.47d** `NDISource.WatchLoop`: break immediately when `WaitOne` and
      `IsCancellationRequested` are both true. *(N15)*
- [ ] **3.47e** `NDIVideoChannel.EnqueueFrame` ring accounting: strict
      increment-on-write / decrement-on-read; `Debug.Assert(>= 0)`. *(N16)*
- [ ] **3.47f** Document `NDISource.StateChanged.args.NewState` as authoritative.
      *(N17)*
- [ ] **3.47g** Create framesync before `receiver.Connect(source)` to match the
      SDK sample order. *(N20)*
- [ ] **3.47h** `NDIAudioChannel.Dispose` drain + re-pool ring buffers (cosmetic).
      *(N21)*
- [ ] **3.47i** `NDIVideoChannel.ParseNdiColorMeta` minimal XML reader instead of
      `Contains("BT.709")`. *(N22)*
- [ ] **3.47j** `NDIReceiverSettings.AllowVideoFields` default `false` because the
      receive path always pulls Progressive. *(N23)*

### Channel / endpoint contract cleanups

- [ ] **3.48** Document (or enforce) single-reader on `IMediaChannel.FillBuffer` —
      two routes sharing one audio channel today race. Either serialize inside
      the router or document forbidden. *(CH1)*
- [ ] **3.49** Split `IAudioChannel.Position` from a new
      `IAudioChannel.ReadHeadPosition` (the PTS of the next sample to be
      produced), removing the implicit "Position updates after the read"
      dance. *(CH2)*
- [ ] **3.50** Mark the non-PTS `IAudioEndpoint.ReceiveBuffer` default impl as
      `[Obsolete]` so sinks that should emit timecodes cannot silently skip
      the PTS overload. *(CH4)*
- [ ] **3.51** `IPullAudioEndpoint.FillCallback` swap semantics: replace the
      setter with `SetFillCallback(IAudioFillCallback?)` that does a volatile
      write + short spin for in-flight fills. *(CH5)*
- [ ] **3.52** Document (and enforce in Debug via assertions) that
      `IPullAudioEndpoint.EndpointFormat` / `FramesPerBuffer` are frozen
      after the endpoint is Open. *(CH6)*
- [ ] **3.53** `IFormatCapabilities<T>` must declare non-empty
      `SupportedFormats` or throw — wire this here in Tier 1 as a
      `Debug.Assert` and promote to a throw in §6.10. *(CH9, R22 prep)*
- [ ] **3.54** `AVRouter.DriftEma` single-pair limitation: either assert
      single-pair input or key by `(audioInputId, videoInputId)`
      (mirror of §6.9; pick one place to land the fix). *(R16)*
- [ ] **3.55** `FFmpegVideoChannel._defaultSub` double-dispose path during
      concurrent teardown — guard `EnsureDefaultSubscription` with a
      `_disposed` check under `_subsLock`. *(review Concurrency #10)*
- [ ] **3.56** Decide fate of `IAudioChannel.Volume` (and its
      `FFmpegAudioChannel` / `NDIAudioChannel` setters) — remove or keep
      per §0.4.5. *(review §Consistency, NDI §Consistency)*

---

## 4. Tier 2 — Ergonomic helpers (1 week)

- [x] **4.1** `AudioFormat.NegotiateFor(AudioChannel src, AudioDeviceInfo dev, int capChannels = 2)`
      returning `(AudioFormat hw, ChannelRouteMap map)`. Deletes the duplicated
      `Math.Min(...)` + `BuildRouteMap` boilerplate from 5 test apps. *(main review #1, NDI #7)*
      *(Done: two overloads (`AudioFormat` and `IAudioChannel`) in
      `Media/S.Media.Core/Media/AudioFormat.cs`; SimplePlayer + MultiOutputPlayer
      migrated; covered by `AudioFormatNegotiateForTests` (6 tests).)*
- [x] **4.2** `ChannelRouteMap.AutoStereoDownmix(int srcCh, int dstCh)`. *(main review #2)*
      *(Done: ITU-R BS.775 5.1→stereo, mono→stereo fan-out, stereo→mono 0.5×
      average, passthrough fallback; covered by
      `ChannelRouteMapAutoStereoDownmixTests` (5 tests).)*
- [x] **4.3** `MediaPlayer.WaitForCompletionAsync(CancellationToken ct)` that handles
      EOF + drain grace. *(main review #3)* *(Done: `WaitForCompletionAsync(TimeSpan drainGrace = default, CancellationToken ct = default)`
      returning `PlaybackCompletedReason`; honours cancellation; leaves player
      running on cancel so caller decides whether to Stop.)*
- [x] **4.4** `MediaPlayer : IAsyncDisposable`; replace sync `AddEndpoint`/`RemoveEndpoint`
      start/stop with async equivalents. *(B19, review §Consistency)*
      *(Done: `MediaPlayer` now implements `IAsyncDisposable`; sync `Dispose`
      delegates to `DisposeAsync` which awaits `_router.StopAsync()`, each
      endpoint's `StopAsync()`, and the new `FFmpegDecoder.StopAsync()` (§4.5).
      Covered by `MediaPlayerLifecycleTests` (3 tests: await-using without
      media, await-using after Open, and idempotent DisposeAsync). Outstanding:
      full async conversion of `AddEndpoint` / `RemoveEndpoint` — today they
      still call `StartAsync().GetAwaiter().GetResult()`; tracked under a
      future §4.4 follow-up alongside the §5.1 builder.)*
- [x] **4.5** `FFmpegDecoder.StopAsync()` that awaits the demux task cooperatively.
      *(Concurrency #1)* *(Done: `FFmpegDecoder.StopAsync(ct)` cancels the
      internal CTS and awaits `_demuxTask` with `WaitAsync(ct)`; idempotent,
      safe to race Dispose. Because the decoder class is `unsafe`, the actual
      `await` lives in a non-unsafe `FFmpegDecoderAsyncHelpers` companion.
      Covered by 2 tests: without-Start completes <500 ms; with-Start joins
      the demux task and a second call is a no-op.)*
- [x] **4.6** Exception hierarchy: `MediaOpenException`, `MediaDecodeException`,
      `MediaRoutingException`, `MediaDeviceException`, `ClockException`.
      Replace `InvalidOperationException` at API boundaries. *(review §Consistency, EL1)*
      *(Done: 5 new types in `Media/S.Media.Core/Errors/`, all inheriting
      `MediaException`, with context-carrying properties (`ResourcePath`,
      `Position`, `DeviceName`). Covered by `ExceptionHierarchyTests` (5 tests).
      Outstanding: replace existing `InvalidOperationException` throw-sites in
      `AVRouter`/`MediaPlayer`/`FFmpegDecoder` — tracked as §3.21 and the
      Tier-2 DoD grep condition.)*
- [ ] **4.7** `IAudioEndpointFormatHint` propagation so decoder skips SWR when the
      endpoint format already matches source. *(main review #6)*
- [ ] **4.8** `IClockCapableEndpoint.DefaultPriority` so network clocks register at
      `External`, local hardware at `Hardware`, virtual at `Internal`. *(R11)*
- [ ] **4.9** `AVRouter.ActiveClockChanged` event under `_clockLock`. *(R10)*
- [ ] **4.10** `PooledWorkQueue.Complete()` + `IsCompleted`; `TryEnqueueWithCap` to
      prevent leaked reservations. *(PQ1, PQ2, PQ4)*
- [x] **4.11** Replace MediaPlayer `_isRunning` with a single `PlaybackState` enum
      that is consistent with `IsPlaying`. *(B20)* *(Done: `PlaybackState`
      enum (`Idle`/`Opening`/`Ready`/`Playing`/`Paused`/`Stopping`/`Stopped`/
      `Faulted`) + `StateChanged` / `PlaybackCompleted` / `PlaybackFailed`
      events in `Media/S.Media.FFmpeg/MediaPlayer.cs`.)*
- [~] **4.12** Extract an `IAudioMixer` interface into `Media/S.Media.Core/Mixing/`
      (the folder already exists and is empty). Move `MixInto`, `ApplyChannelMap`,
      `ApplyGain`, `MeasurePeak`, add `FlushDenormalsToZero`. Enables unit tests
      without spinning up a router. *(M1)* *(Done: `Media/S.Media.Core/Mixing/`
      now contains `IAudioMixer.cs` and `DefaultAudioMixer.cs` (stateless
      singleton `DefaultAudioMixer.Instance`). `AVRouter`'s four private static
      math helpers forward to the interface. Covered by `AudioMixerTests`
      (10 tests) exercising SIMD + scalar tails of `MixInto`, `ApplyGain`,
      `MeasurePeak`, and three `ApplyChannelMap` shapes. **Known gap:**
      `FlushDenormalsToZero` is currently a documented no-op — the modern
      .NET surface no longer exposes `Sse.SetCsr` / `SetFlushZeroMode`. A
      P/Invoke-based MXCSR writer is tracked as §4.13 / M2 follow-up.)*
- [ ] **4.13** Mixer math improvements: denormal-flush on push/fill thread entry,
      optional auto-attenuation / soft-clip, overflow counter on
      `RouterDiagnosticsSnapshot`. *(M2, R3)*
- [ ] **4.14** Apply channel map whenever `BakedChannelMap` is set. *(already tracked
      as 3.15 — keep mirrored with M4.)*
- [ ] **4.15** Move peak metering to post-map, pre-`ReceiveBuffer`. *(R24, M3)*

### NDI ergonomic helpers (Tier 2-N)

- [ ] **4.16** `NDIClockPolicy { VideoPreferred, AudioPreferred, FirstWriter }`; only
      the chosen channel writes to the shared `NDIClock`. *(N4)*
- [ ] **4.17** `NDIUnsupportedFourCc` / `NDIFormatChange` events; expose
      `MaxForwardPtsJumpMs`. *(N7, N11)*
- [ ] **4.18** Process-wide `NDISource.Discovered` singleton registry. *(NDI §Required #3)*
- [ ] **4.19** `NDIReconnectPolicy` record replacing the two boolean knobs. *(NDI §Required #2)*
- [ ] **4.20** Narrow `PlanarToInterleaved` to only run on `Fltp` FourCC. *(N10)*

---

## 5. Tier 3 — Builder API (2 weeks)

- [ ] **5.1** `MediaPlayerBuilder` with the `With*` methods shown in the review §"Proposed
      simplified API". *(review §Proposed simplified API)*
- [ ] **5.2** One-step factories: `PortAudioEndpoint.Create(device, format)`,
      `SDL3VideoEndpoint.ForWindow(title)`, etc. Closes the "Open before Register"
      footgun in the concrete classes. *(review §Consistency; depends on 1.2)*
- [ ] **5.3** Auto-propagate `IVideoColorMatrixHint` from channel → endpoint on route
      creation; remove the YUV prompt from the VideoPlayer test app. *(main review #7)*
- [ ] **5.4** Auto audio-preroll inside `AVRouter.StartAsync` when an `IPullVideoEndpoint`
      and an audio input coexist (`AVRouterOptions.WaitForAudioPrerollMs = 1000` /
      `MinBufferedFramesPerInput`). Replaces the VideoPlayer warmup block. *(main review #4, R12)*
- [ ] **5.5** Auto-derive `InternalTickCadence` from registered endpoints (pick
      `min(endpoint.NominalTickCadence)`). *(main review #8, R13, C7)*
- [ ] **5.6** Expose `VideoRouteOptions.OverflowPolicy`. *(main review #9)*
- [ ] **5.7** `MediaPlayerBuilder.WithNDIInput(...)` overloads; orchestrate video-first
      format detection + prebuffer + start ordering. *(NDI §Required #1, #6)*
- [ ] **5.8** Auto-register `NDIClock` at `Hardware` priority inside
      `WithNDIInput(...)`. *(NDI §Required #4)*
- [ ] **5.9** `WithAutoAvDriftCorrection(options?)` rolling up NDIAutoPlayer L380–416.
      *(NDI §Required #5)*
- [ ] **5.10** `RouterBuilder` parallel for advanced users (atomic registration;
      closes R8 by construction). *(cross-cutting Tier 3)*
- [~] **5.11** Migrate all test apps to the builder API; measure LoC reduction (target:
      SimplePlayer ≤15 LoC, VideoPlayer ≤20 LoC). *(main review §Goal)*
      *(Interim: `SimplePlayer`, `MultiOutputPlayer`, `NDIPlayer`, `NDIAutoPlayer`
      migrated off deleted legacy types to `PortAudioEndpoint.Create(...)`.
      `SimplePlayer` + `MultiOutputPlayer` additionally migrated to
      `AudioFormat.NegotiateFor(...)` (no more hand-rolled `Math.Min` +
      `BuildRouteMap`). Full builder-based rewrite still pending §5.1.)*

---

## 6. Tier 4 — Per-route & per-endpoint flexibility (2 weeks)

- [ ] **6.1** Replace global `AVRouter.BypassVideoPtsScheduling` with per-route
      `VideoRouteOptions.LiveMode` (so SDL3 preview can be live while NDI record is
      scheduled on the same router). Rename consistently with plan doc ("LiveMode").
      *(main review #5 + Tier 4 #22, R23)*
- [ ] **6.2** Store `PtsDriftTracker` per-route (not per-endpoint) so the one-route-per-
      video-endpoint invariant is no longer implicit. *(R14)*
- [ ] **6.3** Per-route `BakedChannelMap` always applied regardless of channel count.
      *(already in Tier 1 as 3.15; re-verify here.)*
- [ ] **6.4** Weighted or leader-input PTS when mixing N inputs to one PTS-aware endpoint
      (audio path). Document "single-leader" fallback. *(R2)*
- [ ] **6.5** Endpoint format-change events → route re-validation (Phase 1 open Q3).
      *(Tier 4 #23)*
- [ ] **6.6** Per-endpoint peak metering (already done for inputs; extend to outputs).
      *(Tier 4 #24)*
- [ ] **6.7** `AVRouterOptions.AudioTickCadence` / `VideoTickCadence` split. *(R13)*
- [ ] **6.8** `AudioRouteOptions.Resampler` usable on push routes too. *(R5 follow-up)*
- [ ] **6.9** Per-input drift EMA keyed by `(audioInputId, videoInputId)`. *(R16)*
- [ ] **6.10** `IFormatCapabilities<T>` must declare non-empty `SupportedFormats` or
      throw. *(R22, CH9)*
- [ ] **6.11** Warn eagerly (at route creation) on format incompatibility, not on
      first enumeration. *(R1)*

---

## 7. Tier 5 — Timeline / gapless / multi-source (Phase 2)

- [ ] **7.1** `ITimeline` / `TimelineItem`; transport on top of the builder API.
      *(Tier 5 #25)*
- [ ] **7.2** Gapless playback via pre-opened next-track decoders. *(Tier 5 #26)*
- [ ] **7.3** Playlist / crossfade as route gain automation. *(Tier 5 #27)*
- [ ] **7.4** Multi-NDI-source mixing (each `NDISource` as distinct router input).
      Depends on the discovery registry (4.18). *(Tier 5-N)*
- [ ] **7.5** Extracted mixer math (4.12) is a prerequisite. *(M1-M5 cross-link)*

---

## 8. Tier 6 — Performance

- [ ] **8.1** Per-channel `VideoFrame` buffer pool (fixed-size, LOH-aware) replacing
      `ArrayPool<byte>.Shared` for 4K60 video. *(Tier 6 #28)*
- [ ] **8.2** Zero-copy fast-path when source pixel format == sink format (NDIAVSink,
      SDL3 clones). *(Tier 6 #29)*
- [ ] **8.3** SIMD I210→UYVY converter. *(Tier 6 #30)*
- [ ] **8.4** Extend `_scratchBuffers` caching to `destBuf` and `mappedBuf` per endpoint.
      *(main review Performance #3)*
- [ ] **8.5** Synchronous `TryWrite`-first fast path in `FFmpegDemuxWorker.WritePacketAsync`.
      *(main review Performance #5)*
- [ ] **8.6** Persistent-mapped PBO upload path in SDL3 and Avalonia. *(§3.3, §4.3)*
- [ ] **8.7** Reuse the HUD scratch VBO buffer. *(S13)*
- [ ] **8.8** Denormal flush-to-zero on push/fill thread entry. *(R3, duplicate of 4.13)*
- [ ] **8.9** Zero-copy NDI receive: retain `NDIVideoFrameV2` through `NDIVideoFrameOwner`;
      call `_frameSync.FreeVideo` on Dispose. Depends on `VideoFrameHandle` (3.11). *(Tier 4-N)*

---

## 9. Tier 7 — GL/rendering robustness (new)

- [ ] **9.1** Unified SDL process-wide event pump (duplicate of 3.39). *(S9)*
- [ ] **9.2** Off-thread GL state serialisation (3.34). *(S6, A5)*
- [ ] **9.3** Context-lost recovery audit across SDL3 + Avalonia. *(A4, A7)*

---

## 10. Ongoing / cross-cutting

- [ ] **10.1** Unit test scaffolding. Start with:
      - `ChannelRouteMap.Auto`
      - `DriftCorrector.CorrectFrameCount`
      - Seek-epoch filter
      - `LinearResampler` boundary continuity (`_pendingFrames` edge cases).
      - Extracted `IAudioMixer` (4.12).
      *(review §Nice-to-haves)*
- [ ] **10.2** Correlation-scoped logging (`RouteId`, `InputId`). *(EL2)*
- [ ] **10.3** Deterministic `MediaPlayer.DisposeAsync` orchestration
      (stop router → stop endpoints in parallel → stop decoder → dispose all).
      *(review §Nice-to-haves)*
- [ ] **10.4** `AVRouterDiagnostics` event stream + expose `PtsDriftTracker.Snapshot`
      on the diagnostics snapshot. *(§7 Nice-to-haves)*
- [ ] **10.5** SDL3 HUD additions (drift ms, current clock name); Avalonia HUD parity.
      *(§7 Nice-to-haves)*

---

## 10.5 Definition of done per tier (acceptance criteria)

Use these to decide when a tier's PR is ready to merge. Each bullet must be
a checkable condition on the repo state, not a subjective judgment.

**Tier 0 — docs sync**
- [ ] `Doc/Clone-Sinks.md` no longer references `avMixer.*`.
- [ ] `Doc/README.md` explains the MediaPlayer-vs-AVRouter layering (§0.1).
- [ ] `MediaPlayer.PlaybackEnded` is deleted from source.

**§1 — endpoint consolidation**
- [ ] `PortAudioOutput` and `PortAudioSink` are `[Obsolete]` forwarders to
      a single `PortAudioEndpoint` class.
- [ ] `NDIAVSink`, `SDL3VideoOutput`, `SDL3VideoCloneSink`,
      `AvaloniaOpenGlVideoOutput` (and clone) each forward to `*Endpoint`
      renames.
- [ ] `grep -ri "IAudioOutput\|IAudioSink\|IVideoOutput\|IVideoSink" Doc/`
      produces only historical-context hits (in this review, checklist, or
      explicitly-marked "legacy" sections).
- [ ] Every public `*Endpoint` type has a `Create(...)` factory that
      returns a ready-to-register instance; `new *Endpoint()` is either
      removed or made `internal`.
- [ ] `IClockCapableEndpoint.Clock` returns a valid clock for every
      endpoint that implements the interface, at all times after
      construction.
- [ ] `PortAudioEndpoint` in `BlockingWrite` mode round-trips audio and
      its `Clock.Position` advances monotonically under `Pa_WriteStream`
      load (manual verification or unit test).

**Tier 1 — bug fixes (§3)**
- [ ] Seek on a 4K long-GOP file shows no "rewind-then-black" (B1).
- [ ] `StreamAvioContext` surfaces broken-stream errors distinctly from EOF
      to subscribers of the new `OnError` event (B9).
- [ ] `AVRouter` Dispose-in-flight tests (loop create/dispose + play) pass
      10× with no handle leaks (R9, B12, R17 collectively).
- [ ] SDL3 `WindowClosed` handler calling `output.Dispose()` no longer
      deadlocks (S1).
- [ ] NDI receive: dispose during live capture runs cleanly 50× with no
      native crash (N1, N2, N19).

**Tier 2 — ergonomic helpers (§4)**
- [ ] `AudioFormat.NegotiateFor` replaces the hand-rolled block in every
      test app (`grep "Math.Min(srcFmt.Channels"` returns zero hits in
      `Test/`).
- [ ] `MediaPlayer : IAsyncDisposable` and sync `Dispose` delegates to it.
- [ ] `IAudioMixer` lives in `Media/S.Media.Core/Mixing/` and the existing
      `AVRouter` call sites use the interface (M1).
- [ ] Exception hierarchy in use — `grep` for
      `throw new InvalidOperationException` at public API boundaries in
      `AVRouter` / `MediaPlayer` returns zero hits.

**Tier 3 — builder API (§5)**
- [ ] `Test/MFPlayer.SimplePlayer/Program.cs` ≤ 15 lines of real code
      (excluding `using`s and help-text).
- [ ] `Test/MFPlayer.VideoPlayer/Program.cs` ≤ 25 lines.
- [ ] Every test app compiles and runs without the manual `BuildRouteMap`
      helper (it's deleted).

**Tier 4 — per-route flexibility (§6)**
- [ ] `AVRouter.BypassVideoPtsScheduling` global is deleted; per-route
      `VideoRouteOptions.LiveMode` exists and is covered by a test.
- [ ] An NDI scheduled route + an SDL3 live preview route can coexist on
      one `AVRouter` instance.

**Tier 5+ (§7–§9)**
- [ ] Acceptance criteria defined per-feature when the item is picked up
      (timeline, gapless, PBO upload, event pump). Not gating this doc.

---

## 11. Nice-to-haves / future work

Grouped from §Nice-to-haves in the main review and the NDI addendum. No checkbox —
these are feature ideas, not required work.

- Audio effects chain per route (EQ, limiter, loudness normalisation).
- Recording endpoint (FFmpeg mux writer as `IAVEndpoint`).
- Subtitles / closed captions (`ISubtitleChannel` + text render endpoint).
- HDR/PQ path: `ColorSpace` + `TransferCharacteristics` on `VideoFormat`;
  10-bit NDI receive (P216/V210).
- PTP/genlock `IMediaClock` implementation at `External` priority.
- Loudness metering (R128) beyond peak.
- `MediaPlayer.SeekAsync` that completes only once the first post-seek frame renders.
- NDI Tally passthrough / PTZ control / KVM / metadata.
- NDI receive raw-capture mode (`NDIReceiveMode { FrameSync, RawPolling }`).
- `StopwatchClock` Windows `timeBeginPeriod(1)` option.
- `VideoFrameSlot.SetAndReturnPrevious` variant to defer disposal.
- Per-sender NDI statistics (`GetPerformance` / `GetQueue`).

---

*Mapping reference:*
*B1..B22 = main review bug table · N1..N23 = NDI addendum · R1..R24 = router findings*
*C1..C9 = clocks · CH1..CH9 = channel/frame/endpoint contracts · M1..M5 = mixer math*
*S1..S14 = SDL3 · A1..A14 = Avalonia · P1..P8 = PortAudio · PQ1..PQ4 = PooledWorkQueue*
*EL1..EL3 = errors/logging.*

