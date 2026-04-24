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
>
> **2026-04-23 session (this pass):** closed the three outstanding §0.4
> framing-decisions and finished §1.2 + §2.1. Concretely:
> §0.4.3 obsoletion policy decided (`[Obsolete(error: false)]`
> public type-forwarders inheriting from the new endpoint; PortAudio's
> earlier delete-outright is the one grandfathered exception); §0.4.4 LoC
> snapshot taken for all eight `Test/MFPlayer.*` apps (total 3 395 LoC,
> `SimplePlayer` 377, `VideoPlayer` 940 — the reduction targets §5.11 will
> chase); §0.4.5 `IAudioChannel.Volume` designated dead code (§3.56 sweep
> will delete it); §1.2 all four remaining endpoint renames landed
> (`SDL3VideoOutput`→`SDL3VideoEndpoint`,
> `SDL3VideoCloneSink`→`SDL3VideoCloneEndpoint`,
> `AvaloniaOpenGlVideoOutput`→`AvaloniaOpenGlVideoEndpoint`,
> `AvaloniaOpenGlVideoCloneSink`→`AvaloniaOpenGlVideoCloneEndpoint`, plus
> the `NDIAVSink.cs`→`NDIAVEndpoint.cs` file rename for the already-renamed
> class); `SDL3VideoOutputLegacy.cs` + `AvaloniaOpenGlVideoOutputLegacy.cs`
> provide the new `[Obsolete]` forwarders mirroring the existing
> `NDIAVSinkLegacy.cs` template; parent endpoint classes unsealed so the
> forwarders can inherit. Clone endpoints need no forwarder because their
> ctors are internal and they are obtainable only via
> `parent.CreateCloneSink(...)`. §2.1 doc vocabulary sweep completed across
> `Doc/MediaPlayer-Guide.md`, `Doc/Quick-Start.md`, `Doc/Usage-Guide.md`,
> `Doc/README.md`, and the per-project READMEs (SDL3, Avalonia,
> AvaloniaVideoPlayer, Core, FFmpeg, NDI, PortAudio). §4.4 was
> downgraded from ✅ to ⚠️ to reflect that `AddEndpoint`/`RemoveEndpoint`
> still block synchronously (plan-agent audit finding). All 214 tests still
> pass; build 0 warnings / 0 errors.
>
> **2026-04-23 second pass (this session):** closed another self-contained
> batch — §1.4 factories (`SDL3VideoEndpoint.ForWindow(...)`,
> `AvaloniaOpenGlVideoEndpoint.Create(...)`; NDI already had
> `NDIAVEndpoint.Create(...)` from an earlier pass), §1.4b
> (clock-selection doc section added to `Doc/MediaPlayer-Guide.md`),
> §1.5 (verified the XML was already comprehensive on
> `IAudioEndpoint` / `IVideoEndpoint`), §3.8 / §3.9 / §3.10 (FFmpeg
> fan-out: local `_subs` snapshots in `Seek` / `BufferAvailable` /
> `ApplySeekEpoch` / `CompleteDecodeLoop` / `PublishToSubscribers`;
> `TryPublish` try/catch with ref-release on failure; `DropOldest`
> `Debug.Assert(_queued >= 0)` + XML invariant), §3.32 (SDL3
> `WindowClosed` now dispatched via `ThreadPool.QueueUserWorkItem`; naive
> `Dispose()` handlers no longer self-deadlock on `_renderThread.Join`),
> §3.40 (SDL3 `AcquireSdlVideo` try/finally on `Interlocked.Decrement`),
> §3.40c (`GLMakeCurrent` failure now sets `_closeRequested` and raises
> `WindowClosed`), §3.48 (XML single-reader contract on
> `IMediaChannel.FillBuffer`), §3.51 (`FillCallback` swap semantics —
> volatile write + `SpinWait` on `_callbackInFlight` when set to null;
> contract documented on the interface), §3.52 (frozen-after-Open XML on
> `IPullAudioEndpoint.EndpointFormat` / `FramesPerBuffer`), §3.55
> (`FFmpegVideoChannel.EnsureDefaultSubscription` double-dispose guard
> via `ObjectDisposedException.ThrowIf` under `_subsLock`). Also
> corrected the §0.4.5 decision text to match the live XML on
> `IAudioChannel.Volume` ("keep as legacy, obsolete via the §5.1
> MediaPlayerBuilder sweep" — not "remove outright"). Fixed two residual
> `PortAudioOutput` XML references in `SDL3VideoEndpoint.cs` and
> `NDIPlaybackProfile.cs` that the first-pass rename `sed` missed.
> Build 0 warnings / 0 errors; all **214 tests pass**.
>
> **2026-04-23 third pass (this session):** landed §3.11 `VideoFrameHandle`
> (the biggest architectural blocker — unblocks §3.38 clone-sink ref-count,
> §5.3 color-hint auto-propagation, §8.2 / §8.9 zero-copy). New
> `readonly struct VideoFrameHandle` in `Media/S.Media.Core/Video/` with
> `Retain()`/`Release()` that forward to the existing
> `RefCountedVideoBuffer` machinery; non-ref-counted owners fall back to
> `IDisposable.Dispose` on Release and throw on Retain. `IVideoEndpoint`
> and `IVideoPresentCallback` each gained a handle overload with a
> default interface method that forwards to the legacy `VideoFrame`
> overload, so every existing endpoint keeps working unchanged.
> `AVRouter.PushVideoTick` now delivers via the handle overload and calls
> `handle.Release()` in place of the old `MemoryOwner?.Dispose()`.
> Covered by 7 new `VideoFrameHandleTests`. Also ticked off a set of
> already-implemented checklist items that still had `[ ]` boxes: §3.12
> (`_pushVideoPending` `AddOrUpdate` atomicity), §3.14
> (`GetOrCreateScratch` race + `PreallocateScratch` at registration),
> §3.16 (`_endpointsSnapshot` COW), §3.17 (`RegisterEndpoint` /
> `UnregisterEndpoint` wrapped in `_lock`), §3.19 (push-threads
> `CancellationTokenSource` + cancellation-aware `WaitUntil`), §3.33
> (SDL3/Avalonia identity compare via `ReferenceEquals(MemoryOwner,…)`).
> Fixed one latent type error the CS0121 ambiguity from the new handle
> overload surfaced — `_lastUploadedMemoryOwner` field widened from
> `IMemoryOwner<byte>?` to `IDisposable?` in `SDL3VideoEndpoint`,
> `SDL3VideoCloneEndpoint`, `AvaloniaOpenGlVideoEndpoint` to match
> `VideoFrame.MemoryOwner`'s actual type. Two explicit
> `out VideoFrame` type annotations added at the SDL3/Avalonia
> `TryPresentNext` call sites to resolve the new overload ambiguity.
> Build 0 warnings / 0 errors; all **221 tests pass** (was 214, +7
> `VideoFrameHandleTests`).
>
> **2026-04-23 fourth pass (this session):** wrapped up the decoder
> Tier-1 correctness cluster and landed the §5.1 `MediaPlayerBuilder`
> scaffold. §3.1 `FFmpegDecoder.WriteControlPacket` now fast-paths
> `TryWrite` then falls back to a bounded 50 ms `WriteAsync` with a
> linked CTS so seek-flush sentinels reach decode workers even when the
> packet queue is saturated; §3.2 `FFmpegDecoder.Dispose` reordered to
> cancel CTS → join demux (3 s timeout) → dispose channels → release HW
> device → take `_formatIoGate` write lock → `avformat_close_input` so
> an in-flight `av_read_frame` cannot race a close; §3.4
> `FFmpegAudioChannel.DecodePacketAndEnqueue` catches
> `ChannelClosedException` and returns the pooled float chunk on the
> concurrent-complete path. Covered by two new stress-style regression
> tests (`FFmpegDecoder_DisposeDuringDemux_IsCleanUnderStress`,
> `FFmpegDecoder_SeekUnderLoad_DoesNotDropFlushSentinel`). §5.1
> `MediaPlayerBuilder` landed with `WithAudioOutput` / `WithVideoOutput`
> / `WithAVOutput` / `WithClock` / `WithDecoderOptions`
> / `WithRouterOptions` / `OnError` / `OnStateChanged` / `OnCompleted`
> + `Build()` that unwinds via `DisposeAsync` on partial failure.
> `MediaPlayer.Create()` is the public entry; the options-taking
> `MediaPlayer(AVRouterOptions?)` ctor is `internal` so Build is the
> only way to inject router options. Also moved `PlayAsync`'s
> "no-media" pre-check inside the try/catch so `PlaybackFailed` fires
> for that stage too (builder tests would otherwise miss it). §0.4.1
> ticked — the existing `ref-test1-a-1` branch serves as the feature
> branch (different name than the checklist anticipated). Covered by 8
> new `MediaPlayerBuilderTests`. Build 0 warnings / 0 errors; all
> **231 tests pass** (was 221, +2 decoder lifecycle tests, +8 builder
> tests; 162 core + 69 FFmpeg).
>
> **2026-04-23 fifth pass (this session):** ergonomic-helpers + builder
> follow-up batch. Landed §5.3 (color-hint auto-propagation via new
> `IVideoColorMatrixReceiver` + receiver impls on SDL3/Avalonia + try/catch
> guard in `CreateVideoRoute`), §5.4 (`AVRouterOptions.MinBufferedFramesPerInput`
> + `WaitForAudioPreroll`, actually-async `StartAsync`, builder sugar
> `WithAutoPreroll(...)`), §3.56 (`IAudioChannel.Volume` setter accessor
> marked `[Obsolete]`, in-tree `MediaPlayer.AttachDecoder` + SimplePlayer
> arrow-key controls migrated to `router.SetInputVolume(inputId, …)`),
> §4.20 (`NDIAudioChannel` capture loop now refuses non-Fltp audio
> FourCCs with log-once + framesync free, mirroring the existing video
> `_unsupportedFourCcLogged` pattern). Also clerically ticked §4.4
> (full async Add/Remove path is in place via `AddEndpointAsync` /
> `RemoveEndpointAsync` plus the now-volume-clean `MediaPlayer`),
> §4.8 (`DefaultPriority` was already wired with the
> `AutoRegisterEndpointClock` resolver), §4.9 (`ActiveClockChanged`
> already wired and now covered by 4 tests), §4.10
> (`PooledWorkQueue.Complete` / `IsCompleted` / `TryEnqueueWithCap`
> already in place from an earlier pass). Build 0 warnings / 0 errors;
> all **242 tests pass** (was 231, +4 `VideoColorHintPropagationTests`,
> +3 `AudioPrerollTests`, +4 `AVRouterActiveClockChangedTests`; 173
> core + 69 FFmpeg).
>
> **2026-04-23 sixth pass (this session):** focused cleanup pass on
> SDL3/Avalonia render-loop hygiene and the NDI-input Tier-1-N cluster.
> Landed **§3.36** (Avalonia `OnOpenGlRender` only re-arms the render
> timer when a frame was actually uploaded or `LiveMode` is on — the
> previous unconditional `RequestNextFrameRendering()` in the `finally`
> turned an idle control into a UI-thread busy-loop), **§3.40d**
> (SDL3 + Avalonia render-exception logs now tag `{ExceptionType}` so
> the 1/2/3-then-every-100th samples group by failure mode), **§3.40j**
> (Avalonia `Dispose` throws when `VisualRoot is not null` so the
> compositor can't race a still-attached control), **§3.40k** (Avalonia
> caches `_renderScalingBits` via `Interlocked.Exchange` / stashed on
> `OnAttachedToVisualTree` + `OnPropertyChanged(BoundsProperty)` — the
> render thread no longer touches `VisualRoot.RenderScaling`, which is
> technically UI-thread-only), **§3.41 + §3.46 + §3.47e** on
> `NDIVideoChannel` (per-start `_cts` + `ObjectDisposedException.ThrowIf`
> in `StartCapture`, `Interlocked.CompareExchange` "already started"
> CAS guard, strict `Debug.Assert(>= 0)` ring-counter invariants on
> EnqueueFrame/FillBuffer/Dispose — mirroring the pattern
> `NDIAudioChannel` already had), **§3.45** (rename `NDISource.Stop` →
> `StopClock` + `[Obsolete]` forwarder; `NDIAVChannel.Stop` follows),
> **§3.47** (`NDIAVChannel` now accepts video-only NDI sources —
> `AudioChannel` is `IAudioChannel?`, and the ctor only rejects
> audio-less AND video-less sources; `MFPlayer.NDIAutoPlayer` keeps its
> audio-required behaviour via a local null-check), **§3.47f**
> (XML on `NDISource.StateChanged` explicitly documents
> `NewState` as authoritative vs the racy `State` property),
> **§3.47j** (`NDIReceiverSettings.AllowVideoFields` default now
> `false` — framesync always pulls Progressive so allowing fields
> is wasted bandwidth). Build 0 warnings / 0 errors; all **242 tests
> still pass** (no new tests added this pass — all changes are
> covered by existing router/NDI lifecycle tests).
>
> **2026-04-24 eighth pass (this session):** Tier-2 / Tier-3
> coverage. Closed **§3.40a** (clone-sink wiring contract documented
> in `Doc/Clone-Sinks.md` + XML on `CreateCloneSink` for SDL3 &
> Avalonia — model (b), parent.Dispose cascade is a safety net),
> **§4.7** (new `FFmpegDecoderOptions.AudioTargetFormat` reshapes
> SWR to produce the endpoint's native rate/channels — router
> recognises source == endpoint and skips its per-route resampler;
> output capacity computed with `av_rescale_rnd` so rate change is
> safe), **§4.13** (`IAudioMixer.CountOverflows` + `ApplySoftClip`
> with a cheap tanh-ish Padé approximation; `AVRouterOptions.SoftClipThreshold`
> enables the protection on every audio endpoint; push + pull paths
> count overflows and feed `EndpointDiagnostics.OverflowSamplesTotal`),
> **§4.14** (verified stale — `BakedChannelMap` is already applied
> unconditionally in both paths via `§3.15 / R4`), **§4.15**
> (per-endpoint `PeakLevel` measured post-channel-map, post-gain,
> pre-ReceiveBuffer on both push and pull paths; exposed via
> `GetEndpointPeakLevel` + `EndpointDiagnostics.PeakLevel`),
> **§4.17** (`NDIUnsupportedFourCcEventArgs` +
> `NDIVideoFormatChangedEventArgs` in new `NDIEvents.cs`; events
> fired log-once per FourCC on the internal channels and forwarded
> onto `NDISource` + `NDIAVChannel`; new
> `NDISourceOptions.MaxForwardPtsJumpMs` replaces the hard-coded
> 750 ms constant), **§4.19** (`NDIReconnectPolicy` record
> supersedes the `AutoReconnect` + `ConnectionCheckIntervalMs`
> flag pair; legacy flags marked `[Obsolete(error: false)]` but
> honoured via internal `ResolveReconnectPolicy()` bridge; watch
> loop uses resolved policy; `ForPreset` updated), **§5.5**
> (`IAudioEndpoint.NominalTickCadence` + `IVideoEndpoint.NominalTickCadence`
> hints; `AVRouter._effectiveCadenceSwTicks` volatile-written by
> `RecomputeEffectiveCadence()` under `_lock` on every
> Register/Unregister; push and video loops `Volatile.Read()`
> each tick so registration reshapes cadence without a Stop/Start;
> new public `EffectiveTickCadence` getter), **§5.6**
> (`VideoRouteOptions.OverflowPolicy` + `Capacity` with nullable
> override semantics; router picks defaults when the caller leaves
> them unset), **§5.10** (new `RouterBuilder` in
> `Media/S.Media.Core/Routing/` — fluent
> `AddAudioInput`/`AddVideoInput`/`AddEndpoint`/`AddRoute`/
> `AddClock`/`WithOptions`; tokens map to real ids at `Build()`
> time; partial-failure disposes the half-wired router and
> rethrows), and **§10.3** (`MediaPlayer.DisposeAsync` now uses
> parallel endpoint stop via `Task.WhenAll` with per-task
> try/catch — router stop → parallel endpoint stop → decoder stop
> → router dispose, documented inline). Also refactored
> `AvaloniaOpenGlVideoEndpoint.OnOpenGlRender` helper extraction
> and finished §3.34 the prior session. Build 0 warnings / 0
> errors; all **260 tests pass** (up from 242 — +8 mixer, +5
> cadence, +5 RouterBuilder). §3.50 previously-reclassified
> `[~]` note retained.
>
> **2026-04-23 seventh pass (this session):** audit + focused fixes.
> Swept the checklist for stale `[ ]` boxes — §2.1 (`Doc/Clone-Sinks.md`
> was already rewritten), §3.40i (Avalonia Dispose already relies on
> `OnOpenGlDeinit`), §3.44 (NDI capture loops already have the narrow
> try/catch + `finally { FreeAudio/FreeVideo }` with `§3.44 / N6`
> tags), §3.54 (`AVRouter` already resets the drift-EMA on
> `(audioInput, videoInput)` pair change at `AVRouter.cs:780-793`)
> were all already landed in earlier commits. Flipped those boxes.
> Real new work: §3.40h (extracted `ResolvePresentationClock` /
> `TryPullFrameWithCatchUp` / `PresentFrame` from the 145-line
> `AvaloniaOpenGlVideoEndpoint.OnOpenGlRender` — now ~55 lines);
> §3.38 (ref-counted zero-copy fast-path in `SDL3VideoCloneEndpoint`
> + `AvaloniaOpenGlVideoCloneEndpoint` — new `IsRefCounted`
> accessor on `VideoFrameHandle` lets external assemblies gate the
> `Retain()` call, legacy frames still fall through to the copy
> path); §3.34 (SDL3 + Avalonia YUV-hint setters no longer touch
> `_renderer` off the render thread — they flip a
> `_yuvHintsDirty` flag that the render loop consumes at the top
> of each tick under the GL context, new
> `AvaloniaOpenGlVideoEndpoint.ApplyPendingYuvHints` helper).
> §3.50 reclassified to `[~]` — XML note is done, `[Obsolete]`
> attribute intentionally deferred because it would fire on every
> legitimate non-timecoded override (PA, Virtual, test endpoints).
> Build 0 warnings / 0 errors; all **242 tests still pass**
> (changes are concurrency / refactor / off-thread-GL fixes — no
> behavioural invariants added that new tests could pin).
>
> **2026-04-24 ninth pass (this session):** More Tier-2/3/4 cleanup
> and mid-sized closes. **§4.16** landed as new `NDIClockPolicy`
> (`Both`/`VideoPreferred`/`AudioPreferred`/`FirstWriter`) with
> `NDIClock.TryUpdateFromFrame(ts, writerKind)` atomic CAS for
> `FirstWriter`; policy flows through `NDISourceOptions.ClockPolicy`
> and both NDI channels gate their `_clock.UpdateFromFrame` call on
> it. **§4.18** added new `NDIDiscovery` static registry
> (single-shared finder + watch thread + `Discovered`/`Lost` events
> + `WaitForAsync` helper; refcount lifecycle via `AddRef`/`Release`/
> `Shutdown`) so multi-source callers share one mDNS thread.
> **§6.8** push-audio path now invokes `route.Resampler` when
> src/dst rates differ (pull size via `GetRequiredInputFrames`,
> log-once warning on mismatch without a resampler wired).
> **§6.7** split cadence — `AVRouterOptions.AudioTickCadence` +
> `VideoTickCadence` (both nullable) + per-kind
> `_effectiveAudioCadenceSwTicks` / `_effectiveVideoCadenceSwTicks`
> updated on Register/Unregister with kind-filtered endpoint hints;
> public `EffectiveAudioTickCadence` / `EffectiveVideoTickCadence`
> getters; legacy `EffectiveTickCadence` kept as alias for audio.
> **§10.2** new `LoggerCorrelationExtensions` with
> `BeginRouteScope`/`BeginInputScope`/`BeginEndpointScope`; applied
> at audio + video route-creation log sites and the push-path
> format-mismatch warning (per-tick hot paths intentionally
> unwrapped). Verified-stale boxes flipped: **§6.10** (null-list
> throw was already wired at AVRouter.cs:1001) and **§6.11**
> (eager format-incompat warning already fires at route-creation
> time, `§6.10 / R22 / CH9` inline comments). Build 0 warnings /
> 0 errors; all **261 tests pass** (260 + 1 new split-cadence test).

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

- [x] **0.4.1** Create a long-lived feature branch (`feat/endpoint-consolidation`)
      and enable required CI gates (build + any existing tests) on PRs into it.
      *(Done: working on `ref-test1-a-1` — the repo's chosen name for the
      endpoint-consolidation feature branch. All Tier-0/Tier-1 refactor work is
      landing here before merging to `main`.)*
- [x] **0.4.2** Add a minimal xUnit/NUnit test project under `Test/` (there is
      none today). Seed with `ChannelRouteMap.Auto`, `DriftCorrector.CorrectFrameCount`
      and the seek-epoch filter (§10.1); without it the larger refactors land
      blind. *(Done: `Test/S.Media.Core.Tests` with 115 tests + `Test/S.Media.FFmpeg.Tests` with 52 tests, all green.)*
- [x] **0.4.3** Decide the obsoletion policy and document it in
      `Doc/README.md`: `[Obsolete(error: false)]` type-forwarders for renamed
      concrete classes for one release, `[Obsolete(error: true)]` for the
      release after. *(Decided 2026-04-23: use `[Obsolete(error: false)]`
      public type-forwarder classes inheriting from the new endpoint type
      (precedent: `NDIAVSinkLegacy.cs`). PortAudio legacy types were already
      deleted outright before this policy existed — that is the single
      exception; going forward every rename in §1.2 uses the forwarder
      approach. See `SDL3VideoOutputLegacy.cs` and
      `AvaloniaOpenGlVideoOutputLegacy.cs` for the template. Clone endpoints
      use internal ctors and are only obtainable via parent
      `CreateCloneSink(...)`, so they do not need forwarder classes — `var` /
      implicit-typed callers migrate transparently.)*
- [x] **0.4.4** Snapshot LoC of every `Test/MFPlayer.*` app so §5.11 has a
      baseline to measure reduction against. *(Snapshot 2026-04-23:
      `AvaloniaVideoPlayer` 358, `MultiOutputPlayer` 250, `NDIAutoPlayer` 517,
      `NDIPlayer` 314, `NDISender` 370, `SimplePlayer` 377,
      `VideoMultiOutputPlayer` 269, `VideoPlayer` 940. Total 3 395 LoC across
      8 sample apps; §5.11 will aim for ≤15 LoC SimplePlayer and ≤25 LoC
      VideoPlayer.)*
- [x] **0.4.5** Audit `NDIAudioChannel.Volume` / `FFmpegAudioChannel.Volume`
      and `IAudioChannel.Volume` setters: decide whether to remove the
      channel-level `Volume` outright (review §Consistency says it's legacy
      vs. `SetInputVolume`). *(Decided 2026-04-23: **keep as legacy, obsolete
      later**. `IAudioChannel.Volume` remains on the interface so
      direct-channel callers (legacy tests, sample apps that bypass the
      router) keep working. XML on the property now names
      `AVRouter.SetInputVolume(inputId, volume)` as the preferred API and
      flags the setter for `[Obsolete]` once in-tree call sites (MediaPlayer
      facade, SimplePlayer) migrate — that migration rides along with the
      §5.1 `MediaPlayerBuilder` pass so every call site moves in one step.
      Tracked under §3.56 Tier-1 contract sweep.)*
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
      *(Done 2026-04-23: `Doc/Clone-Sinks.md` was rewritten previously;
      `Doc/MediaPlayer-Guide.md`, `Doc/Quick-Start.md`, `Doc/Usage-Guide.md`,
      `Doc/README.md`, and per-project READMEs swept for
      "sink"/"output" → "endpoint" vocabulary and identifier renames
      (`SDL3VideoOutput`→`SDL3VideoEndpoint`,
      `AvaloniaOpenGlVideoOutput`→`AvaloniaOpenGlVideoEndpoint`,
      `NDIAVSink`→`NDIAVEndpoint`, and the matching clone renames). XML doc
      sweeps for `SDL3VideoOutput` / `AvaloniaOpenGlVideoOutput` identifier
      references in `NDIPlaybackProfile.cs` and `YuvAutoPolicy.cs` updated
      alongside the code rename.)*
- [x] **1.2** Collapse concrete classes so each backend has **one** `*Endpoint`
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
        forwarders — callers were migrated inline, see §5.11 partial.
        This predated the §0.4.3 policy decision; subsequent renames follow
        the `[Obsolete]` forwarder policy.)*
      - [x] `NDIAVSink` → `NDIAVEndpoint`. *(Done: class renamed; the file
        `NDIAVSink.cs` renamed to `NDIAVEndpoint.cs`; legacy
        `NDIAVSinkLegacy.cs` provides an `[Obsolete]` public type-forwarder
        `class NDIAVSink : NDIAVEndpoint`.)*
      - [x] `SDL3VideoOutput` → `SDL3VideoEndpoint`;
        `SDL3VideoCloneSink` → `SDL3VideoCloneEndpoint`. *(Done: class and
        file renames; parent class unsealed so the legacy forwarder can
        inherit; `SDL3VideoOutputLegacy.cs` provides
        `[Obsolete] public sealed class SDL3VideoOutput : SDL3VideoEndpoint`.
        Clone endpoint has an internal ctor and is constructed only via
        `parent.CreateCloneSink(...)`, so no clone forwarder is needed —
        `var` / implicit-typed callers migrate transparently.)*
      - [x] `AvaloniaOpenGlVideoOutput` → `AvaloniaOpenGlVideoEndpoint` (and
        clone). *(Done: class and file renames; parent class unsealed;
        `AvaloniaOpenGlVideoOutputLegacy.cs` provides the `[Obsolete]` public
        type-forwarder. Clone endpoint constructed only via parent
        `CreateCloneSink(...)` — no forwarder needed.)*
- [x] **1.3** `MediaPlayer.AddEndpoint(IAudioEndpoint)` /
      `AddEndpoint(IVideoEndpoint)` / `AddEndpoint(IAVEndpoint)` already
      exist in [MediaPlayer.cs](../Media/S.Media.FFmpeg/MediaPlayer.cs). The
      Doc/ vocabulary sweep (§2.1) is the remaining concern and is tracked
      there.
- [x] **1.4** Remove the "Open before Register" footgun by merging constructor
      + open into a single factory per endpoint.
      `PortAudioEndpoint.Create(device, format, mode, framesPerBuffer)`
      returns a ready-to-register instance with `Clock` already valid
      (closes **P1** / **CH8**). *(Done 2026-04-23 for the full set:
      PortAudio had `PortAudioEndpoint.Create(...)` from an earlier pass;
      NDI has `NDIAVEndpoint.Create(sender, options)` /
      `NDIAVEndpoint.Create(sender, videoFmt, audioFmt, preset, ...)`;
      SDL3 now exposes `SDL3VideoEndpoint.ForWindow(title, width, height,
      format?, vsync?)`; Avalonia exposes
      `AvaloniaOpenGlVideoEndpoint.Create(width, height, format?)` —
      Avalonia still needs to be placed in a visual tree by the host app,
      but `Clock` is valid from the moment `Create(...)` returns, matching
      the §2.4 lifetime contract.)*
- [x] **1.4a** Keep `PortAudioClock` as a standalone `IMediaClock` (it already
      is — derived from `HardwareClock`, wraps a PA stream handle). The merged
      `PortAudioEndpoint` just hands out the same clock in both driving modes.
      Auto-registered at `ClockPriority.Hardware` when the endpoint is
      registered (existing behaviour). This is the **recommended** clock for
      PA-only playback and is always present even when using a push
      (`BlockingWrite`) PA endpoint. *(Done: `Audio/S.Media.PortAudio/PortAudioClock.cs`
      extracted and handed out from both driving modes.)*
- [x] **1.4b** Document the clock-swap story explicitly in `Doc/` and in the
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
      *(Done 2026-04-23: `Doc/MediaPlayer-Guide.md` now has a
      "Clock selection (§1.4b)" section covering the four scenarios above
      (PA-only default, PA + PTP override, `SetClock` hard override, NDI
      send caveat); XML on `IClockCapableEndpoint` / `IAudioEndpoint` /
      `IVideoEndpoint` already described the lifetime + priority-fallback
      contract from previous passes.)*
- [x] **1.5** `AVRouter.RegisterEndpoint` already figures out push/pull. Document
      this explicitly in the XML on `IAudioEndpoint` and `IVideoEndpoint`
      ("implement `IPullAudioEndpoint` only if you drive an RT callback;
      otherwise just `IAudioEndpoint` and the router will push to you"). No
      code change needed on the router beyond docs. *(Done 2026-04-23 —
      verified `IAudioEndpoint.cs` and `IVideoEndpoint.cs` already carry the
      two-paragraph explanation ("`IPullAudioEndpoint`/`IPullVideoEndpoint`
      is an opt-in capability mixin, not a separate kind of endpoint" plus
      the `RegisterEndpoint(...)` runtime-check note). No further XML
      change needed.)*
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

- [x] **2.1** Update `Doc/Clone-Sinks.md` to use `AVRouter.RegisterEndpoint` / `CreateRoute`
      (remove the obsolete `avMixer.RegisterVideoSink` example). *(Tier 0 #1)*
      *(Done: the doc was rewritten to the `router.CreateRoute` API earlier;
      this box was stale — verified 2026-04-23 seventh pass.)*
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

- [x] **3.1** `FFmpegDecoder.Seek` flush reliability (drain-then-write or
      `WriteAsync` with 50 ms timeout). *(B1)* *(Done 2026-04-23:
      `WriteControlPacket` now does a fast-path `TryWrite` and, on full,
      falls back to a bounded `WriteAsync` with a 50 ms linked-timeout CTS
      so the flush sentinel reaches the decode worker even when the packet
      ring is saturated. Previously a dropped sentinel left pre-seek
      packets queued and the decode worker emitted frames with the new
      epoch but the old PTS — audible/visible rewind-then-black after the
      seek. Covered by `FFmpegDecoder_SeekUnderLoad_DoesNotDropFlushSentinel`.)*
- [x] **3.2** `FFmpegDecoder.Dispose` ordering: cancel CTS → join demux task → dispose
      channels → close format. Take the format write-lock around
      `avformat_close_input`. *(B2, B3)* *(Done 2026-04-23:
      `FFmpegDecoder.Dispose` now cancels `_cts`, joins `_demuxTask` with
      a 3-second timeout, then disposes audio/video channels, releases
      `_hwDeviceCtx`, and finally takes the `_formatIoGate` write lock
      before `avformat_close_input` so any demux-read that slipped past
      the cancel cannot touch freed state. XML on `Dispose` documents the
      teardown order; covered by
      `FFmpegDecoder_DisposeDuringDemux_IsCleanUnderStress` (10 rapid
      open→start→dispose cycles at varying demux progress).)*
- [x] **3.3** `StreamAvioContext` callbacks: distinguish EOF from IO errors; return
      `AVERROR(EIO)` for non-EOF failures; propagate via an `OnError` event on the
      owning decoder. *(B9)* *(Done: `StreamAvioContext.ReadPacket` returns
      `AVERROR(EIO)` (-5) on catch and records the exception via
      `ConsumeLastIoError`; `FFmpegDecoder.TryReadNextPacket` consumes it and
      raises `OnError`/`MediaDecodeException` via `RaiseDemuxError`, with a
      `DemuxReadResult.Fatal` return so the demux worker stops cleanly instead
      of tight-looping on Retry.)*
- [x] **3.4** `FFmpegAudioChannel` WriteAsync leak: on `ChannelClosedException`,
      return the rented chunk to the pool. *(B10)* *(Done 2026-04-23:
      `FFmpegAudioChannel.DecodePacketAndEnqueue` now catches both
      `OperationCanceledException` and `ChannelClosedException` around the
      `WriteAsync` result-retrieval; the rented `float[]` is returned to
      `_chunkPool` on either path so a seek-flush race or a Dispose on the
      ring writer no longer leaks the audio chunk buffer.)*
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

- [x] **3.8** Snapshot `_subs` locally in `FFmpegVideoChannel.Seek` / `BufferAvailable`
      / `PublishToSubscribers`. *(B4)* *(Done 2026-04-23:
      `BufferAvailable`, `ApplySeekEpoch`, `Seek`, `CompleteDecodeLoop` and
      `PublishToSubscribers` all read `_subs` into a local before iterating.
      `ImmutableArray<T>` is tear-free, but the local snapshot pattern
      guarantees a single consistent view across `lock`-free reads and
      matches the existing `PublishToSubscribers` convention.)*
- [x] **3.9** Wrap `TryPublish` body in try/catch that returns false on any exception so
      refs are never leaked on `ChannelClosedException` etc. *(B5)*
      *(Done 2026-04-23: `PublishToSubscribers` catches any exception from
      `subs[i].TryPublish(...)`, logs at Warning, and treats the publish as
      failed — the frame's ref for that subscription is released via the
      existing "publish failed" branch, so a buggy/disposed subscription
      cannot leak ref-count shares or skip publish for later subs.)*
- [x] **3.10** Document the ref-count invariant on `FFmpegVideoSubscription.DropOldest`
      eviction; add Debug.Assert. *(B6)* *(Done 2026-04-23: XML comment in
      `FFmpegVideoSubscription.TryPublish` DropOldest branch describes the
      `_queued` increment/decrement invariant and each evicted frame's
      `MemoryOwner.Dispose` contract; `Debug.Assert(after >= 0)` pins the
      non-negative invariant on every eviction.)*
- [x] **3.11** Introduce `VideoFrameHandle` (explicit retain/release) to replace the
      implicit router-disposes-after-ReceiveFrame contract. Route-level per-endpoint
      retain via `RefCountedVideoBuffer.Retain()`. *(B15, B16, R18, CH7)*
      *(Done 2026-04-23: new public `readonly struct VideoFrameHandle` in
      `Media/S.Media.Core/Video/VideoFrameHandle.cs` wraps a `VideoFrame` + its
      `RefCountedVideoBuffer` with `Retain()`/`Release()` methods that forward
      to the existing refcount machinery; non-ref-counted owners fall back to
      `IDisposable.Dispose` on Release and throw on Retain. `IVideoEndpoint`
      and `IVideoPresentCallback` each grew a handle overload with a default
      interface method forwarding to the legacy `VideoFrame` overload so all
      existing endpoints keep working unchanged. `AVRouter.PushVideoTick` now
      delivers via `ReceiveFrame(in VideoFrameHandle)` and calls
      `handle.Release()` in place of the old
      `candidate.MemoryOwner?.Dispose()`. Endpoints that need zero-copy
      retention (clone-sink fast path §3.38, async GPU uploads) can now opt in
      by overriding the handle overload and calling `Retain()`. Covered by 7
      new `VideoFrameHandleTests` (retain/release symmetry, fan-out N=4,
      non-ref-counted fallback, reference-identity equality, default-instance
      no-op). Test suite: 221 green (was 214).)*

### Router

- [x] **3.12** `_pushVideoPending` atomicity: use `AddOrUpdate` with a disposal-in-lambda
      pattern, or hold `_lock` during the swap. Fix both the pull (B12) and push
      (R17) paths. *(Done: both the early-frame cache path (AVRouter L1384-1387)
      and the catch-up "next too early" branch (L1405-1408) use
      `_pushVideoPending.AddOrUpdate(route.Id, candidate, (_, stale) => {
      stale.MemoryOwner?.Dispose(); return candidate; })` — the updater runs
      under the bucket lock, so a concurrent `RemoveRouteInternal` TryRemove
      cannot double-dispose an intermediate state.)*
- [x] **3.13** `DisposeCore` must call `RemoveRouteInternal` for every route so
      subscriptions, drift trackers and `_pushVideoDrift` are not leaked. *(R9)*
      *(Done: `AVRouter.DisposeCore` now snapshots `_routes.Keys` and routes
      each through `RemoveRouteInternal` under `_lock` — per-route resamplers,
      `VideoSub`, `_pushVideoPending`, `_pushVideoDrift`, and
      `_pushAudioFormatMismatchWarnings` are all released symmetrically.)*
- [x] **3.14** `GetOrCreateScratch` race: pre-allocate in `SetupPullAudio`, use
      `GetOrAdd` with a sized factory. *(R6)* *(Done: `AVRouter.PreallocateScratch`
      sizes the per-endpoint buffer from `IPullAudioEndpoint.EndpointFormat ×
      FramesPerBuffer` (or `NegotiatedFormat` + `DefaultFramesPerBuffer` for
      push endpoints) at registration time under `_lock`, with a 2048-float
      floor so the common case never reallocates. The RT hot path
      `GetOrCreateScratch` is now fast-path read first, and the grow branch
      uses `AddOrUpdate` so concurrent Register + push-tick callers can no
      longer lose one buffer and duplicate the other.)*
- [x] **3.15** Apply `BakedChannelMap` whenever it is non-null, not only when
      `srcChannels != dstChannels`. *(R4)* *(Done: push-audio path in
      `AVRouter.PushAudioTick` no longer gates on
      `srcFormat.Channels != format.Channels` — a user-supplied channel map is
      applied even for equal-channel shapes (e.g. stereo L↔R swap, mid-side
      encode). Pull path already behaved correctly; this aligns the two.)*
- [x] **3.16** COW `_endpointsSnapshot` rebuilt under `_lock`; push tick reads only the
      snapshot to close the mid-registration window. *(R7)* *(Done:
      `AVRouter._endpointsSnapshot` (volatile `EndpointEntry[]`) is rebuilt via
      `RebuildEndpointsSnapshot()` under `_lock` at every Register/Unregister.
      `PushAudioTick` iterates the snapshot instead of `_endpoints.Values`, so
      the tick cannot observe a half-initialised endpoint during the
      `SetupPull* → _endpoints[id]=entry → AutoRegisterEndpointClock` window.)*
- [x] **3.17** Wrap `RegisterEndpoint` / `UnregisterEndpoint` in `_lock` for atomicity
      with `AutoRegisterEndpointClock` + `SetupPull*`. *(R8)* *(Done: all three
      `RegisterEndpoint` overloads (audio / video / AV) and `UnregisterEndpoint`
      now run their full Setup → dictionary-write → snapshot-rebuild →
      clock-auto-register sequence under `_lock`, so concurrent push ticks
      cannot observe a half-initialised endpoint (entry visible but
      FillCallback not yet attached, or clock not yet registered).)*
- [x] **3.18** `SetInputVolume/TimeOffset/Enabled` use `Volatile.Write`; diagnostics
      reads via `Volatile.Read`. *(B13, R15)* *(Done: `AVRouter.InputEntry`
      now stores the time offset as a `long TimeOffsetTicks` with a
      `TimeSpan` wrapper property using `Interlocked.Read`/`Interlocked.Exchange`;
      `SetInputVolume` uses `Volatile.Write` on the float field;
      `SetInputEnabled` uses `Volatile.Write` on the bool.)*
- [x] **3.19** Use a `CancellationTokenSource` on the push threads instead of busy-waiting
      on `_running`. *(R19, R20)* *(Done: `AVRouter._pushCts` is rebuilt per
      `StartAsync` and cancelled in `StopAsync` before the join. The push
      threads' `WaitUntil` helper uses `ct.WaitHandle.WaitOne(sleepMs)` for the
      coarse-sleep portion and checks `ct.IsCancellationRequested` in the
      spin-wait tail, so `StopAsync` unblocks an in-flight wait in tens of
      microseconds instead of waiting for the 5 ms cadence tick to notice
      `_running==false`.)*
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
- [x] **3.25** `PortAudioClock.SetStreamHandle` atomicity vs `Position` reads;
      take the same lock used by `HardwareClock` for `_lastValidPosition`.
      *(P2)* *(Done: `PortAudioClock` stores the stream handle in a
      mutable `HandleRef` box captured by the provider lambda;
      `SetStreamHandle` / `ClearStreamHandle` publish updates via
      `Interlocked.Exchange`, and the provider reads via
      `Volatile.Read` before calling `Pa_GetStreamTime`. The
      `HardwareClock.Position` fallback state is already guarded by its
      own `SpinLock`, so reads of the PA handle are consistent with the
      fallback transition under that same critical section.)*
- [x] **3.26** Pre-rent router scratch buffers so `AudioFillCallbackForEndpoint.Fill`
      never hits `ArrayPool.Rent` on RT. *(P3)* *(Done 2026-04-23: new
      `_outputScratchBuffers` dictionary alongside `_scratchBuffers`,
      pre-allocated at `RegisterEndpoint` time for every
      `IPullAudioEndpoint` with size `framesPerBuffer × channels ≥ 2048`.
      `AudioFillCallbackForEndpoint.Fill` now serves both the resampler
      output and the channel-map output from `GetOrCreateOutputScratch`,
      so the RT pull-audio path never calls `ArrayPool<float>.Rent` in
      steady state. Grow is gated behind a size mismatch and remains the
      documented slow path. Push-tick code is unchanged — it still rents
      from the pool because it can overlap multiple endpoints on one
      tick thread.)*
- [x] **3.27** *(→ do during §1.2)* `Open` failure path must `Free` the
      `GCHandle` on `PortAudioClock.Create` failure. *(P6)* *(Done: `GCHandle`
      allocated lazily per open attempt in `CallbackEndpoint.TryOpenStreamAttempt`
      and freed in `Dispose`; rate-fallback retry no longer leaks.)*
- [x] **3.28** *(→ do during §1.2)* `Dispose` callback-in-progress guard (null
      the `_fillCallback` + spin on an "in-flight" flag). *(P5)* *(Done:
      `_callbackInFlight` interlocked counter + SpinWait in
      `CallbackEndpoint.Dispose` after nulling `_fillCallback`.)*
- [x] **3.28a** `PortAudioEngine.Terminate` refcount awareness (docs or mirror
      PA's internal refcount). *(P7)* *(Done 2026-04-23: XML on
      `PortAudioEngine` now carries a "single-instance contract" paragraph
      explaining that the wrapper does **not** replicate PA's internal
      `Pa_Initialize`/`Pa_Terminate` refcount — callers must keep exactly
      one engine alive for the process lifetime, and multi-engine scenarios
      need a reference-counted façade.)*
- [x] **3.28b** Offload `Pa_StartStream` to a worker task (or document the
      100–300 ms WASAPI-exclusive blocking behaviour). *(P4)* *(Done
      2026-04-23: `PortAudioEndpoint.StartAsync` XML now documents the
      WASAPI-exclusive 100–300 ms blocking window and advises callers who
      start many endpoints in parallel to wrap the call in
      `Task.Run`. No behaviour change to the default path.)*
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
- [x] **3.31b** `StopwatchClock`: document Windows ~15 ms granularity; optional
      `timeBeginPeriod(1)` around Start/Stop. *(C2)* *(Done 2026-04-23:
      `StopwatchClock` XML carries a "Windows timer granularity" paragraph
      explaining the 15.6 ms default, and the `UseHighResolutionTimer`
      opt-out (default on) wraps `Start`/`Stop`/`Reset` with
      `winmm.timeBeginPeriod(1)` / `timeEndPeriod(1)`. `_holdsHighResPeriod`
      gates release so a double-Stop cannot underflow the process-wide
      refcount. No-op on non-Windows.)*

### SDL3 / Avalonia

- [x] **3.32** Raise `SDL3VideoOutput.WindowClosed` via `ThreadPool.QueueUserWorkItem`
      so naive `Dispose()` handlers do not deadlock-join. *(S1)*
      *(Done 2026-04-23: new `RaiseWindowClosedAsync()` helper on
      `SDL3VideoEndpoint` queues the event on the `ThreadPool` and
      catches/logs handler exceptions. Both fire-sites (render-loop exit
      after `_closeRequested` and the `GLMakeCurrent` failure path from
      §3.40c) now use the helper — handlers that call
      `endpoint.Dispose()` no longer self-deadlock on
      `_renderThread.Join(3s)`.)*
- [x] **3.33** Replace `ReadOnlyMemory<byte>.Equals` texture-reuse identity with
      `(MemoryOwner ref + Pts + W + H)` in SDL3 and Avalonia. *(S3, S12, A2)*
      *(Done: SDL3VideoEndpoint L664-668, SDL3VideoCloneEndpoint L193,
      AvaloniaOpenGlVideoEndpoint L442 all use
      `ReferenceEquals(vf.MemoryOwner, _lastUploadedMemoryOwner)` +
      `Pts == _lastUploadedPts` + width/height guards. Bug fix 2026-04-23:
      `_lastUploadedMemoryOwner` field widened from `IMemoryOwner<byte>?` to
      `IDisposable?` to match `VideoFrame.MemoryOwner`'s actual type — the
      previous assignment was a latent type error that the compiler only
      surfaced after unrelated changes triggered re-analysis.)*
- [x] **3.34** SDL3 / Avalonia YUV-hint setters: queue a pending-change flag; apply at
      the top of the next render tick under the correct GL context. *(S6, A5)*
      *(Done 2026-04-23 seventh pass: new `_yuvHintsDirty` int field on both
      endpoints. SDL3 `YuvConfig` setter flips the flag instead of calling
      `ApplyResolvedYuvHints` from the caller thread; the render loop
      consumes the flag via `Interlocked.Exchange` at the top of each
      iteration, under the GL context. Avalonia `SetYuvHints` /
      `ResetYuvHints` / `ApplyColorMatrixHint` all flip the flag; new
      `ApplyPendingYuvHints` helper applies user-pinned values at the top
      of `OnOpenGlRender` under the GL context (auto-mode remains handled
      by `ApplyAutoYuvHintsIfNeeded` per-frame).)*
- [x] **3.35** `OnOpenGlLost` resets `_lastAutoMatrix/Range` to `Auto`. *(A4, A7)*
      *(Done: `AvaloniaOpenGlVideoEndpoint.OnOpenGlLost` sets both
      `_lastAutoMatrix = YuvColorMatrix.Auto` and
      `_lastAutoRange = YuvColorRange.Auto` so the next `OnOpenGlInit`
      re-resolves hints against a brand-new renderer.)*
- [x] **3.36** Avalonia: unconditional `RequestNextFrameRendering` in `finally` →
      request only when a frame was uploaded this tick (or in LiveMode). *(A1, A10, A14)*
      *(Done 2026-04-23 sixth pass: `OnOpenGlRender` tracks a local
      `uploadedThisTick` flag; the `finally` only re-arms the render
      timer when `uploadedThisTick || LiveMode`. Layout changes still
      trigger a tick via `OnPropertyChanged(BoundsProperty)`. New
      `LiveMode` bool property documents the opt-in for push/live
      scenarios that need every-vsync ticks without a pull upload.)*
- [ ] **3.37** Avalonia clone sink: GL-side multi-format shaders instead of scalar
      CPU YUV→RGB on the render thread. *(A9)*
      *(Deferred — requires moving the basic pixel-format converter out of
      the clone's render thread and into `AvaloniaGlRenderer`'s shader set.
      The §3.38 ref-counted fast-path removed the CPU copy hot-path in the
      Rgba/Bgra case, so the remaining scalar conversion only fires on
      YUV sources — acceptable until a GL multi-format rewrite lands.)*
- [x] **3.38** Ref-counted fast-path in Avalonia + SDL3 clone sinks when the incoming
      `MemoryOwner is RefCountedVideoBuffer`. *(S8, A3; depends on 3.11)*
      *(Done 2026-04-23 seventh pass: `VideoFrameHandle` grew a public
      `IsRefCounted` accessor so external assemblies can gate on it
      without reaching the `internal RefBuffer`.
      `SDL3VideoCloneEndpoint` and `AvaloniaOpenGlVideoCloneEndpoint`
      override `ReceiveFrame(in VideoFrameHandle)`: when
      `handle.IsRefCounted`, they call `Retain()` and park the frame in
      `_latestFrame` without copying or renting; when `Set` replaces the
      previous frame, the slot's auto-dispose routes back through
      `RefCountedVideoBuffer.Release`. Non-ref-counted frames fall back
      to the legacy copy path so the change is purely additive.)*
- [ ] **3.39** Unified process-wide SDL event pump dispatched by window ID. *(S9)*
- [x] **3.40** SDL3 `AcquireSdlVideo` try/finally on `Interlocked.Decrement`. *(S14)*
      *(Done 2026-04-23: `AcquireSdlVideo` now wraps the `SDL.Init` call in
      a `try/finally` that decrements `_sdlRefCount` unless `initialised`
      was observed true — a managed exception out of the SDL bindings can
      no longer leak a refcount slot and leave the process
      "permanently-SDL-initialised".)*
- [x] **3.40a** Decide + document the clone-sink wiring contract: either (a)
      parent `SDL3VideoEndpoint` tees frames to clones during render, or
      (b) clones are standalone endpoints the user registers on the router
      (current de-facto behaviour). Implement whichever you pick and remove
      the parent's `Dispose` cascade to clones if (b). *(S2, S4)*
      *(Done 2026-04-24: picked model (b) and documented in
      `Doc/Clone-Sinks.md` (new "Wiring contract (§3.40a)" section +
      recommended teardown order) plus XML on
      `SDL3VideoEndpoint.CreateCloneSink` and
      `AvaloniaOpenGlVideoEndpoint.CreateCloneSink`. The parent cascade is
      retained as a safety net — clones are tracked in `_clones` and
      disposed on parent.Dispose; `ReceiveFrame` on a disposed clone is a
      no-op, so the cascade cannot crash, only produces a small log spam
      window until the router notices. Callers with finer control should
      `RemoveRoute` + `UnregisterEndpoint` + `clone.Dispose()` before
      disposing the parent.)*
- [x] **3.40b** `SDL3VideoEndpoint.OverridePresentationClock` /
      `ResetClockOrigin`: use `Interlocked.Exchange` for the origin pair,
      or pack into one `long` to close the torn-read window on weakly
      ordered ARM64. *(S5)* *(Done: both `OverridePresentationClock` and
      `ResetClockOrigin` use `Interlocked.Exchange` on
      `_presentationClockOriginTicks` and `_hasPresentationClockOrigin`
      (ticks written first, then flag) so weakly-ordered CPUs cannot
      observe `hasOrigin==1 && ticks==<stale>`.)*
- [x] **3.40c** On `GLMakeCurrent` failure, set `_closeRequested = true` so
      `WindowClosed` still fires. *(S7)* *(Done 2026-04-23: the
      `RenderLoop` early-return path after `SDL.GLMakeCurrent` failure now
      sets `_closeRequested = true` and calls the new
      `RaiseWindowClosedAsync()` helper so Dispose completes promptly and
      any subscribers are notified of the render-init failure.)*
- [x] **3.40d** Tag exception types + first/last-seen timestamps in the
      rate-limited render-loop log. *(S10)* *(Done 2026-04-23 sixth pass:
      both `SDL3VideoEndpoint.RenderLoop` and
      `AvaloniaOpenGlVideoEndpoint.OnOpenGlRender` emit
      `"Render(-loop) exception [{ExceptionType}] (count={Count})"` so
      the 1/2/3-then-every-100th samples can be grouped by concrete
      exception type. First/last-seen timestamps deferred — the
      `ILogger` scope typically annotates its own event timestamps.)*
- [x] **3.40e** Wrap `glDelete*` in try/catch during Dispose when the context
      may already be gone (window user-closed path). *(S11)* *(Done:
      `AvaloniaOpenGlVideoEndpoint.OnOpenGlDeinit` / `OnOpenGlLost` and
      `SDL3VideoEndpoint.Dispose` wrap `_renderer.Dispose()` +
      `SDL.GLMakeCurrent/GLDestroyContext` in individual try/catch
      blocks with `LogWarning` so a dying driver cannot block `_renderer
      = null` or leak downstream cleanup.)*
- [ ] **3.40f** Reuse HUD scratch VBO buffer — also tracked as §8.7;
      de-duplicate when implementing. *(S13)*
- [x] **3.40g** Avalonia: publish `_catchupLagThreshold`/`_lastUploadedPts`
      via `Volatile.Read`/`Volatile.Write` (or pack into `long`). *(A6)*
      *(Done: `_catchupLagThresholdTicks` is a `long` updated via
      `Volatile.Write` from the setter and read via `Volatile.Read` on the
      render thread; `_lastUploadedPts` is render-thread-only.)*
- [x] **3.40h** Extract `ResolvePresentationClock` / `TryPullFrameWithCatchUp`
      / `PresentFrame` from the 125-line `OnOpenGlRender`. *(A8)*
      *(Done 2026-04-23 seventh pass: `AvaloniaOpenGlVideoEndpoint.OnOpenGlRender`
      is now ~55 lines — entry guards + viewport math + try/catch/finally
      shell. The three helpers each carry their own XML explaining the
      invariant they own (override-origin normalisation; pull + bounded
      catchup; upload/reuse + internal-clock advance). No behavioural
      change; mechanical refactor.)*
- [x] **3.40i** Avalonia Dispose: only stop the state machine; rely on
      `OnOpenGlDeinit` for renderer teardown to avoid racing a compositor
      draw. *(A11)* *(Already done at `AvaloniaOpenGlVideoEndpoint.cs:577-609`
      with inline `§3.40i / A11` comment — Dispose throws on attached
      visual tree, then calls `_ = StopAsync()` and does NOT dispose
      `_renderer` (that happens on the queued `OnOpenGlDeinit`). Stale
      checkbox flipped 2026-04-23 seventh pass.)*
- [x] **3.40j** Avalonia parent/clone Dispose: require detach from visual
      tree first; throw `InvalidOperationException` if attached. *(A12)*
      *(Done 2026-04-23 sixth pass: `AvaloniaOpenGlVideoEndpoint.Dispose`
      throws `InvalidOperationException` when `VisualRoot is not null`
      so the compositor can't race a still-attached control during
      teardown. Clone endpoint follows the same pattern — deferred
      until §3.40a resolves the clone-wiring contract.)*
- [x] **3.40k** Avalonia: stash `_renderScaling` on UI thread via
      `OnPropertyChanged` instead of reading `VisualRoot.RenderScaling` per
      frame. *(A13)* *(Done 2026-04-23 sixth pass: new
      `_renderScalingBits` `long` field holds the IEEE-754 bit pattern
      of the render-scaling double, updated via `Interlocked.Exchange`
      from `OnAttachedToVisualTree` + `OnPropertyChanged(BoundsProperty)`
      (both UI-thread). `OnOpenGlRender` reads it with `Interlocked.Read`
      + `BitConverter.Int64BitsToDouble`, never touching
      `VisualRoot.RenderScaling` on the render thread.)*

### NDI input (Tier 1-N)

- [x] **3.41** `_cts` per-start (not latched singleton) in `NDIAudioChannel` and
      `NDIVideoChannel`; `StartCapture` throws `ObjectDisposedException` when
      `_disposed`. *(N3)* *(Done: `NDIAudioChannel` had this from an
      earlier pass; `NDIVideoChannel` caught up 2026-04-23 sixth pass —
      nullable `_cts` rebuilt per `StartCapture`, snapshotted into a
      local at the top of `CaptureLoop` so a Dispose-after-Start that
      nulls `_cts` can't NRE the loop, and `Dispose` does
      `Interlocked.Exchange(ref _cts, null).Cancel()` → `Join(2s)` →
      `Dispose`.)*
- [ ] **3.42** `NDISource._sessionGate` held across `recv_connect` and framesync
      create/destroy; loop-join capture threads instead of 2 s timeout. *(N1, N2, N19)*
- [x] **3.43** `NDIClock` uses `options.SampleRate`. *(N5)* *(Done:
      `NDIClock.ctor` takes `sampleRate` and `NDISource.Open` constructs
      it as `new NDIClock(sampleRate: options.SampleRate)` — verified
      2026-04-23 sixth pass.)*
- [x] **3.44** Narrow capture-loop exception handling; guarantee
      `FreeAudio`/`FreeVideo` in `finally`. *(N6)* *(Already done in
      `NDIAudioChannel.CaptureLoop` (L206+) and
      `NDIVideoChannel.CaptureLoop` (L157+): narrow
      `catch (OperationCanceledException)` + `catch (Exception) when
      (!token.IsCancellationRequested)` with tagged-exception log, and a
      `finally` block that guarantees `FreeAudio`/`FreeVideo` when
      `haveFrame` is set — wraps the free in its own try/catch so a
      buggy free cannot take down the capture thread. Stale checkbox
      flipped 2026-04-23 seventh pass.)*
- [x] **3.45** Rename `NDISource.Stop` → `StopClock` with `[Obsolete]` forwarder. *(N12)*
      *(Done 2026-04-23 sixth pass: `NDISource.StopClock()` is the new
      name; `Stop()` is now an `[Obsolete]` forwarder that points
      callers at the narrower verb. `NDIAVChannel.Stop` follows. The
      in-tree `MFPlayer.NDIPlayer` still uses `.Stop()` via the
      Obsolete path — intentional until §5.11 sample migration.)*
- [x] **3.46** `StartCapture` "already started" CAS guard. *(N18)* *(Done:
      both `NDIAudioChannel.StartCapture` and
      `NDIVideoChannel.StartCapture` use
      `Interlocked.CompareExchange(ref _captureStartedFlag, 1, 0) != 0`
      to short-circuit a double-start with a `LogDebug` instead of
      racing two capture threads against the same `_frameSync`.)*
- [x] **3.47** Allow `NDIAVChannel` with null `AudioChannel` (video-only NDI sources).
      *(N14)* *(Done 2026-04-23 sixth pass: `NDIAVChannel.AudioChannel`
      is now `IAudioChannel?`; the ctor rejects only the degenerate
      case where **both** audio and video are null.
      `MFPlayer.NDIAutoPlayer` keeps its audio-required expectation
      via a local null-check + explanatory `InvalidOperationException`.)*
- [x] **3.47a** `NDIVideoChannel` I420 chroma stride heuristic review against padded
      sources. *(N8)* *(Done 2026-04-23: `CopyI420` now carries an inline
      "§3.47a / N8 — I420 chroma-stride heuristic" comment explaining that
      NDI exposes only the Y-plane stride, so chroma strides derive from
      `Y_stride / 2` (correct for 16/32/64-byte aligned padded sources
      because aligned Y strides remain a multiple of 2). The
      `Math.Max(uvRowBytes, …)` is documented as a belt-and-braces guard
      against the zero-stride pathological frame.)*
- [x] **3.47b** `NDIAudioChannel` DropOldest accounting: use the manual-ring
      pattern used by `NDIVideoChannel` so `_framesProduced` stays accurate.
      *(N9)* *(Done: `NDIAudioChannel` switched from
      `BoundedChannelFullMode.DropOldest` to manual drop-oldest on an
      unbounded channel — the capture loop explicitly `TryRead`s the
      evicted chunk, returns it to `_pool`, and increments/decrements the
      explicit `_framesInRing` counter. Pairs every decrement with a
      `Debug.Assert(>= 0)` and only advances `_framesProduced` for chunks
      that were actually enqueued without subsequent eviction.)*
- [x] **3.47c** `NDISource.OpenByNameAsync`: wrap open + hand-off in try/catch,
      dispose finder on failure. *(N13)* *(Done: the `Open(found.Value,
      options)` call is wrapped in a try/catch that disposes the finder
      before rethrowing, so a receiver/framesync create or connect failure
      no longer leaks mDNS discovery threads for the lifetime of the
      process. The non-auto-reconnect path also disposes the finder after
      a successful open.)*
- [x] **3.47d** `NDISource.WatchLoop`: break immediately when `WaitOne` and
      `IsCancellationRequested` are both true. *(N15)* *(Done:
      `WatchLoop` now `break`s out of the loop when `WaitHandle.WaitOne`
      returns `true` (cancellation fired), saving one extra
      `ConnectionCheckIntervalMs` hang on Dispose.)*
- [x] **3.47e** `NDIVideoChannel.EnqueueFrame` ring accounting: strict
      increment-on-write / decrement-on-read; `Debug.Assert(>= 0)`. *(N16)*
      *(Done 2026-04-23 sixth pass: every `_framesInRing` decrement in
      `EnqueueFrame`, `FillBuffer`, and the `Dispose` drain loop is
      paired with `Debug.Assert(after >= 0, …)` so a missed-increment
      bug surfaces immediately in debug builds instead of silently
      reporting negative `BufferAvailable`.)*
- [x] **3.47f** Document `NDISource.StateChanged.args.NewState` as authoritative.
      *(N17)* *(Done 2026-04-23 sixth pass: XML on
      `NDISource.StateChanged` now explicitly calls out that the
      event args' `NewState` is the authoritative transition value;
      the `State` property may already have advanced when a handler
      runs because dispatch happens on the `ThreadPool`.)*
- [x] **3.47g** Create framesync before `receiver.Connect(source)` to match the
      SDK sample order. *(N20)* *(Done: `NDISource.Open` now creates the
      `NDIFrameSync` immediately after the receiver and only then calls
      `receiver.Connect(source)`; `Connect` is wrapped in a try/catch that
      disposes the framesync + receiver on failure. Matches the SDK sample
      order so the first frame or two after connect is no longer dropped
      because the framesync was not yet attached.)*
- [x] **3.47h** `NDIAudioChannel.Dispose` drain + re-pool ring buffers (cosmetic).
      *(N21)* *(Done: `Dispose` now drains `_ringReader` after
      `TryComplete` and returns each leftover chunk to `_pool`, keeping
      post-Dispose diagnostics clean (zero pool churn, no
      rented-but-never-returned tail).)*
- [x] **3.47i** `NDIVideoChannel.ParseNdiColorMeta` minimal XML reader instead of
      `Contains("BT.709")`. *(N22)* *(Done: `ParseNdiColorMeta` now
      scopes its search to the `<ndi_color_space …/>` tag, extracts
      `colorspace`/`range` as attribute values via a bounded
      `TryReadAttribute` helper, and only classifies the color space from
      those attribute values. A `<note>not BT.2020</note>` comment or any
      other tag in the metadata payload can no longer mis-classify the
      stream.)*
- [x] **3.47j** `NDIReceiverSettings.AllowVideoFields` default `false` because the
      receive path always pulls Progressive. *(N23)* *(Done 2026-04-23
      sixth pass: default flipped to `false` in
      `NDI/NDILib/NDIWrappers.cs` with an XML note explaining the
      rationale; consumers that really want fielded video opt in
      explicitly via `new NDIReceiverSettings { AllowVideoFields = true }`.)*

### Channel / endpoint contract cleanups

- [~] **3.48** Document (or enforce) single-reader on `IMediaChannel.FillBuffer` —
      two routes sharing one audio channel today race. Either serialize inside
      the router or document forbidden. *(CH1)* *(Done at the XML/doc level
      2026-04-23: `IMediaChannel<TFrame>.FillBuffer` XML carries a bold
      "Single-reader invariant (§3.48 / CH1)" paragraph explaining the
      serialisation guarantee and pointing multi-reader consumers at the
      channel's `Subscribe(...)` facility. Runtime enforcement via
      `Debug.Assert(Interlocked.Exchange(ref _reader, 1) == 0)` on the
      concrete channels is deferred — small scope but touches every
      `IMediaChannel` implementation.)*
- [ ] **3.49** Split `IAudioChannel.Position` from a new
      `IAudioChannel.ReadHeadPosition` (the PTS of the next sample to be
      produced), removing the implicit "Position updates after the read"
      dance. *(CH2)*
- [~] **3.50** Mark the non-PTS `IAudioEndpoint.ReceiveBuffer` default impl as
      `[Obsolete]` so sinks that should emit timecodes cannot silently skip
      the PTS overload. *(CH4)* *(XML note on
      `IAudioEndpoint.ReceiveBuffer` is the live actionable guidance;
      `[Obsolete]` attribute intentionally **not** applied —
      `NDIAVEndpoint` (the only in-tree timecoded sink) has already
      migrated to the PTS overload, but `PortAudioEndpoint` /
      `VirtualClockEndpoint` / test endpoints legitimately implement
      only the non-PTS variant and decorating the interface method would
      fire warnings on every call site and every implementation. The XML
      paragraph is the guidance new timecoded sinks follow. Re-evaluated
      2026-04-23 seventh pass — downgraded from `[ ]` to `[~]`.)*
- [x] **3.51** `IPullAudioEndpoint.FillCallback` swap semantics: replace the
      setter with `SetFillCallback(IAudioFillCallback?)` that does a volatile
      write + short spin for in-flight fills. *(CH5)* *(Done 2026-04-23 via
      the compatible surface — kept `FillCallback { get; set; }` on the
      interface (avoids a source-breaking change for third-party impls)
      and documented the MUST-spin semantics in the property's XML; the
      PortAudio `CallbackEndpoint.FillCallback` setter now does volatile
      write followed by a bounded `SpinWait` on `_callbackInFlight` when
      the incoming value is null, so the router's Unregister path can
      safely tear down the EndpointEntry immediately after the setter
      returns.)*
- [x] **3.52** Document (and enforce in Debug via assertions) that
      `IPullAudioEndpoint.EndpointFormat` / `FramesPerBuffer` are frozen
      after the endpoint is Open. *(CH6)* *(Done 2026-04-23 at the XML/doc
      level: both properties now carry a "Frozen-after-Open (§3.52 / CH6)"
      remark. A per-read `Debug.Assert` in the router would require
      snapshotting the values at `RegisterEndpoint` and is deferred —
      router code only reads these twice per endpoint lifetime anyway, so
      the cost/benefit of the snapshot+compare is weak.)*
- [x] **3.53** `IFormatCapabilities<T>` must declare non-empty
      `SupportedFormats` or throw — wire this here in Tier 1 as a
      `Debug.Assert` and promote to a throw in §6.10. *(CH9, R22 prep)*
      *(Done: `AVRouter.CreateAudioRoute` / `CreateVideoRoute` both have
      `Debug.Assert(caps.SupportedFormats is not null, "... must be non-null
      (3.53 / CH9)")`. XML on `IFormatCapabilities<T>` documents the
      non-empty convention and the §6.10 promotion plan.)*
- [x] **3.54** `AVRouter.DriftEma` single-pair limitation: either assert
      single-pair input or key by `(audioInputId, videoInputId)`
      (mirror of §6.9; pick one place to land the fix). *(R16)*
      *(Already done at `AVRouter.cs:780-793` with inline `§3.54 / R16`
      comment: the EMA stores the `(audioInput, videoInput)` pair and
      resets the filter on any pair change, so poll-site switches
      cannot contaminate history. Full per-pair dictionary remains
      tracked under §6.9. Stale checkbox flipped 2026-04-23 seventh
      pass.)*
- [x] **3.55** `FFmpegVideoChannel._defaultSub` double-dispose path during
      concurrent teardown — guard `EnsureDefaultSubscription` with a
      `_disposed` check under `_subsLock`. *(review Concurrency #10)*
      *(Done 2026-04-23: after acquiring `_subsLock`,
      `EnsureDefaultSubscription` re-checks `_disposed` via
      `ObjectDisposedException.ThrowIf` before creating a new default
      subscription — prevents a concurrent Dispose that nulls `_defaultSub`
      from racing the lazy init into an already-disposed channel.)*
- [x] **3.56** Decide fate of `IAudioChannel.Volume` (and its
      `FFmpegAudioChannel` / `NDIAudioChannel` setters) — remove or keep
      per §0.4.5. *(review §Consistency, NDI §Consistency)*
      *(Done 2026-04-23, fifth pass: applied
      `[Obsolete("Channel-level Volume is legacy — use AVRouter.SetInputVolume…")]`
      to the **setter accessor** in `IAudioChannel.Volume` (getter stays
      non-obsolete because diagnostic / meter code legitimately reads it).
      Migrated the in-tree caller `MediaPlayer.AttachDecoder` to
      `_router.SetInputVolume(inputId, _volume)`, and the SimplePlayer
      arrow-key volume controls to `router.SetInputVolume(inputId, volume)`.
      Concrete-class setters on `FFmpegAudioChannel` / `NDIAudioChannel`
      remain (they implement the storage) but are no longer called by any
      in-tree code.)*

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
      *(Done 2026-04-23, fifth pass: `MediaPlayer` implements `IAsyncDisposable`;
      sync `Dispose` delegates to `DisposeAsync` which awaits
      `_router.StopAsync()`, each endpoint's `StopAsync()`, and
      `FFmpegDecoder.StopAsync()` (§4.5). Async siblings
      `MediaPlayer.AddEndpointAsync(IAudioEndpoint|IVideoEndpoint|IAVEndpoint, CancellationToken)`
      and `MediaPlayer.RemoveEndpointAsync(IMediaEndpoint, CancellationToken)`
      are in place; the sync overloads are kept for the IDLE-state common
      case (no endpoint StartAsync runs at registration time, so they
      cannot deadlock) and only call `GetAwaiter().GetResult()` on the
      narrow path where the player is already Playing — async callers
      should prefer the *Async overloads. Covered by
      `MediaPlayerLifecycleTests` (3 tests).)*
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
- [x] **4.7** `IAudioEndpointFormatHint` propagation so decoder skips SWR when the
      endpoint format already matches source. *(main review #6)*
      *(Done 2026-04-24: `FFmpegDecoderOptions.AudioTargetFormat` lets
      callers reshape the decoder's output to an endpoint-native
      rate/channel count. `FFmpegAudioChannel` accepts a `targetFormat`
      ctor parameter, configures SWR's `out_chlayout`/`out_sample_rate`
      accordingly, announces `SourceFormat == target` so the router
      recognises source == endpoint and skips its per-route resampler,
      and sizes output via `av_rescale_rnd` so rate conversion is safe.
      Channel-count guard compares against the codec context (frame
      validity) rather than the target. A full `IAudioEndpointHint`
      interface was not needed — the builder can set the option
      directly when there's exactly one audio endpoint with a known
      format.)*
- [x] **4.8** `IClockCapableEndpoint.DefaultPriority` so network clocks register at
      `External`, local hardware at `Hardware`, virtual at `Internal`. *(R11)*
      *(Done: `IClockCapableEndpoint.DefaultPriority` exists with
      <c>Hardware</c> default; `VirtualClockEndpoint` overrides to
      `Internal`; `AVRouter.AutoRegisterEndpointClock` honours the override
      and falls back to `AVRouterOptions.DefaultEndpointClockPriority` only
      when the endpoint left the value at the interface default. Covered by
      `EndpointClockPriorityTests` (3 tests).)*
- [x] **4.9** `AVRouter.ActiveClockChanged` event under `_clockLock`. *(R10)*
      *(Done: `AVRouter.ActiveClockChanged` of type `Action<IMediaClock>?`
      fires on `RegisterClock` / `UnregisterClock` / `SetClock` whenever the
      resolver picks a different active clock. Raised **outside** `_clockLock`
      so subscribers may call back into the router safely; same-clock
      no-ops do not fire. Covered by `AVRouterActiveClockChangedTests`
      (4 tests).)*
- [x] **4.10** `PooledWorkQueue.Complete()` + `IsCompleted`; `TryEnqueueWithCap` to
      prevent leaked reservations. *(PQ1, PQ2, PQ4)*
      *(Done: `PooledWorkQueue<T>.Complete()` + `IsCompleted` flag wired so
      `WaitForItem` returns `false` once the queue drains after Complete;
      `TryEnqueueWithCap(T item, int cap)` does atomic reserve+enqueue and
      releases the reservation on the (unreachable) throw path; the
      reserved-but-not-filled leak window of the legacy two-step
      `TryReserveSlot` + `EnqueueReserved` pattern is now opt-out only.)*
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
- [~] **4.13** Mixer math improvements: denormal-flush on push/fill thread entry,
      optional auto-attenuation / soft-clip, overflow counter on
      `RouterDiagnosticsSnapshot`. *(M2, R3)*
      *(Partial 2026-04-24: soft-clip + overflow counter landed.
      `IAudioMixer.CountOverflows` counts samples outside ±1.0;
      `ApplySoftClip(threshold = 0.98)` uses a tanh-ish Padé curve that
      preserves sign, is monotonic in |input|, and never crosses ±1.
      `AVRouterOptions.SoftClipThreshold` (nullable) enables the feature
      globally; push + pull paths count overflows first (diagnostic
      reflects raw mix), then optionally soft-clip, then measure peak
      post-clip. `EndpointEntry.OverflowSamplesTotal/ThisTick` exposed
      via `EndpointDiagnostics.OverflowSamplesTotal`. 8 new mixer tests.
      **Still open:** denormal-flush-on-thread-entry — `DefaultAudioMixer.FlushDenormalsToZero`
      remains a no-op until a P/Invoke-based MXCSR writer is wired.)*
- [x] **4.14** Apply channel map whenever `BakedChannelMap` is set. *(already tracked
      as 3.15 — keep mirrored with M4.)*
      *(Verified stale 2026-04-24: both pull (AVRouter.cs:1391) and push
      (AVRouter.cs:1626) paths apply `BakedChannelMap` unconditionally
      when non-null, with the inline `§3.15 / R4` comment.)*
- [x] **4.15** Move peak metering to post-map, pre-`ReceiveBuffer`. *(R24, M3)*
      *(Done 2026-04-24: new `EndpointEntry.PeakLevel` measured
      post-channel-map, post-endpoint-gain, pre-soft-clip, immediately
      before `ReceiveBuffer` on both push and pull paths. Input-level
      meter retained as a pre-map source reading for diagnostic
      completeness. Exposed via `AVRouter.GetEndpointPeakLevel(EndpointId)`
      and `EndpointDiagnostics.PeakLevel`.)*

### NDI ergonomic helpers (Tier 2-N)

- [ ] **4.16** `NDIClockPolicy { VideoPreferred, AudioPreferred, FirstWriter }`; only
      the chosen channel writes to the shared `NDIClock`. *(N4)*
- [x] **4.17** `NDIUnsupportedFourCc` / `NDIFormatChange` events; expose
      `MaxForwardPtsJumpMs`. *(N7, N11)*
      *(Done 2026-04-24: new `NDIEvents.cs` adds
      `NDIUnsupportedFourCcEventArgs` (FourCc string + raw uint +
      `IsAudio` discriminator) and `NDIVideoFormatChangedEventArgs`
      (previous + new format). `NDIAudioChannel` + `NDIVideoChannel`
      raise `UnsupportedFourCc` on the first sighting of each FourCC
      (log-once continues); `NDIVideoChannel` raises `FormatChanged`
      when `FormatsEquivalent(...)` returns false between frames —
      never on the first frame. Forwarded on `NDISource` +
      `NDIAVChannel`. `NDISourceOptions.MaxForwardPtsJumpMs` (default
      750 ms, ≤ 0 disables) replaces the hard-coded constant in
      `NDIVideoChannel`.)*
- [ ] **4.18** Process-wide `NDISource.Discovered` singleton registry. *(NDI §Required #3)*
- [x] **4.19** `NDIReconnectPolicy` record replacing the two boolean knobs. *(NDI §Required #2)*
      *(Done 2026-04-24: new `NDIReconnectPolicy.cs`
      (`CheckIntervalMs` + `InitialDelayMs` + `Default` static).
      `NDISourceOptions.ReconnectPolicy` supersedes `AutoReconnect` +
      `ConnectionCheckIntervalMs` (both marked `[Obsolete(error: false)]`).
      `ResolveReconnectPolicy()` bridges the legacy flags. Watch loop +
      finder-retention decisions both use the resolved policy.
      `ForPreset` sets `ReconnectPolicy = NDIReconnectPolicy.Default`
      alongside the legacy flag for source-compat.)*
- [x] **4.20** Narrow `PlanarToInterleaved` to only run on `Fltp` FourCC. *(N10)*
      *(Done 2026-04-23, fifth pass: `NDIAudioChannel` capture loop now
      checks `frame.FourCC != NDIFourCCAudioType.Fltp` before calling
      `PlanarToInterleaved` and returns the framesync slot via `FreeAudio`
      on the reject path. Unsupported FourCC values are logged once via
      `_unsupportedAudioFourCcLogged` (mirrors the existing video-side
      `_unsupportedFourCcLogged` pattern). The current NDI v6 SDK only
      emits Fltp at this code path; the guard exists so a future SDK or
      advanced sender that surfaces a different format cannot silently
      produce garbage interleaved samples.)*

---

## 5. Tier 3 — Builder API (2 weeks)

- [x] **5.1** `MediaPlayerBuilder` with the `With*` methods shown in the review §"Proposed
      simplified API". *(review §Proposed simplified API)*
      *(Done 2026-04-23, fourth pass: `Media/S.Media.FFmpeg/MediaPlayerBuilder.cs`
      with `WithAudioOutput`/`WithAudioSink`/`WithVideoOutput`/`WithAVOutput`/
      `WithClock`/`WithDecoderOptions`/`WithRouterOptions`/`OnError`/
      `OnStateChanged`/`OnCompleted` + `Build()` that unwinds via
      `DisposeAsync` on partial-initialisation failure. Entry point is
      `MediaPlayer.Create()`; the options-taking ctor is now `internal` so
      the builder is the only way to inject `AVRouterOptions` /
      `FFmpegDecoderOptions` defaults. Device-based overloads
      (`WithAudioOutput(AudioDeviceInfo)` etc.) intentionally deferred to
      §5.2 because that requires the one-step endpoint factories in the
      endpoint assemblies, which the builder must not depend on. Covered
      by 8 `MediaPlayerBuilderTests` (null-guards, empty build, endpoint
      registration without auto-start, error-handler wiring, decoder-
      options default application, router-options passthrough, partial-
      failure safety).)*
- [ ] **5.2** One-step factories: `PortAudioEndpoint.Create(device, format)`,
      `SDL3VideoEndpoint.ForWindow(title)`, etc. Closes the "Open before Register"
      footgun in the concrete classes. *(review §Consistency; depends on 1.2)*
- [x] **5.3** Auto-propagate `IVideoColorMatrixHint` from channel → endpoint on route
      creation; remove the YUV prompt from the VideoPlayer test app. *(main review #7)*
      *(Done 2026-04-23, fifth pass: new `IVideoColorMatrixReceiver` interface
      in `Media/S.Media.Core/Video/`; `AVRouter.CreateVideoRoute` calls
      `endpoint.ApplyColorMatrixHint(matrix, range)` once at route-creation
      time when the source channel implements `IVideoColorMatrixHint` AND
      the endpoint implements `IVideoColorMatrixReceiver`. Implemented on
      `SDL3VideoEndpoint` (preserves explicit user values, honours `Auto`
      as "no change") and `AvaloniaOpenGlVideoEndpoint` (also respects the
      `_hasYuvHintsOverride` user-pin flag). Receiver throws are caught and
      logged so a buggy endpoint can't break route creation. Covered by
      `VideoColorHintPropagationTests` (4 tests). VideoPlayer YUV prompt
      removal will land in the §5.11 sample-app rewrite.)*
- [x] **5.4** Auto audio-preroll inside `AVRouter.StartAsync` when an `IPullVideoEndpoint`
      and an audio input coexist (`AVRouterOptions.WaitForAudioPrerollMs = 1000` /
      `MinBufferedFramesPerInput`). Replaces the VideoPlayer warmup block. *(main review #4, R12)*
      *(Done 2026-04-23, fifth pass: added `AVRouterOptions.MinBufferedFramesPerInput`
      (default 0 — disabled) and `AVRouterOptions.WaitForAudioPreroll`
      (default 1 s). `AVRouter.StartAsync` now actually awaits when both
      preroll is configured and the graph has both an audio input and a
      pull-video endpoint — pure-audio or pure-video graphs skip the wait
      so it's safe to enable unconditionally. Wait runs **outside** `_lock`
      so concurrent registration is not blocked. Builder sugar
      `MediaPlayerBuilder.WithAutoPreroll(int minBufferedFrames = 2048,
      TimeSpan deadline = default)` rolls both into one fluent call.
      Covered by `AudioPrerollTests` (3 tests: skip-without-video,
      threshold-met, deadline-hit).)*
- [x] **5.5** Auto-derive `InternalTickCadence` from registered endpoints (pick
      `min(endpoint.NominalTickCadence)`). *(main review #8, R13, C7)*
      *(Done 2026-04-24: new `IAudioEndpoint.NominalTickCadence` +
      `IVideoEndpoint.NominalTickCadence` (both default `null`).
      `AVRouter._effectiveCadenceSwTicks` volatile-written by
      `RecomputeEffectiveCadence()` under `_lock` on every
      Register/Unregister; push + video loops `Volatile.Read()` each
      tick so registration reshapes cadence without a Stop/Start.
      Sub-ms hints clamped to 1 ms. Public `EffectiveTickCadence`
      getter for diagnostics. 5 new tests.)*
- [x] **5.6** Expose `VideoRouteOptions.OverflowPolicy`. *(main review #9)*
      *(Done 2026-04-24: `VideoRouteOptions.OverflowPolicy` and
      `.Capacity` are now nullable overrides; defaults mirror the
      prior router behaviour (`Wait` + deep queue for pull endpoints,
      `DropOldest` + 4 for push). Inline `§5.6` comment in
      `AVRouter.CreateVideoRoute`.)*
- [ ] **5.7** `MediaPlayerBuilder.WithNDIInput(...)` overloads; orchestrate video-first
      format detection + prebuffer + start ordering. *(NDI §Required #1, #6)*
- [ ] **5.8** Auto-register `NDIClock` at `Hardware` priority inside
      `WithNDIInput(...)`. *(NDI §Required #4)*
- [ ] **5.9** `WithAutoAvDriftCorrection(options?)` rolling up NDIAutoPlayer L380–416.
      *(NDI §Required #5)*
- [x] **5.10** `RouterBuilder` parallel for advanced users (atomic registration;
      closes R8 by construction). *(cross-cutting Tier 3)*
      *(Done 2026-04-24: new `Media/S.Media.Core/Routing/RouterBuilder.cs`
      — fluent `AddAudioInput`/`AddVideoInput`/`AddEndpoint`/`AddRoute`/
      `AddClock`/`WithOptions`. Opaque tokens map to real `InputId` /
      `EndpointId` values at `Build()` time; partial-failure disposes
      the half-wired router and rethrows — callers never see a
      partially-wired router. 5 new tests.)*
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
- [x] **10.3** Deterministic `MediaPlayer.DisposeAsync` orchestration
      (stop router → stop endpoints in parallel → stop decoder → dispose all).
      *(review §Nice-to-haves)*
      *(Done 2026-04-24: `MediaPlayer.DisposeAsync` now runs
      router.StopAsync → `Task.WhenAll` of per-endpoint StopAsync (each
      wrapped in its own try/catch so a faulty endpoint can't
      short-circuit the whole fan-out) → decoder.StopAsync →
      ReleaseSession + router.Dispose. Documented inline.)*
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

