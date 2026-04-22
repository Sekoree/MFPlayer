# MFPlayer — API & Implementation Review

*Review date: 2026-04-22. Read-only inspection; no source files were modified.*
*Updated 2026-04-22 with framing notes §0 (Layering, Endpoint consolidation) following
discussion with the repo owner. See the companion document
[`Implementation-Checklist.md`](./Implementation-Checklist.md) for a ticklist-shaped
version of the refactor roadmap.*

---

## 0. Framing (added post-review)

### 0.1 Layering: `MediaPlayer` vs `AVRouter`

These two types serve **different purposes** and are intentionally not merged:

- **`S.Media.Core.Routing.AVRouter`** is the base of a much larger media
  input/output routing system. It is the substrate for multi-source mixing,
  timeline playback, clone fan-out, recording endpoints, and live/NDI
  production graphs. It must stay minimal, composable, and free of "convenience"
  behaviour that would be wrong for a non-playback use case. Every ergonomic
  helper proposed below (builder, drain detection, auto-preroll, etc.) is
  scoped to the facade layer unless it improves `AVRouter` correctness.
- **`S.Media.FFmpeg.MediaPlayer`** is a thin facade over `AVRouter` intended for
  simple single-file playback apps. The `MediaPlayerBuilder` proposal in
  §"Proposed simplified API" lives *here*, not on `AVRouter`.

Concretely:
- Features like `WaitForCompletionAsync`, `OpenAndPlayAsync`,
  `AddEndpointAsync`, and `PlaybackState` belong on `MediaPlayer`.
- Features like per-route `LiveMode`, endpoint format capabilities, clock
  registry policy, and PTS-aware mixing belong on `AVRouter`.
- The review's tier breakdown already respects this split; the checklist
  (`Implementation-Checklist.md` §0.1) calls it out explicitly.

### 0.2 Audio endpoint consolidation (`IAudioEndpoint`)

The repo owner asked whether the "Output" and "Sink" interfaces could be
unified into a single `IAudioEndpoint`, mirroring the video side. **The
interface-level consolidation is already complete**:

- `IAudioEndpoint` (`Media/S.Media.Core/Media/Endpoints/IAudioEndpoint.cs`)
  is the single unified push contract. Its XML doc literally states
  *"Replaces `IAudioOutput`, `IAudioSink`, and `IAudioBufferEndpoint` with a
  single unified push contract."*
- `IPullAudioEndpoint : IAudioEndpoint` is an **optional capability mixin**
  implemented by endpoints that are driven by an RT pull callback (hardware
  outputs like PortAudio). It adds `FillCallback`, `EndpointFormat`,
  `FramesPerBuffer`.
- The video side has exactly the same shape: `IVideoEndpoint` (push) +
  `IPullVideoEndpoint : IVideoEndpoint` (pull capability).
- `AVRouter.RegisterEndpoint(IAudioEndpoint)` and
  `RegisterEndpoint(IVideoEndpoint)` each branch internally on the capability
  interface (`is IPullAudioEndpoint pull` at `AVRouter.cs:314, 640`;
  analogous for video at `:316, 650`), so **users register every destination
  through one method** and the router decides push vs pull at runtime.

What is still confusing is therefore *not* the interfaces but:

1. **Concrete class names** still split the world into "Output" vs "Sink":
   `PortAudioOutput` vs `PortAudioSink`, `SDL3VideoOutput` vs `SDL3VideoCloneSink`,
   `NDIAVSink`, `AvaloniaOpenGlVideoOutput`, etc. End users reasonably infer
   that they are different kinds of objects that need to be plumbed differently,
   even though `AVRouter` treats them identically.
2. **Docs and examples** (`MediaPlayer-Guide.md`, `Quick-Start.md`,
   `Usage-Guide.md`, `Clone-Sinks.md`) use both words, reinforcing the split.
3. **`PortAudioOutput` exposes `Clock` only after `Open()`** (see **P1** / **CH8**
   below), which conflates "Open the device" with "Register the endpoint" in
   the user's mental model.

Action plan (tracked in `Implementation-Checklist.md §1`):

- Standardise public vocabulary on **"endpoint"** only; mark "output" / "sink"
  as legacy in all XML and `Doc/`.
- **Collapse to one concrete class per backend.** Under the existing interface
  model, two concrete PortAudio types are not needed: both
  `PortAudioOutput` (callback/pull) and `PortAudioSink` (blocking-write/push)
  wrap a `Pa_OpenStream` handle, and `Pa_GetStreamTime` is a valid hardware
  clock in both modes. Merge them into a single `PortAudioEndpoint` with a
  `DrivingMode { Callback, BlockingWrite }` option (see §0.3 below for the
  concrete shape). Same argument applies to `NDIAVSink`/`SDL3VideoOutput`
  etc.: each backend gets one `*Endpoint` class. Breaking, with `[Obsolete]`
  type-forwarders for one release:
  - `PortAudioOutput` + `PortAudioSink` → `PortAudioEndpoint`.
  - `NDIAVSink` → `NDIAVEndpoint`.
  - `SDL3VideoOutput` → `SDL3VideoEndpoint`;
    `SDL3VideoCloneSink` → `SDL3VideoCloneEndpoint`.
  - `AvaloniaOpenGlVideoOutput` → `AvaloniaOpenGlVideoEndpoint` (and clone).
- Merge "new + Open" into a single `Create(...)` factory on each concrete
  endpoint, so `Clock` is always valid immediately after construction
  (closes **P1** and **CH8**).
- Add `IAudioEndpoint.NegotiatedFormat` (nullable) so push endpoints can
  advertise a preferred rate/channel count, enabling route-level auto-resampling
  in the router — closes **R5** without reintroducing a `SinkFormat`/`OutputFormat`
  split.
- `AVRouter.RegisterEndpoint` already figures out push vs pull at runtime; no
  router API change is required. Document this explicitly in the XML on both
  `IAudioEndpoint` and `IVideoEndpoint`.

**Net result for end users:** one method (`AddEndpoint` on `MediaPlayer`,
`RegisterEndpoint` on `AVRouter`), one interface family per media type
(`IAudioEndpoint` / `IVideoEndpoint` / `IAVEndpoint`), and **exactly one
concrete class per backend**, whose name carries no policy hints about
"primary" vs "secondary" plumbing.

### 0.3 `PortAudioClock` as an independent clock source

Per the repo owner's follow-up: *"Could the `PortAudioOutput.Clock` perhaps be
a separated class that can act as a clock for a Sink?"* — yes, and in fact
it effectively already is. `Audio/S.Media.PortAudio/PortAudioClock.cs` is a
standalone `IMediaClock` implementation derived from `HardwareClock`; it
wraps a `HandleRef` over the PA stream handle and reads `Pa_GetStreamTime`.
Nothing in it depends on the endpoint being driven in callback mode.

So on the merged `PortAudioEndpoint`:

```csharp
public sealed class PortAudioEndpoint : IAudioEndpoint, IClockCapableEndpoint
{
    public enum DrivingMode { Callback, BlockingWrite }

    public static PortAudioEndpoint Create(
        AudioDeviceInfo device,
        AudioFormat     format,
        DrivingMode     mode             = DrivingMode.Callback,
        int             framesPerBuffer  = 0);

    public string       Name              { get; }
    public AudioFormat  HardwareFormat    { get; }
    public IMediaClock  Clock             { get; }     // PortAudioClock; valid from ctor
    public DrivingMode  Mode              { get; }

    // Implements IPullAudioEndpoint only when Mode == Callback (runtime downcast
    // in AVRouter already handles this — no router change required).
    // Implements IClockCapableEndpoint in BOTH modes — blocking-write users get
    // a real hardware clock too.
}
```

- **`Callback`** mode → zero-alloc RT pull path. Best for the primary hardware
  output.
- **`BlockingWrite`** mode → RT-safe push from any source thread with an
  internal worker + PA blocking write. Best for secondary fan-out, and still
  exposes a hardware clock because `Pa_GetStreamTime` works identically on a
  blocking stream.
- Push-only backends with no hardware clock (NDI send, file-writer) simply
  don't implement `IClockCapableEndpoint`. That's the whole purpose of keeping
  `IClockCapableEndpoint` as a capability mixin, and it means no extra
  plumbing type is ever needed to express "I can receive audio *and*
  provide a clock".

Follow-up consequence: the test apps' common pattern of
`using var output = new PortAudioOutput(); output.Open(...); router.SetClock(output.Clock);`
collapses to a single-line `router.RegisterEndpoint(PortAudioEndpoint.Create(...))`
(clock auto-registers at `Hardware` priority), whether the user picked
callback or blocking-write mode. The checklist reflects this merge in §1.2.

### 0.4 Clock selection: PA-recommended, but swappable

Confirming the intended model for the merged endpoint:

- Every `IClockCapableEndpoint` **auto-registers** its clock at
  `ClockPriority.Hardware` when the endpoint is registered with the router
  (existing behaviour, `AVRouter.cs:340-344`). So by default,
  `router.RegisterEndpoint(paEndpoint)` installs the `PortAudioClock` as the
  de-facto hardware clock and nothing else has to be done. This is the
  **recommended** path for PA-only playback.
- The clock is **not** owned by the endpoint in any exclusive sense. It is
  just an entry in the router's priority-ranked registry
  (`Internal < Hardware < External < Override`), and the resolver picks the
  highest-priority currently-registered clock for every tick.
- To swap in a different clock when another hardware source is also involved
  (e.g. sending via NDI alongside PA), the user has two options:
  ```csharp
  router.RegisterClock(ndiClock, ClockPriority.External);   // outranks Hardware
  // — or —
  router.SetClock(ndiClock);                                // @ Override
  ```
  The PA endpoint's `PortAudioClock` stays registered at `Hardware` the whole
  time; it simply loses the resolver race until the higher-priority entry is
  unregistered, at which point the resolver **automatically falls back** to
  the PA clock without any code change.
- Note on terminology: the framework's current *send*-side NDI endpoint
  (`NDIAVSink` / future `NDIAVEndpoint`) is **not** `IClockCapableEndpoint`.
  `NDIClock` as it exists today is a *receive*-side class derived from
  sender-stamped timestamps. In real production the hardware clock for an
  NDI send is usually the PA device or a PTP/genlock source, not NDI itself;
  the two natural patterns are PA-clocked (default) or PTP-clocked
  (`SetClock(ptpClock)`). If a future NDI **sender** wants to advertise a
  clock, it can implement `IClockCapableEndpoint` and auto-register at
  `Hardware` like PA does — no router change needed.
- Known rough edge (executive finding #8): the router's internal tick
  cadence is decoupled from `Clock.Position`. Swapping in a clock that
  doesn't advance in lock-step with wall time can cause silent drift.
  Tracked in the checklist as §4.9 (`ActiveClockChanged` event) + §6.7
  (per-axis cadence auto-derived from the active clock) + §5.5 (auto-derive
  tick cadence from registered endpoints).

---

## Executive summary (top-impact findings)

1. **Boilerplate explosion in Test apps** — SimplePlayer is 385 LoC and MultiOutputPlayer 264 LoC for what should be ≤20 LoC. Every app repeats device picking, format computation, `BuildRouteMap`, `new AVRouter()`, `RegisterEndpoint`, `SetClock`, `RegisterAudioInput`, `CreateRoute`, drain detection, EOF plumbing, and start‑ordering. The framework is powerful but lacks a single “just play this” facade. `MediaPlayer` (in `S.Media.FFmpeg`) *does* most of this already but is not used by any test app except implicitly.
2. **`FFmpegDecoder.Dispose` dead‑lock hazard** — It awaits the demux task with a 3 s blocking `Wait`, but channels disposal runs *before* the wait; if the demux worker is mid-`WriteAsync` into one of those channel writers with cancellation disabled, teardown can hang. The XML doc acknowledges this but the API hides it.
3. **NDIAVSink — `_pendingAsyncPin` leak on shutdown race & `StopAsync` not fully idempotent.** The async‑send pin/pool buffer retention is safe on the steady state but `Dispose` can double-Free if `StopAsync` already ran (both call `FlushAsync` + `ReleasePendingAsyncVideo` but the second call is a no‑op only because of null sentinels — brittle). Also `StopAsync` flips `_started` to 0 but does not null-out `_cts`, so a second `StartAsync` *after* `StopAsync` uses a cancelled CTS (see below — `Interlocked.CompareExchange(ref _started, 1, 0)` will succeed the second time, but `_cts` is *replaced*, good — however the `_videoThread`/`_audioThread` fields are not reset, and `StopAsync` only joins them, it does not null them, so Dispose calls `Join` again on a dead thread which is harmless but odd).
4. **Seek-path non-atomicity** — `FFmpegDecoder.Seek` bumps the epoch while writing the flush sentinel as **best-effort `TryWrite`**, so when the per-stream packet queue is full the flush is dropped silently and decoders may never receive the new epoch's flush, leading to a stale-packet flood that is only filtered by the `< epoch` check (which correctly drops them) but `ApplySeekEpoch` is *never* called on that channel, so the codec is not flushed, the position stays stale, and `EndOfStream` underrun handling races. A `WriteAsync` with a small timeout, or draining the channel to full then writing, is needed.
5. **Push-video pending leak on thread race** — `AVRouter._pushVideoPending[route.Id] = candidate` after the `TryGetValue(...).Dispose()` pattern is not atomic; between `TryGetValue` and the indexer write a racing `RemoveRouteInternal` can observe and drain the dictionary, then *our* indexer write silently re-adds a stale entry that `RemoveRouteInternal` won’t see, leaking the video buffer’s refcount.
6. **`FFmpegVideoChannel.Seek`/`BufferAvailable` are not thread-safe with `Subscribe`/`Dispose`.** `BufferAvailable` iterates `_subs` without taking the immutable snapshot into a local (`var subs = _subs; foreach…`), and `Seek` iterates `_subs` with no read-lock or volatile snapshot. `_subs` is an `ImmutableArray` so this is *probably* safe but the `foreach` binds the field at enumeration start — replace with a local snapshot for correctness under Dispose races.
7. **`StreamAvioContext` `ReadPacket` returns `AVERROR_EOF` on *any* exception**, masking IO errors as EOF — a broken HttpContent stream will look like a clean EOF to the caller, producing silent truncation. Needs `AVERROR(EIO)` for non-EOF failures and propagation of an `Exception` via the owning decoder’s error channel.
8. **Clock model is ambiguous vs. the docs** — The plan dropped `SampleRate` from `IMediaClock` in favor of `TickCadence`, but the router still ticks its own thread with `InternalTickCadence` decoupled from `Clock.Position`. Users setting `SetClock(someExternalClock)` whose position doesn’t advance in lock step with the internal tick (e.g. a paused external clock) will see the router still pushing audio buffers paced by its own thread while stamping PTS from the external clock — a silent drift source. Worth clarifying.
9. **Ref-counted video buffer lifecycle is subtle and error-prone.** `PublishToSubscribers` retains `subs.Length - 1` additional refs; a `TryPublish` that returns false disposes one ref — works only because the code assumes the buffer already holds one ref from creation. Any path that re-publishes on retry, catch-up skip, or pending cache must match these assumptions exactly; currently the `DropOldest` branch evicts a frame and calls `MemoryOwner?.Dispose()` but *also* the `TryPublish` returns false and the caller may not dispose again. Needs a dedicated owner type or a scoped `Release` helper.
10. **No centralized format negotiation.** Every test app computes `outChannels = Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, 2))`, `hwFmt = new AudioFormat(srcFmt.SampleRate, outChannels)`, and `BuildRouteMap`. This should be a one-line API: `AudioFormat.NegotiateFor(src, device)` returning both format and default route map.

---

## Architecture overview (as understood)

```
Source        Decoder/Demux           Channel              Router              Endpoint
─────         ─────────────           ───────              ──────              ────────
file/Stream → FFmpegDecoder       → FFmpegAudioChannel → AVRouter         → PortAudioOutput (IPullAudioEndpoint + IClockCapableEndpoint)
              ├─ DemuxWorker        (ring: Channel<AudioChunk>)  │           → PortAudioSink   (IAudioEndpoint, push)
              │   ├─ av_read_frame                                │           → SDL3VideoOutput (IPullVideoEndpoint)
              │   └─ per-stream                                   ├─ audio    → AvaloniaVideoOutput
              │      bounded Channel<EncodedPacket>               │  pull:      (IPullVideoEndpoint)
              └─ FFmpegDecodeWorkers                              │  FillCb    → NDIAVSink       (IAVEndpoint)
                  (swr / hwframe_transfer / sws_scale)            │            → SDL3CloneSink   (IVideoEndpoint, push)
                                                                  ├─ audio
                                                                  │  push tick → endpoint.ReceiveBuffer(...)
                                                                  ├─ video
                                                                  │  pull:     → endpoint.TryPresentNext
                                                                  │  push:     → endpoint.ReceiveFrame
                                                                  └─ clock registry (Internal < Hardware < External < Override)
```

- **Seek-epoch protocol** (`FFmpegDecoder.Seek` at L524–564, handled in `FFmpegDecodeWorkers.cs` L57–88): demux bumps `_seekEpoch`, writes a `Flush` sentinel, decode worker compares packet epochs and invokes `ApplySeekEpoch` on stream advance.
- **Video fan-out**: `FFmpegVideoChannel.Subscribe` creates one bounded `Channel<VideoFrame>` per subscriber; `PublishToSubscribers` (L658–681) uses `RefCountedVideoBuffer` with manual retain/release to share a single `ArrayPool` rental across all subscribers.
- **Audio path**: each channel owns a 16-chunk bounded `Channel<AudioChunk>`; `FillBuffer` (RT) copies from a current chunk + returns buffer to a per-channel `ConcurrentQueue<float[]>` pool.
- **Router**: COW `RouteEntry[]` snapshots, a per-endpoint push thread (`AVRouter-PushTick` + a sibling `AVRouter-PushVideo`), `PooledWorkQueue<T>` in every sink.
- **Clocks**: `StopwatchClock` for internal; hardware clocks auto-registered at `Hardware` priority when an `IClockCapableEndpoint` is registered; `SetClock` installs an `Override`-priority entry.

---

## Bugs & correctness issues

| # | File : lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| B1 | `FFmpegDecoder.cs:550–560` | High | Seek flush is `TryWrite` best-effort; if packet queue is full it is dropped. Decoders filter stale packets but `ApplySeekEpoch` only fires on the first non-stale packet seen, so `avcodec_flush_buffers` may be delayed arbitrarily until the next natural packet arrives on that stream. On infrequent-keyframe video streams this yields visible rewind then a long black. | Before `TryWrite(flush)`, drain one packet via `TryRead` to make space, or use `WriteAsync(..., quickCT)` with a 50 ms timeout. |
| B2 | `FFmpegDecoder.cs:766–770` | Medium | `_fmt->pb = null` is set only when `_avioCtx != null`. Good. But `avformat_close_input` can still run while another thread holds `_formatIoGate.ReadLock` from a late `TryReadNextPacket`. Dispose does not enter the write lock before closing. | Enter the write lock around `avformat_close_input`, or cancel + join demux before calling it (the current order relies on demux having ack'd cancel, which may not have happened after only `ch.Dispose()` — those don't cancel demux). |
| B3 | `FFmpegDecoder.cs:746–756` | Medium | Disposal order calls `ch.Dispose()` *before* joining the demux task. Channels close their packet-queue readers, but the demux writer is in `WriteAsync` — it will get `ChannelClosedException` and return. So this is fine, *but* the demux’s `_formatIoGate.EnterReadLock` may still be in flight from a prior packet; dispose then proceeds to `avformat_close_input` after only a 3 s timeout. | Cancel CTS first (already done), then `_demuxTask.Wait`, *then* dispose channels, *then* close format. |
| B4 | `FFmpegVideoChannel.cs:225, 503` | Medium | `foreach (var s in _subs) s.Flush()` happens without snapshot; `_subs` is `ImmutableArray` so the read is tear-free, but a concurrent `Subscribe` or `Dispose` can race: the subscriber's `Flush` may run on a sub that's already `_disposed=true`. `FFmpegVideoSubscription.Flush` reads from `_reader` which has been completed — safe, but the publisher’s `TryPublish` vs Dispose race can cause double-Dispose of `MemoryOwner` (see B7). | Snapshot `var subs = _subs;` locally; keep using it. Document that subs self-remove via `RemoveSubscription` under `_subsLock`. |
| B5 | `FFmpegVideoChannel.cs:658–681` | High | `PublishToSubscribers` retains `subs.Length-1` additional refs then disposes one ref per failed `TryPublish`. If `TryPublish` throws (e.g. `OperationCanceledException` from a `Wait`-policy sub), the ref is never released. The `Wait` branch in `FFmpegVideoSubscription.TryPublish` catches the exception and returns false — good — but a `ChannelClosedException` out of `WriteAsync` *not inside* the catch (the `.AsTask().GetAwaiter().GetResult()` path) will propagate, leaving refs leaked. | Wrap the entire `TryPublish` body in a try/catch that returns false on any exception, with `MemoryOwner?.Dispose()` by the caller. |
| B6 | `FFmpegVideoSubscription.cs:84–100` | Medium | `DropOldest` overflow policy evicts *someone else’s* frame (the ring's oldest) and disposes `evicted.MemoryOwner`. But that memory owner is ref-counted; we are one of N subscribers who received a retain. Disposing once decrements refcount by 1 — fine only because each subscription owns exactly one ref of that frame. Still correct, but a comment belongs here. | Document the ref-count invariant; add an assertion in Debug builds. |
| B7 | `NDIAVSink.cs:1103–1188` | Medium | Async send pin/retention works only if every exit path is covered. The `catch (NotSupportedException)` at L1190 and the general `catch` at L1208 both leave `handedOff=false`, so the `finally` returns `pf.Buffer`. But the `ReleasePendingAsyncVideo()` at L1168 has already returned the *previous* frame — if the current send throws right after that call succeeds, we’ve asked NDI to retain a buffer we immediately recycled (we hit the `sendHandle.Free()` in the inner catch — good). The branch at L1093 (`MemoryMarshal.TryGetArray` fail) leaks `scratchBuffer` because the inner `finally` is reached with `handedOff=false`, so scratchBuffer *is* returned. OK. But the `continue;` at L1096 skips the `Interlocked.Increment(ref _videoFormatDrops)` logic (it actually has it L1095 — good). All paths look correct, but it is extremely hard to audit. | Refactor the send into a try-owned `VideoSendScope` RAII helper that takes ownership of `(pf.Buffer, scratch, tempOwner)` and releases based on an explicit `Commit()` call. |
| B8 | `NDIAVSink.cs:81, 441, 725` | Low | Comment claims "audio is single-threaded (only AudioWriteLoop calls SendAudio) so needs no lock" — true today, but `Dispose` → `_sender.FlushAsync()` is on the video lock; if a native NDI sender requires serialization of *any* two calls (which §13 does NOT actually guarantee for all SDK versions), sending audio concurrently with a flush-on-stop from another thread could be racy. | Guard all sender calls with `_videoSendLock` (rename to `_senderLock`) or verify the SDK contract. |
| B9 | `StreamAvioContext.cs:110–113, 145–148` | Medium | Both callbacks silently swallow exceptions and return EOF/-1. Network errors become indistinguishable from clean EOF. | Propagate via an `OnError` event on the owning decoder; return `AVERROR(EIO)` for non-EOF reads; log at Warning. |
| B10 | `FFmpegAudioChannel.cs:224–236` | Medium | On `WriteAsync` failure (not cancellation — e.g. `ChannelClosedException`), the `converted.Value.Buffer` is returned to pool *only* on `OperationCanceledException`. Other exceptions propagate and leak the rented chunk. | Add a `catch (ChannelClosedException) { ReturnChunkToPool(...); return false; }`. |
| B11 | `FFmpegAudioChannel.cs:245–249` | Low | Frame channel-count mismatch drops the frame but does not unref `_frame`. The caller unrefs at L219 (`ConvertFrame()` returns then caller `av_frame_unref`) — correct. False alarm; kept for the record. | — |
| B12 | `AVRouter.cs:1173–1175, 1193–1195` | Medium | `_pushVideoPending.TryGetValue` + dispose + indexer-write is a two-step write under no lock. A concurrent `RemoveRouteInternal` (`TryRemove`) can run between the dispose and the indexer-write, leaving a stale `VideoFrame` entry in the dictionary that `RemoveRouteInternal` already passed. Entry then leaks at Dispose time (covered by the disposal loop at L624–626, so net: no leak, but the frame is held past the route’s lifetime). | Use `_pushVideoPending.AddOrUpdate` with a `Dispose(old)` lambda; or hold `_lock` during the swap; or perform the swap only if the route is still alive after. |
| B13 | `AVRouter.cs:471–490` | Low | `SetInputVolume/TimeOffset/Enabled` write plain fields without volatile. RT readers in `Fill` read them via `inp.Enabled`, `inp.Volume` — non-volatile. On x86 this works; on ARM you may briefly observe a stale value. | `Volatile.Read` / `Volatile.Write`, or mark the fields `volatile` (not allowed for `float` — use `Interlocked.Exchange` on `int` bit-casts or `volatile` wrapper). |
| B14 | `AVRouter.cs:409` | Low | `Clock => _resolvedClock ?? _internalClock` is a `volatile` read, but `_resolvedClock` is swapped under `_clockLock` while registry mutations happen — a single read may observe the “between-states” value. | Fine as-is; documented in comments elsewhere. No change needed. |
| B15 | `AVRouter.cs:1443–1445` | Medium | The `ReferenceEquals(_lastPresentedFrame.Value.MemoryOwner, candidate.MemoryOwner)` check avoids disposing when the *same owner* is reused. But `RefCountedVideoBuffer` is always the unique wrapper per decoded frame; two successive `TryRead`s will never share an owner. So the branch is always taken, meaning we *always* dispose the last frame before replacing — fine. However when `_pendingFrame` is promoted to `candidate` and the *same* frame ends up re-presented across multiple render-loop ticks via the `_lastPresentedFrame.HasValue` re-present path at L1385/1420/1453, we never bump the refcount — we return a `VideoFrame` struct *by value* to the renderer, which does not dispose it. This is correct, but makes it easy for a consumer to accidentally dispose and invalidate the cache. Add a safer wrapper. | Expose `frame.AsBorrowed()` vs `frame.AsOwned()` distinction, or switch `TryPresentNext` to return a `VideoFrameHandle` with explicit ownership semantics. |
| B16 | `AVRouter.cs:1383–1389` | Medium | When `TryRead` fails, we re-present the last frame *without* a PTS/drift check. So if the renderer runs faster than the content FPS (SDL3 vsync at 144 Hz vs 30 fps content), we correctly reuse the upload. But if the subscription is starved *and* the drift tracker would have advanced, we never integrate the error — the PTS tracker stalls. Minor, usually swamped by the tick-rate re-present of fresh frames. | Call `_drift.IntegrateError` with the last-presented PTS when re-presenting. |
| B17 | `PortAudioSink.cs:270` | Medium | `Pa_WriteStream` return is discarded. If it returns `paOutputUnderflowed`/`paStreamIsStopped`, we never surface it. | Check `err`; log once per burst. |
| B18 | `PortAudioSink.cs:186–196` | Low | `StopAsync` joins the write thread (3 s) *before* calling `Pa_StopStream`. If the thread is blocked inside `Pa_WriteStream`, join will time out — then `Pa_StopStream` fires with a thread still pounding `Pa_WriteStream`. Should call `Pa_AbortStream` to break the write. | On join-timeout, `Pa_AbortStream(_stream)` then retry join. |
| B19 | `MediaPlayer.cs:210–215, 391–392` | Medium | `RemoveEndpoint`/`AddEndpoint` call `endpoint.StartAsync()/StopAsync().GetAwaiter().GetResult()` on the current thread. This can deadlock if called from a UI thread (Avalonia). | Provide `AddEndpointAsync` / `RemoveEndpointAsync` and deprecate the sync ones. |
| B20 | `MediaPlayer.cs:383` | Low | `_isRunning` is a field declared *after* methods that reference it; C# allows this but the field isn’t initialized to match the state machine (e.g. `Paused` keeps `_isRunning=false` but the decoder is still running). `IsPlaying => _decoderStarted && !_disposed` contradicts `_isRunning`. Pick one. | Fold into the `_state` enum; remove `_isRunning`. |
| B21 | `FFmpegDecoder.cs:527` | Low | `(long)(position.TotalSeconds * AV_TIME_BASE)` loses microsecond precision by going through `double`. `position.Ticks / (TimeSpan.TicksPerSecond / AV_TIME_BASE)` preserves it. | Small but easy. |
| B22 | `FFmpegVideoChannel.cs:631` | Low | HW-frame transfer: if `av_hwframe_transfer_data` fails (returns < 0) we silently fall through to convert the raw `_frame` (still in GPU memory) — `sws_scale` on a GPU pointer will crash or produce garbage. | Check return value; if < 0, skip the frame and log once. |

---

## Concurrency / lifetime / resource issues

1. **`FFmpegDecoder.Dispose` sync wait from async code** — documented risk, but no `DisposeAsync`/`StopAsync` alternative is offered. Add `public async Task StopAsync()` that awaits the demux task cooperatively.
2. **`NDIAVSink` thread start/stop cycle state leak** — `_videoThread`, `_audioThread`, and `_cts` fields are not cleared on stop, so a future `StartAsync` creates fresh threads but the stale references linger until next start. Not a correctness bug but confusing in diagnostics.
3. **`AVRouter.PushThreadLoop` + `PushVideoThreadLoop` start order** — `_running` is set under `_lock` then threads start. Threads read `_running` *unvolatile*; field is `volatile`, OK.
4. **`_scratchBuffers` in `AVRouter`** — per endpoint one reused `float[]`; if frame count grows between calls we allocate a new one without returning the old to pool. Over long sessions with varying PortAudio buffer sizes, memory grows unboundedly (bounded in practice by max framesPerBuffer).
5. **`FFmpegVideoChannel.Subscribe` after `Dispose`** — guarded by `ObjectDisposedException.ThrowIf` (L446). Good.
6. **Clone sinks' parent disposal** — No `IClonable` contract; `SDL3VideoOutput` tracks `_clones` manually; parent Dispose should cascade but isn’t consistently wired between `SDL3VideoCloneSink` and `AvaloniaOpenGlVideoCloneSink`. Worth standardizing.
7. **`PooledWorkQueue.Dispose`** just disposes the semaphore — does NOT drain the queue. Items are lost silently. Callers (e.g. `NDIAVSink`, `PortAudioSink`) call `Drain` first — good — but the class itself invites misuse.
8. **`FFmpegDecoder._queues` dictionary** never removed from after seek; not an issue since streams are stable for the decoder's lifetime, but documenting prevents future refactor breakage.
9. **Packet pool unbounded growth** — `PacketPool` is a `ConcurrentQueue<EncodedPacket>` with no max cap; in a slow-consumer scenario the pool size equals the peak in-flight count (bounded by `PacketQueueDepth × stream count`), so bounded in practice.
10. **`FFmpegVideoChannel._defaultSub` double-dispose on Dispose** — `subsSnap` already includes `_defaultSub`; we then null it before disposing. If a consumer is still calling `FillBuffer` when Dispose runs, it may reach `EnsureDefaultSubscription` under `_subsLock` after `_subs` was emptied — it creates a fresh subscription on a disposed channel. Minor.
11. **Events raised via `ThreadPool.QueueUserWorkItem`** — good for RT-safety. But several handlers (e.g. `videoOutput.WindowClosed += () => cts.Cancel()` in VideoPlayer test app) expect to run fast; if a subscriber blocks, the pool thread is held. Document the threading contract for each event.

---

## Performance / allocation hotspots

| Location | Issue | Suggestion |
|---|---|---|
| `FFmpegVideoChannel.ConvertFrame` L232–390 | Rents `_swsBufSize` from `ArrayPool<byte>.Shared` per frame (60 fps × 4 K ≈ 8 MB/frame → heavy pool churn). | Per-channel dedicated pool of fixed-size buffers; or move to `Memory<byte>` backed by a ring. |
| `NDIAVSink.AudioWriteLoop` L1237–1345 | Rents planar buffer from `ArrayPool<float>.Shared` once; deinterleave inner loop is bounds-checked; `fixed (float*)` around `SendAudio` is fine. Could use SIMD. | Unroll by 2/4 channels; `Vector<float>` gather-scatter has no primitive so per-channel linear copy is fastest. |
| `AVRouter.PushAudioTick` L1017–1114 | Rents `destBuf` + (when channel-mapped) `mappedBuf` per endpoint per tick. At 100 Hz × N endpoints → 100·N rents/sec. | Cache per-endpoint `float[]` scratches (already done for src via `_scratchBuffers`); extend to `dest` & `mapped`. |
| `AVRouter.VideoPresentCallbackForEndpoint` | Allocates nothing per call (`VideoFrame` is a struct). Good. | — |
| `FFmpegDemuxWorker.WritePacketAsync` | `await WriteAsync` allocates state machine per packet; high-bitrate streams (10 M packets/hr) → measurable. | Use a synchronous `TryWrite`-first fast path before the async await. |
| `NDIAVSink.ReceiveFrame` L494 | `frame.Data.Span[..bytes].CopyTo(dst.AsSpan(0, bytes))` — full frame copy on the producer thread. For 4K60 passthrough this is ~500 MB/s. | If source & target pixel formats match and the source buffer is already ref-counted + pooled, retain the pooled buffer instead of copying (extend `VideoFrame` to optionally hand ownership to sink). |
| `FFmpegVideoSubscription.TryPublish` Wait path L57–66 | `.AsTask().GetAwaiter().GetResult()` blocks the decode thread for N ms. Slow pull endpoint becomes the decoder's pacing source. | Use a bounded ring with `Channel.CreateUnboundedPrioritized` + drop-oldest; or run per-subscriber in a tiny worker (adds latency). |
| `AVRouter` drift smoothing EMA | Holds `_driftEmaLock` for UI diagnostic; called from diagnostics thread only. OK. | — |
| `TryConvertI210ToRgbaManaged` L862–927 | Per-pixel scalar loop with 3 multiplies; for 1080p ~2 MP → ~6 Mops/frame. Only used as FFmpeg-unavailable fallback. | Fine. Cache `TryConvertI210ToRgbaFfmpeg` scratch arrays (already done at L990–994). |
| `SDL3VideoOutput` catch-up (`_maxCatchupPullsPerRender = 6`) | At 144 Hz render vs 30 fps content, we’ll always hit this path and drop 5 frames per cycle per render loop — correct, but the metrics need explicit `DroppedFrames` exposure, already present. | — |
| `ChannelRouteMap.Auto` on route creation | Runs once per route at registration; not a hot path. | — |
| `FFmpegDecoder.DiscoverStreams` `AttachedPic` skip L345 | Fine. Log at Debug. | — |

---

## API ergonomics – current pain points

### Snippet: SimplePlayer (audio-only) — 385 LoC

```csharp
using var engine = new PortAudioEngine();
engine.Initialize();
// ... pick API, pick device ~30 LoC ...

decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions { EnableVideo = false });
var audioChannel = decoder.AudioChannels[0];
var srcFmt = audioChannel.SourceFormat;

int outChannels = Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, 2));
var hwFmt   = new AudioFormat(srcFmt.SampleRate, outChannels);
var routeMap = BuildRouteMap(srcFmt.Channels, outChannels);   // helper defined in Program.cs

using var output = new PortAudioOutput();
output.Open(device, hwFmt, framesPerBuffer: 0);

using var router = new AVRouter();
var epId   = router.RegisterEndpoint(output);
router.SetClock(output.Clock);               // redundant — RegisterEndpoint auto-registers at Hardware priority
var inputId = router.RegisterAudioInput(audioChannel);
router.CreateRoute(inputId, epId, new AudioRouteOptions { ChannelMap = routeMap });

decoder.Start();
await output.StartAsync();
await router.StartAsync();
```

### Pain points
- Four layers (`decoder`, `router`, `output`, `channel`) with four lifetimes to start/stop in the right order, and six objects to pass around.
- `BuildRouteMap` is duplicated verbatim in `SimplePlayer`, `MultiOutputPlayer`, `VideoPlayer`, `NDISender`, `NDIAutoPlayer`. It's boilerplate in every test app because there's no `ChannelRouteMap.AutoStereoDownmix` helper.
- `output.Open(device, hwFmt, framesPerBuffer: 0)` — a third argument whose default value is 0 = "PA chooses" that should be the first-class default.
- `SetClock(output.Clock)` is redundant with auto-registration; tests do it "just in case".
- Drain detection (SimplePlayer L149–177, 265–278) is *hand-rolled every time*. Needs to be `player.WaitForCompletionAsync(token)`.
- NDIAVSink + audio output + video output in VideoPlayer requires **three separate start orderings** (decoder → endpoint.StartAsync × N → router.StartAsync) that must match a precise sequence; the audio-warmup gate at L537–555 is a workaround for a timing bug that should be fixed inside the router.
- YUV range/matrix prompting in VideoPlayer is because the sink cannot auto-resolve from `IVideoColorMatrixHint`; the router should propagate the hint to the output.
- `BypassVideoPtsScheduling` (`VideoLiveMode` in the doc) is on the router as a global boolean; per-endpoint it should be per-route: "render as fast as possible for monitor, schedule for NDI".

---

## Proposed simplified API / auto-routing design

### Goal: SimplePlayer should fit in 10–15 lines. VideoPlayer in 20.

```csharp
// Simplest possible: pick a device and play.
using var player = MediaPlayer.Create()
    .WithAudioOutput(PortAudioEngine.Default)          // picks default device
    .Build();
await player.OpenAsync("song.mp3");
await player.PlayAsync();
await player.WaitForCompletionAsync();                 // drain-aware
```

```csharp
// SimplePlayer equivalent with explicit device choice:
using var engine = PortAudioEngine.Initialize();
var device = engine.GetDefaultOutputDevice(preferredApi: "PulseAudio");

using var player = MediaPlayer.Create()
    .WithAudioOutput(device)                             // format negotiated internally
    .WithVolumeControl()
    .WithTransportKeyboard()                             // wires Space/arrows to player.*
    .Build();

await player.OpenAndPlayAsync(path);
await player.WaitForCompletionAsync(cts.Token);
```

```csharp
// MultiOutputPlayer equivalent — fan-out auto inferred.
using var player = MediaPlayer.Create()
    .WithAudioOutput(primary)
    .WithAudioSink(secondary)                           // auto-creates PortAudioSink in matching format
    .Build();
await player.OpenAndPlayAsync(path);
```

```csharp
// VideoPlayer + NDI:
using var player = MediaPlayer.Create()
    .WithVideoOutput(SDL3VideoOutput.ForWindow(title: "Preview"))
    .WithAudioOutput(engine.Default)
    .WithNDIOutput("MFPlayer NDI", NDIEndpointPreset.LowLatency)   // adds IAVEndpoint for A+V
    .Build();
await player.OpenAndPlayAsync(path);
```

### Concrete facade

```csharp
public sealed class MediaPlayerBuilder
{
    public MediaPlayerBuilder WithAudioOutput(AudioDeviceInfo device, AudioFormat? format = null, int framesPerBuffer = 0);
    public MediaPlayerBuilder WithAudioOutput(PortAudioEngine engine); // default device
    public MediaPlayerBuilder WithAudioSink(AudioDeviceInfo device, AudioFormat? format = null);
    public MediaPlayerBuilder WithVideoOutput(SDL3VideoOutput preOpened);
    public MediaPlayerBuilder WithVideoOutput(AvaloniaOpenGlVideoOutput preOpened);
    public MediaPlayerBuilder WithNDIOutput(string senderName, NDIEndpointPreset preset = NDIEndpointPreset.Balanced);
    public MediaPlayerBuilder WithClock(IMediaClock clock, ClockPriority priority = ClockPriority.External);
    public MediaPlayerBuilder WithDecoderOptions(FFmpegDecoderOptions opts);
    public MediaPlayerBuilder WithRouterOptions(AVRouterOptions opts);
    public MediaPlayerBuilder OnError(Action<PlaybackFailedEventArgs> handler);
    public MediaPlayer Build();   // opens endpoints, registers with router
}
```

### Required additions to make this work

1. **`AudioFormat.NegotiateFor(AudioChannel src, AudioDeviceInfo dev, int capChannels = 2)`** returning `(AudioFormat hw, ChannelRouteMap map)`. Replaces hand-rolled logic in 5 test apps.
2. **`ChannelRouteMap.AutoStereoDownmix(int srcCh, int dstCh)`** — encapsulates the mono→stereo expansion logic.
3. **`MediaPlayer.WaitForCompletionAsync(CancellationToken ct)`** — handles EOF + drain grace, surfaces as a Task. Replaces the 30-line "sourceEnded + drain grace" state machine in every test app.
4. **Automatic audio pre-roll warmup inside router.** The VideoPlayer’s hand-rolled "wait for audio.BufferAvailable > 0 before starting video" should move into `AVRouter.StartAsync` when an `IPullVideoEndpoint` and an audio input coexist. Option: `AVRouterOptions.WaitForAudioPrerollMs = 1000`.
5. **Per-route `LiveMode`** replacing the global `BypassVideoPtsScheduling`, so you can have SDL3 in scheduled mode and NDI in live mode (or vice versa) on the same router.
6. **Endpoint format propagation** — when an `IPullAudioEndpoint` is registered, the router should push its `EndpointFormat` back to an `IAudioEndpointFormatHint` on the decoder's audio channels, so channels can skip SWR work they don't need. Today the decoder always outputs FLT at source rate and the router resamples — wasteful when the output matches the source.
7. **`IVideoColorMatrixHint` auto-propagation** — when video route is created, router should read the channel’s suggested matrix/range and push it to the output unless the user explicitly set one. Removes the YUV prompt in VideoPlayer L264–288.
8. **Sensible default router tick cadence** — currently hard-coded `TickCadence=10ms`, but VideoPlayer has special code to tighten it to 5ms when NDI-LowLatency is chosen. This should be auto-derived from registered endpoints: pick `min(endpoint.NominalTickCadence)`.
9. **Drop-or-wait policy on video fan-out** should default based on endpoint kind, as it already does — but expose it on `VideoRouteOptions.OverflowPolicy` for advanced users.

---

## Consistency / naming / style

- **Namespaces** are tidy: `S.Media.Core` / `S.Media.FFmpeg` / `S.Media.PortAudio` / `S.Media.NDI` etc. Good.
- Some logger names use inline `GetLogger(nameof(X))` while others use module-level loggers; all fine. A few files declare `private static readonly ILogger Log` (good) but `FFmpegDecoder` uses an **instance field** `_log` initialized from `FFmpegLogging.GetLogger(nameof(FFmpegDecoder))` — unnecessary and prevents the JIT from optimizing calls. Make it static.
- **Dispose vs. DisposeAsync**: only `AVRouter` exposes both. `MediaPlayer`, `FFmpegDecoder`, `PortAudioSink`, `NDIAVSink`, `SDL3VideoOutput` should also implement `IAsyncDisposable` so callers are not forced to block.
- **Exception types**: `InvalidOperationException` used liberally for anything from "route not registered" to "codec failed" to "Pa_OpenStream failed". Add a small hierarchy: `MediaOpenException`, `MediaDecodeException`, `MediaRoutingException`, `MediaDeviceException`.
- **`FFmpegDecoderOptions`** is `sealed class` with init-only; `AVRouterOptions` is `record`. Unify.
- **Ownership**: `PortAudioOutput` requires separate `Open()` after `new PortAudioOutput()`. `SDL3VideoOutput` same. Should be single-step factory: `PortAudioOutput.Create(device, format)`.
- **Error events vs. exceptions**: `MediaPlayer` has `PlaybackFailed` but `FFmpegDecoder` throws from `Open`, has no error event for the demux thread (uses `_log.LogError` only).
- **Event threading**: undocumented. Some events fire on `ThreadPool`, some on RT threads (e.g. `BufferUnderrun`). Document and standardize on `ThreadPool` for user-visible events.
- **`Volume` lives on `IAudioChannel`** *and* on the router via `SetInputVolume`. Pick one. The channel-level one is legacy from the pre-router model.
- **`FFmpegVideoChannel` is `internal sealed`** but returned from `FFmpegDecoder.VideoChannels` as `IVideoChannel` — good. But `FFmpegAudioChannel.Volume` setter mutates a field read by the router — this is dead code, volume goes through `SetInputVolume` now. Remove the `Volume` setter semantics from the interface.
- **`BypassVideoPtsScheduling`** in `AVRouter.cs:503` is named inconsistently with the plan doc (`VideoLiveMode`). Pick one name.

---

## Refactor roadmap

Ordered, small-to-large; reconciled with `Doc/AVMixer-Refactor-Plan.md` (Phase 1 complete) and `Doc/Clone-Sinks.md` (which still references the removed `avMixer` API and should be updated first).

### Tier 0 — Doc sync (½ day)
1. Update `Doc/Clone-Sinks.md` to use `AVRouter.RegisterEndpoint` / `CreateRoute` (current example shows the removed `avMixer.RegisterVideoSink`).
2. Remove the obsolete `MediaPlayer.PlaybackEnded` event (`MediaPlayer.cs:118`).

### Tier 1 — Bug fixes, no API change (2–3 days)
3. B1 Seek flush: drain-then-write with timeout.
4. B9 StreamAvioContext: propagate errors via `OnError` event.
5. B15/B16 Video-frame ownership: add `VideoFrameHandle` with explicit retain/release.
6. B12 Push-video pending atomicity fix.
7. B17/B18 PortAudioSink error handling + abort-on-stop.
8. B5 PublishToSubscribers full try/catch.
9. Static-ify `FFmpegDecoder._log`.

### Tier 2 — Ergonomic helpers (1 week)
10. `AudioFormat.NegotiateFor`, `ChannelRouteMap.AutoStereoDownmix`.
11. `MediaPlayer.WaitForCompletionAsync`.
12. `MediaPlayer` → `IAsyncDisposable`; replace sync Start/Stop with async equivalents.
13. `FFmpegDecoder.StopAsync()` that awaits the demux task.
14. Consolidate exception types (`MediaOpenException` etc).
15. `IAudioEndpointFormatHint` propagation so decoder skips SWR when unnecessary.

### Tier 3 — Builder API (2 weeks)
16. `MediaPlayerBuilder` with `WithAudioOutput`, `WithAudioSink`, `WithVideoOutput`, `WithNDIOutput`, `WithClock`.
17. `SDL3VideoOutput.ForWindow(...)` / `PortAudioOutput.Create(...)` one-step factories.
18. Auto-propagate `IVideoColorMatrixHint` from channel → output.
19. Auto audio-preroll inside `AVRouter.StartAsync` (replaces VideoPlayer L537–555).
20. Migrate all test apps to the builder API; measure LoC reduction.

### Tier 4 — Per-route & format negotiation (2 weeks)
21. Per-route `VideoOverflowPolicy` exposed on `VideoRouteOptions`.
22. Per-route `LiveMode` replacing the global `BypassVideoPtsScheduling`.
23. Endpoint format-change events → route re-validation (Phase 1 doc open Q3).
24. Per-endpoint peak metering (already done for inputs; extend to outputs).

### Tier 5 — Phase 2 from the refactor plan (timeline)
25. `ITimeline`/`TimelineItem`, transport on top of the builder API.
26. Gapless playback via pre-opened next-track decoders.
27. Playlist / crossfade as route gain automation.

### Tier 6 — Performance (as needed)
28. Per-channel `VideoFrame` buffer pool (fixed-size, LOH-aware) to replace `ArrayPool<byte>.Shared` churn at 4K60.
29. Zero-copy fast-path for "source format == sink format" in NDIAVSink and SDL3 clones.
30. SIMD I210→UYVY converter (currently scalar, L815–860).

---

## Nice-to-haves / future work

- **Audio effects chain** per route (EQ, limiter, soft-clip, loudness normalization). Today you’d need a custom `IAudioEndpoint` wrapper.
- **Recording endpoint** (file writer via FFmpeg mux) — an `IAVEndpoint` that encodes and writes to mp4/mkv. Natural extension of the current endpoint model.
- **Gapless playback** via decoder pre-warm: second `FFmpegDecoder` opens during last 5 s, route switch at EOF.
- **Subtitles / closed captions** — no interface today; the framework currently skips non-audio/video streams (`FFmpegDecoder.cs:404–406`). Add `ISubtitleChannel` and a text render endpoint.
- **HDR/PQ path** — `PixelFormat` enum lacks 10-bit HDR transfer functions; currently Yuv422p10 is converted to UYVY 8-bit before NDI. Add `ColorSpace` and `TransferCharacteristics` to `VideoFormat`.
- **PTP/genlock clock implementation** — `IMediaClock` is an abstraction; a real PTP clock would plug in at `ClockPriority.External` and drive both router and NDI.
- **Audio loudness metering (R128)** beyond peak.
- **Seek-bar UX helpers** — `MediaPlayer.SeekAsync(TimeSpan)` that returns only once the first post-seek frame is rendered (wall-clock perceivable).
- **`PortAudioSink` format-change on underrun** — today it silently drops. Consider a `DropNoisy` diagnostic.
- **Unit test scaffold** — no tests in tree. A minimal set around `ChannelRouteMap.Auto`, `DriftCorrector.CorrectFrameCount`, and the seek-epoch filter would pay back immediately.
- **Deterministic teardown ordering helper** — `MediaPlayer.DisposeAsync` should orchestrate: stop router → stop endpoints (parallel) → stop decoder → dispose all — removing the “start/stop in the right order” footgun baked into every test app.

---

# Addendum — NDI receive path

*Addendum review date: 2026-04-22. Read-only inspection of `NDI/S.Media.NDI/*` (NDISource, NDIAVChannel, NDIAudio/VideoChannel, NDIClock, NDIAvTimingContext, presets, README) plus `NDI/NDILib/NDIWrappers.cs` and the consumer apps `Test/MFPlayer.NDIPlayer/Program.cs`, `Test/MFPlayer.NDIAutoPlayer/Program.cs`. The original review above covered `NDIAVSink` (the output endpoint) only; this addendum covers the NDI source / receive side.*

## Executive summary (NDI-input top findings)

1. **Capture-thread vs. framesync/receiver teardown race.** `NDISource.Dispose` (NDISource.cs L642–652) cancels the watchdog CTS, then calls `AudioChannel.Dispose()` / `VideoChannel.Dispose()` which only `Join` the capture thread with a **2 s timeout** and *then* disposes `_frameSync` and `_receiver`. If the join times out, the capture thread is still inside `NDIFrameSync.CaptureVideo/CaptureAudio` when the native handle is destroyed → classic native use-after-free.
2. **Watchdog reconnect races capture.** `NDISource.TryReconnect` calls `_receiver.Connect(found.Value)` (L496, L505) from the watch thread while `NDIAudio/VideoChannel.CaptureLoop` is simultaneously calling `_frameSync.CaptureVideo/Audio` on the same receiver. No cross-instance lock exists. The SDK documents thread-safety *within* `recv_*` but frame-sync semantics under live `recv_connect` are not guaranteed.
3. **`_cts` in NDIAudio/VideoChannel is a latched singleton** (NDIAudioChannel.cs L38, NDIVideoChannel.cs L27): created in the field initializer, cancelled in `Dispose`, never replaced. `StartCapture` can be called post-dispose and will spawn a new thread using an already-cancelled token — the thread exits immediately on the first `token.IsCancellationRequested` check, silently.
4. **NDIClock is written by whichever channel got the frame last.** Both `NDIAudioChannel.CaptureLoop` L220 and `NDIVideoChannel.CaptureLoop` L168 call `_clock.UpdateFromFrame(frame.Timestamp)` unconditionally. Senders stamp audio/video timestamps independently, so `NDIClock.Position` stutters non-monotonically by a few ms each tick. No priority (video-preferred / audio-preferred / first-writer).
5. **`NDIClock.SampleRate` is hard-coded to 48000** regardless of `NDISourceOptions.SampleRate` (NDISource.cs L220 passes no argument; NDIClock default at L42). Consumers reading `Clock.SampleRate` on a 44.1 kHz source get the wrong value.
6. **Video path has a synthetic-PTS fallback; audio path does not.** `NDIVideoChannel` has a full stopwatch fallback (L57–70, L196–223). `NDIAudioChannel` only *skips* the clock update when timestamps are undefined — its own `Position` stays monotonic via `_framesConsumed / SampleRate` (L65), but the **shared `NDIClock` stops advancing during audio-only + undefined-timestamps streams** even though audio is flowing.
7. **Framesync uses V2 video capture API.** `NDIFrameSync.CaptureVideo` (NDIWrappers.cs L720) wraps `NDIlib_framesync_capture_video` (V2). 10-bit (P216/V210), HDR, and extended-color streams fall into `TryMapFourCc`'s reject path (NDIVideoChannel.cs L327–331, L345–371) and are dropped entirely.
8. **Per-frame CPU copy + ArrayPool churn.** Every captured UYVY frame (1920×1080×2 = ~4 MB) is `Marshal.Copy`'d row-by-row into an `ArrayPool<byte>.Shared` rental, wrapped in an `NDIVideoFrameOwner`, and pushed into the ring. At 60 fps that is **~240 MB/s** of managed copies purely to decouple from the NDI internal pool. Could be avoided by keeping the framesync frame alive through the pipeline (requires the `VideoFrameHandle` ownership redesign flagged as B15/B16 in the main review).
9. **`NDISource.Stop()` does not stop capture** — it only stops the clock (NDISource.cs L386–389). Capture threads run until `Dispose`. The XML comment admits this; the method name strongly implies the opposite.
10. **`NDIAVChannel.Open` throws if the source has no audio** (NDIAVChannel.cs L33–34). NDI sources can legitimately be video-only (NDI|HX video feeds, PTZ previews); the facade refuses them, forcing consumers back to raw `NDISource`.

---

## Architecture of the NDI receive path

```
                       ┌────────────────────────────────────────┐
NDIDiscoveredSource    │  NDISource (per-source lifecycle)      │
        │              │  NDIReceiver (recv_create_v3)          │
        ▼              │   Connect(src)                         │
  NDISource.Open ──────┼─▶ NDIFrameSync (framesync_create)      │
                       │   ├─ NDIAudioChannel  ── capture thread│──▶ IAudioChannel (pull)
                       │   │    ring<float[]> (DropOldest)      │
                       │   ├─ NDIVideoChannel  ── capture thread│──▶ IVideoChannel (pull)
                       │   │    ring<VideoFrame> (manual)       │
                       │   └─ NDIClock (UpdateFromFrame: both)  │──▶ IMediaClock   ⚠ not auto-registered
                       │  WatchLoop (auto-reconnect)            │
                       └────────────────────────────────────────┘
                                         ▼
                       AVRouter.RegisterAudioInput / RegisterVideoInput
                       (test apps call router.SetClock(output.Clock) — NDIClock is dropped)
```

- **`NDISource`** is analogous to `FFmpegDecoder` for the live NDI pull model: owns the native receiver + framesync, creates one channel per media type, runs an optional watchdog for auto-reconnect, and exposes `State`/`StateChanged` (L165–172). Factory is `Open(NDIDiscoveredSource)` or `OpenByNameAsync(string)` (L200, L268).
- **Channel model** mirrors FFmpeg: `NDIAudioChannel : IAudioChannel`, `NDIVideoChannel : IVideoChannel, IVideoColorMatrixHint`. Pull-only (`FillBuffer`), `CanSeek = false`, `Seek` no-op.
- **Frame-sync only.** The code uses `NDIFrameSync.CaptureAudio/Video` exclusively. Raw `NDIlib_recv_capture_v3` is wrapped in `NDIReceiver.Capture` / `NDICaptureScope` but never called from the receive channels. Frame-sync gives automatic audio resampling and progressive video — that's why `SourceFormat` is stable.
- **`NDIClock`** (derived from `MediaClockBase`) tracks the last-seen NDI frame timestamp and interpolates sub-tick position from a `Stopwatch`. **Never registered with the router** by either test app — both do `router.SetClock(output.Clock)` (NDIPlayer L205, NDIAutoPlayer L289), so PortAudio's hardware clock is authoritative and `NDIClock` is effectively dead weight.
- **`NDIAvTimingContext`** is a *sink-side* helper used only by `NDIAVSink`; unrelated to the receive path despite living in the same namespace.
- **Preset layering:**
  - `NDILatencyPreset` = a single queue-depth int.
  - `NDIEndpointPreset` = user-facing dial (Safe/Balanced/LowLatency/UltraLowLatency).
  - `NDIPlaybackProfile.For(preset)` derives **output-side** knobs.
  - `NDISourceOptions.ForPreset(preset)` derives **source-side** knobs.
  - `NDIVideoPresetOptions` / `NDIAudioPresetOptions` are **only consumed by `NDIAVSink`**; the receive channels ignore them. This asymmetry is confusing.

---

## Bugs & correctness issues (NDI-input)

| # | File : lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| N1 | `NDISource.cs:642–652` | **High** | Dispose tears down channels with a 2 s `Thread.Join` timeout, then unconditionally disposes `_frameSync`/`_receiver`. Blocked capture thread → native handle freed underneath → use-after-free. | Loop-join until thread exits, or take a top-level `_sessionGate` and hold it during capture + for `_frameSync.Dispose()`. |
| N2 | `NDISource.cs:496, 505` | **High** | `_receiver.Connect(...)` in `WatchLoop` races against both channels' `_frameSync.Capture*` on the same receiver. No cross-instance lock. | Serialize `recv_connect` with all frame-sync captures via a `_sessionGate`, or explicitly pause capture threads during reconnect. |
| N3 | `NDIAudioChannel.cs:38, 351`; `NDIVideoChannel.cs:27, 500` | **Medium** | `_cts` is a latched field initializer, cancelled in `Dispose`, never replaced. Post-dispose `StartCapture` spawns a thread using a cancelled token — exits silently. No `ObjectDisposedException` guard. | Re-create `_cts` on each `StartCapture`; throw `ObjectDisposedException` when `_disposed`. |
| N4 | `NDIAudioChannel.cs:220` + `NDIVideoChannel.cs:168` | **Medium** | Both channels write into the same `NDIClock` with no coordination; timestamps from independent sender stamping cause `NDIClock.Position` to stutter non-monotonically. | Introduce a `ClockPolicy` (VideoPreferred / AudioPreferred / FirstWriter); make the non-chosen channel a no-op on `UpdateFromFrame`. |
| N5 | `NDIClock.cs:42`, `NDISource.cs:220` | Medium | `NDIClock` default `sampleRate=48000` regardless of `NDISourceOptions.SampleRate`. | Pass `options.SampleRate` in `NDISource.Open`. |
| N6 | `NDIAudioChannel.cs:251` + `NDIVideoChannel.cs:286` | Medium | Bare `catch (Exception)` in capture loops: if the throw was *inside* `_frameSync.Capture*`/`Free*`, an un-freed NDI frame leaks on every error. | Separate try/finally; guarantee `FreeAudio`/`FreeVideo` on any non-empty frame; narrow the outer catch. |
| N7 | `NDIVideoChannel.cs:345–371` (`TryMapFourCc`) | Medium | Drops 10-bit (P216/V210), 16-bit (P416/PA16), and extended-color formats silently. Depending on `NDIRecvColorFormat`, this can be most frames. | Add P216/V210 mapping (core `VideoFormat` needs 10-bit), or at minimum surface a `FormatUnsupported` event instead of silent drop. |
| N8 | `NDIVideoChannel.cs:432` | Low | `uvStride = Math.Max(uvRowBytes, yStride/2)` heuristic for I420 may disagree with NDI's actual padded chroma stride. | Verify against padded NV12/I420 sources; use `(yStride + 1) / 2` convention. |
| N9 | `NDIAudioChannel.cs:240–243` | Low | DropOldest discards silently without notifying; the `_framesProduced` counter can under-report after internal drop. | Track by polling `Reader.Count` before/after write, or switch to the manual-ring pattern used by `NDIVideoChannel`. |
| N10 | `NDIAudioChannel.cs:253–283` | Low | Scalar fallback in `PlanarToInterleaved` assumes planar stride even when FourCC is interleaved — latent bug because framesync always returns FLTP today. | Guard on `frame.FourCC == NDIFourCCAudioType.Fltp`. |
| N11 | `NDIVideoChannel.cs:210–223` | Low | 750 ms forward-jump clamp pins to synthetic PTS forever on legitimate pauses. | Expose `NDISourceOptions.MaxForwardPtsJumpMs`. |
| N12 | `NDISource.cs:385–389` | Low (API) | `Stop` name contradicts behavior — only stops the clock, not capture. | Rename `StopClock` (mark old `[Obsolete]`); or actually stop capture threads for symmetry with `Start`. |
| N13 | `NDISource.cs:303` | Low | `OpenByNameAsync` can leak the finder if the internal `NDISource.Open` throws. | Wrap open + hand-off in try/catch; dispose finder on failure. |
| N14 | `NDIAVChannel.cs:33–34` | Medium (API) | Refuses video-only sources with `InvalidOperationException`. PTZ previews and NDI|HX streams are legitimately video-only. | Make `AudioChannel` nullable and let consumers branch. |
| N15 | `NDISource.cs:435–439, 474` | Low | Second `WaitOne(connectionCheckIntervalMs)` loops once after cancellation before exit. | Break on `WaitOne == true && token.IsCancellationRequested`. |
| N16 | `NDIVideoChannel.cs:507–522` | Low | `EnqueueFrame` drop-oldest path races `_framesInRing` counter with consumer `FillBuffer`; the `Max(0, …)` clamp in `BufferAvailable` hides a transient negative. | Strict increment-on-write / decrement-on-read with `Debug.Assert(>= 0)`. |
| N17 | `NDISource.cs:622–627` | Low | `StateChanged` fires via `ThreadPool` after `_state` has already advanced; handlers reading `source.State` vs `args.NewState` see different values. | Document: `args.NewState` is authoritative (already passed — just document). |
| N18 | `NDIAudioChannel.cs:147` + `NDIVideoChannel.cs:116` | Low | `StartCapture` has no "already started" guard; double-call spawns a second thread on the same framesync. | `if (Interlocked.Exchange(ref _started, 1) == 1) return;` |
| N19 | `NDISource.cs:647` | Low | Serial disposal with 2 s joins → worst-case 4 s teardown before `_frameSync.Dispose`. | Cancel both channel CTSs first, then join both, then dispose. |
| N20 | `NDISource.cs:211` | Low | `receiver.Connect(source)` before `NDIFrameSync.Create` — tiny window where initial frames may be dropped. | Create framesync first, then connect (matches SDK sample order). |
| N21 | `NDIAudioChannel.cs:355` | Low | Dispose doesn't drain + re-pool ring buffers (GC picks them up; cosmetic). | Drain loop in Dispose. |
| N22 | `NDIVideoChannel.cs:534–549` | Low | `ParseNdiColorMeta` uses `Contains("BT.709")` on raw XML — false positives on comments. | Minimal XML reader; NDI metadata is small. |
| N23 | `NDIWrappers.cs:123` vs `NDIWrappers.cs:720` | Low | `AllowVideoFields = true` by default but framesync always pulls `Progressive` → wasted decode/bandwidth. | Default `AllowVideoFields = false` when the channel always pulls Progressive. |

---

## Concurrency / lifetime / resource issues (NDI-input)

1. **Native handle lifetime** — no ref/release; the implicit "capture stops before Dispose" contract is defeated by the 2 s join timeout (N1).
2. **Reconnect-while-capturing** — see N2. A `ReceiverSession` state machine with explicit Suspend/Resume would be the principled fix.
3. **`_frameSyncGate` is intentionally per-channel** (NDISource.cs L221–224 comments: a shared gate would block audio during large video `Marshal.Copy`). Correct, but **Dispose must not race with capture** — introduce a separate `_sessionGate` only taken for teardown/reconnect.
4. **Start/Stop/Dispose idempotency** — `_started`/`_videoStarted`/`_audioStarted` are one-shot flags. No `Restart`. Same cycle-state leak pattern as `NDIAVSink` (main review §Concurrency #2).
5. **Watchdog/reconnect** single-threaded; OK.
6. **`_finder` retained for name-based reconnect** (L300–303); disposed in `NDISource.Dispose`. OK.
7. **Event dispatch threading**: `StateChanged`/`BufferUnderrun` on `ThreadPool`. `EndOfStream` declared but never raised (`#pragma warning disable CS0067`) — the `IAudioChannel`/`IVideoChannel` interfaces force it to exist; document that it is inapplicable for live NDI.
8. **Synchronous dispose only** — `NDISource : IDisposable`, joins threads on the calling thread; up to ~4 s from a UI thread (N19). Add `IAsyncDisposable`.
9. **Clock disposal ordering** — `Clock.Dispose()` is last (L652, after `_receiver.Dispose()`). Good.

---

## Performance / allocation hotspots (NDI-input)

| Location | Issue | Suggestion |
|---|---|---|
| `NDIVideoChannel.CopyPacked/CopyNv12/CopyI420` L373–462 | Full managed copy per frame via `Marshal.Copy` into `ArrayPool<byte>.Shared` — ~240 MB/s at 1080p60 UYVY. Identical pattern to FFmpeg's `ConvertFrame`. | Keep `NDIVideoFrameV2` alive inside `NDIVideoFrameOwner`; call `_frameSync.FreeVideo` on `Dispose` (serialize with capture on `_frameSyncGate`). Eliminates the copy. |
| `NDIAudioChannel.PlanarToInterleaved` L253–283 | Uses SDK SIMD path; optimal. | — |
| `NDIAudioChannel._pool` L49 | Pre-sized for `framesPerCapture * channels`; stale after channel-count change (L228–232). | Re-allocate the pool on channel-count change. |
| `NDIVideoChannel.EnqueueFrame` L507–522 | Drop-oldest path does two ring ops per overflow. | For queue depth == 1, use `Volatile.Exchange(ref _latest, frame)`. |
| `NDISource.DiscoverAsync` L529–566 | Creates a fresh `NDIFinder` per call. | Cached singleton `NDISource.Discovered` with `SourceAppeared`/`SourceDisappeared` events. Replaces ad-hoc finders in NDIPlayer L87–110 and inside `OpenByNameAsync`.
| `NDIVideoChannel.CaptureLoop` L251–278 | Per-iteration `lock (_formatLock)` read of `_sourceFormat.FrameRate` (hundreds/s). | Cache `fpsNow`; refresh only when format changes. |
| `NDIReceiverSettings` defaults L121–122 | `Bandwidth = Highest` even for `UltraLowLatency` preset. | Map preset → `NDIRecvBandwidth` in `NDISourceOptions.ForPreset`. |

---

## API ergonomics for an NDI consumer

### Current boilerplate (NDIAutoPlayer ~532 LoC, NDIPlayer ~329 LoC)

```csharp
var avSource = await NDIAVChannel.OpenByNameAsync(name, NDISourceOptions.ForPreset(preset, channels: outCh), ct);
var srcFmt   = avSource.AudioChannel.SourceFormat;
int outCh    = Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, 2));
var hwFmt    = new AudioFormat(srcFmt.SampleRate, outCh);
var routeMap = BuildRouteMap(srcFmt.Channels, outCh);        // duplicated in 5 apps

using var output = new PortAudioOutput();
output.Open(device, hwFmt, suggestedLatency: profile.AudioSuggestedLatency);

avSource.StartVideoCapture();                                  // order-sensitive
await avSource.WaitForVideoBufferAsync(1, vfCts.Token);        // format detection
var videoFormat = avSource.VideoChannel.SourceFormat;
var videoOutput = new SDL3VideoOutput();
videoOutput.Open($"NDI — {name}", w, h, videoFormat);
videoOutput.OverridePresentationClock(output.Clock);
if (profile.ResetClockOrigin) videoOutput.ResetClockOrigin();

using var router = new AVRouter();
var audioEp = router.RegisterEndpoint(output);
router.SetClock(output.Clock);                                  // NDIClock dropped on the floor
var audioIn = router.RegisterAudioInput(avSource.AudioChannel);
router.CreateRoute(audioIn, audioEp, new AudioRouteOptions { ChannelMap = routeMap });
var videoEp = router.RegisterEndpoint(videoOutput);
var videoIn = router.RegisterVideoInput(avSource.VideoChannel!);
router.CreateRoute(videoIn, videoEp);
router.BypassVideoPtsScheduling = profile.BypassVideoPtsScheduling;

avSource.StartAudioCapture();
await Task.WhenAll(
    avSource.WaitForAudioBufferAsync(profile.AudioPreBufferChunks, ct),
    avSource.WaitForVideoBufferAsync(profile.VideoPreBufferFrames, ct));
await output.StartAsync();
await videoOutput.StartAsync();
await router.StartAsync();
```

~40 lines of plumbing *after* the presets already collapse most knobs. The auto-reconnect watchdog is free, but the A/V drift auto-correction in NDIAutoPlayer L377–417 is reimplemented in every consumer.

### Proposed facade

```csharp
using var player = MediaPlayer.Create()
    .WithNDIInput("OBS", NDIEndpointPreset.LowLatency,
                  reconnect: NDIReconnectPolicy.AutoWithFinder)
    .WithAudioOutput(engine.Default)
    .WithVideoOutput(SDL3VideoOutput.ForWindow(title: "NDI — OBS"))
    .WithAutoAvDriftCorrection()                       // opt-in; wraps avSource.TryGetAvDrift + SetInputTimeOffset
    .Build();

await player.PlayAsync();                               // orchestrates: StartVideoCapture → wait → StartAudioCapture → prebuffer → start
await player.WaitForWindowCloseOrCancelAsync(ct);
```

### Required additions

1. `MediaPlayerBuilder.WithNDIInput(NDIDiscoveredSource, NDIEndpointPreset = Balanced, NDIReconnectPolicy = None)` plus `WithNDIInput(string name, …)` overload that opens lazily.
2. `NDIReconnectPolicy` record replacing the two boolean knobs (`AutoReconnect`, `FinderSettings != null`).
3. **`NDISource.Discovered` singleton registry** — one long-lived `NDIFinder` owned by `NDIRuntime`, exposing `Sources`, `SourceAppeared`/`SourceDisappeared`, `FindAsync(pattern, ct)`. Replaces ad-hoc finders in NDIPlayer L87–110 and inside `OpenByNameAsync`.
4. **Automatic clock registration** — `WithNDIInput` should `router.RegisterClock(avSource.Clock, ClockPriority.Hardware)` so external consumers *can* slave to NDI time; default resolver still picks audio hardware clock.
5. `WithAutoAvDriftCorrection(options?)` rolling up NDIAutoPlayer L380–416.
6. Video-format pre-wait baked into `PlayAsync` (today every consumer does StartVideoCapture → WaitForVideoBufferAsync(1) → read SourceFormat → open SDL3 → StartAudioCapture by hand).
7. `AudioFormat.NegotiateFor(channel, device)` (already flagged in the main review) — solves the duplicated `Math.Min(…)` + `BuildRouteMap` in NDIPlayer L178–179 / NDIAutoPlayer L192–194.
8. `NDISource.TryOpenVideoOnly` / `TryOpenAudioOnly` to relax N14.

---

## Consistency with the rest of the framework

- **Naming**: `NDISource` has no peer in FFmpeg (where `FFmpegDecoder` plays both roles). README calls it "analogous to FFmpegDecoder" but it is closer to a demuxer+receiver hybrid. Pick one analogy.
- **Seek**: `NDIAudio/VideoChannel.Seek` silently no-op (`CanSeek = false`). Framework-wide choice should be explicit; a `NotSupportedException` would catch misuse earlier — or document the no-op in XML and enforce `CanSeek` awareness at the router level.
- **Dispose**: `IDisposable` only. Same `IAsyncDisposable` gap as `FFmpegDecoder`.
- **Error events**: `StateChanged` exists, but capture-loop errors are swallowed + logged (N6). Needs a unified `IMediaSourceErrorSource` event surface across `FFmpegDecoder` + `NDISource`.
- **Clock priority**: `NDIClock` is not registered with the router. Decide: is it `Hardware` (tied to real signal) or `External` (remote authority)? Currently de-facto "discarded".
- **`IMediaClock.SampleRate`**: main review notes the plan dropped it in favor of `TickCadence`, but `NDIClock.SampleRate` is still a concrete property (L37). Reconcile.
- **Volume**: `NDIAudioChannel.Volume` (L63) is stored but never read — identical dead-code issue to `FFmpegAudioChannel.Volume` flagged in the main review §Consistency.
- **`BypassVideoPtsScheduling`**: receive-path amplifies the need for per-route `LiveMode` (main review Tier 4 #22) because one NDI source frequently feeds both a low-latency preview *and* a scheduled recording endpoint from the same router.
- **`IVideoColorMatrixHint` propagation**: `NDIVideoChannel` implements the hint (L18, L49–55), but NDIAutoPlayer doesn't propagate it — the receive path is the ideal first customer for the auto-propagation refactor (main review Tier 3 #18).

---

## Refactor roadmap (NDI-input sub-tier)

Ordered small → large; integrates with the main review's tiering.

### Tier 1-N — Correctness fixes (1–2 days)
- **N1 / N3 / N18** — per-start `_cts`; "already started" guard; loop-join capture threads before disposing framesync/receiver.
- **N2** — introduce a `_sessionGate` shared by `NDISource`, both channels, and `TryReconnect`; hold it across `recv_connect` and framesync destruction.
- **N5** — pass `options.SampleRate` to `NDIClock` ctor.
- **N6** — narrow exception handling; guarantee `FreeAudio`/`FreeVideo` in `finally`.
- **N12 / N19** — rename `NDISource.Stop` → `StopClock` ([Obsolete] forwarder); cancel both channel CTSs in parallel before joining.
- **N14** — allow `NDIAVChannel` with null `AudioChannel`.

### Tier 2-N — Ergonomic helpers (3–5 days)
- **N4 + clock policy** — `NDIClockPolicy { VideoPreferred, AudioPreferred, FirstWriter }`.
- **N7 / N11** — `NDIUnsupportedFourCc` and `NDIFormatChange` events; expose `MaxForwardPtsJumpMs`.
- **NDI discovery registry** — one process-wide `NDISource.Discovered`.
- **`NDIReconnectPolicy` record** replacing the two boolean knobs.

### Tier 3-N — Builder integration (aligns with main-review Tier 3)
- `MediaPlayerBuilder.WithNDIInput(…)` overloads; orchestrate video-first format detection + prebuffer + start ordering.
- Auto-register `NDIClock` into the router's clock registry at `Hardware`.
- Auto-propagate `IVideoColorMatrixHint` in `Build()`.
- `WithAutoAvDriftCorrection()` rolling up NDIAutoPlayer L380–416.

### Tier 4-N — Zero-copy + extended formats
- **Zero-copy video** — keep `NDIVideoFrameV2` alive inside `NDIVideoFrameOwner`; `_frameSync.FreeVideo` on `Dispose`. Depends on the `VideoFrameHandle` ownership refactor (main review B15/B16).
- **10-bit / HDR** — P216/V210 support + `ColorSpace`/`TransferCharacteristics` on `VideoFormat`.
- **Raw recv mode** — `NDIReceiveMode { FrameSync, RawPolling }` so latency-critical consumers can bypass framesync resampling.

### Tier 5-N — Advanced NDI features
- Integrate with main-review Tier 5 (timeline / multi-source mixing): each `NDISource` is a hot-swappable router input.

---

## Nice-to-haves / future work (NDI-input)

- **Tally passthrough** — `NDIReceiver.SetTally` (NDIWrappers.cs L261) is not surfaced on `NDISource`. Trivial to expose.
- **PTZ control** — wrappers exist (NDIWrappers.cs L340–418); add an `NDISource.Ptz` sub-object.
- **KVM / upstream metadata** — `NDIReceiver.SendMetadata` (L250) not exposed. Needed for bi-directional control.
- **Structured NDI color-space XML parsing** replacing the `Contains` scan (N22); also pick up `<ndi_tally>` / `<ndi_timecode>`.
- **Low-latency raw-recv preset** — `NDIRecvBandwidth.Lowest` + `recv_capture_v3` with 1 ms poll; bypasses framesync for lower baseline latency at the cost of manual resampling.
- **Multi-NDI-source mixing** — one `NDIReceiver` per source, each registered as a distinct `AVRouter` input. Blocked on the discovery registry.
- **NDI Discovery Server integration** — `NDIRecvListener`/`NDIRecvAdvertiser` wrappers exist (NDIWrappers.cs L937, L1035) but have no surface.
- **Format-change events** on both channels when width/height/FPS/FourCC change, so SDL3 windows and NDI sinks can re-open their pipelines automatically.
- **Per-sender statistics** — `NDIReceiver.GetPerformance`/`GetQueue` (L279–287) unused. Surface as `NDISource.GetDiagnostics()` for the 5 s status ticker.
- **Unit-test scaffolding** — `FakeFrameSync` interface to drive timestamp-fallback / synthetic-PTS re-origin / clock-monotonicity tests without a live sender.

---

*Addendum based on read-only inspection of `NDI/S.Media.NDI/NDISource.cs`, `NDIAVChannel.cs`, `NDIAudioChannel.cs`, `NDIVideoChannel.cs`, `NDIClock.cs`, `NDIAvTimingContext.cs`, `NDIEndpointPreset.cs`, `NDILatencyPreset.cs`, `NDIPlaybackProfile.cs`, `NDISourceState.cs`, `README.md`; the wrapper surface `NDI/NDILib/NDIWrappers.cs`; and the consumer apps `Test/MFPlayer.NDIPlayer/Program.cs`, `Test/MFPlayer.NDIAutoPlayer/Program.cs`. Line numbers refer to the checked-out revision and are approximate.*

---
---

# Addendum 2 — Core, SDL3, Avalonia, PortAudio deep dive

*Addendum review date: 2026-04-22. Does not duplicate B1..B22 or N1..N23.*

## Executive summary (top 10 new findings)

1. **[Router]** `PushAudioTick` stamps the endpoint buffer's PTS with the *first* filled input only (AVRouter.cs:1060-1061); multi-input mixes onto the same endpoint deliver NDI/SMPTE timecodes that belong to one of the sources.
2. **[Router]** `ApplyChannelMap` is skipped entirely when `srcFormat.Channels == format.Channels` (AVRouter.cs:1074-1092), silently ignoring explicit user-supplied maps that only reorder/gain same-count channels.
3. **[Router]** Mix accumulation has no clip, no automatic headroom, and no denormal flush — N correlated routes summed into `dest` can saturate, and near-zero decay tails cost 20-100× CPU on x86.
4. **[Router]** Teardown path `DisposeCore` (AVRouter.cs:609-630) never calls `RemoveRouteInternal`, so `route.VideoSub` subscriptions are not disposed — their queued frames leak until finalization GC'd.
5. **[Core]** `VideoPresentCallbackForEndpoint` holds a *single* `PtsDriftTracker` per endpoint (AVRouter.cs:1337); if the video-route last-write-wins policy is ever relaxed, two inputs to one pull endpoint silently fight for the tracker origin.
6. **[Core]** `Mixing/` folder is empty while mixing math lives in `AVRouter.MixInto`/`ApplyChannelMap`/peak — clear signal of an in-progress split; decouples testability and blocks Tier 5 timeline work.
7. **[SDL3]** `WindowClosed` event (SDL3VideoOutput.cs:744) fires from the render thread; common handler pattern is `output.Dispose()` which then re-enters the same thread's `_renderThread.Join` → deadlock on user-close.
8. **[SDL3]** Clone sinks created via `CreateCloneSink` are tracked by the parent but nothing fans frames to them; the only delivery path is the user re-registering each clone on the router — undocumented.
9. **[Avalonia]** `AvaloniaOpenGlVideoCloneSink.ReceiveFrame` rents + memcpys every frame on the router thread, even when the source is a ref-counted pool buffer — ~2 GB/s on 4K60 per clone. No use of `RefCountedVideoBuffer.Retain`.
10. **[PortAudio]** `IClockCapableEndpoint.Clock` throws "Call Open() first." on `PortAudioOutput`; `AVRouter.AutoRegisterEndpointClock` evaluates it at register-time, so any user who registers before opening the device crashes. Contract is undocumented.

---

## 2. S.Media.Core deep dive

### 2.1 AVRouter

Architecture recap: `AVRouter` owns a push-thread pair (`PushAudioTick` + `PushVideoTick` on a secondary thread) plus two pull-style callbacks (`AudioFillCallbackForEndpoint`, `VideoPresentCallbackForEndpoint`) installed on each registered endpoint. Snapshots (`_audioRouteSnapshot`, `_videoRouteSnapshot`, `_{audio,video}RoutesByEndpoint`) are copy-on-write inside `_lock`; route/input/endpoint dictionaries are concurrent. A separate `_clockLock` guards the clock registry and `StopwatchClock` fallback. Scratch buffers, per-route pending video frames, and drift trackers live in per-key `ConcurrentDictionary` maps. Teardown uses `StopAsync` → `DisposeCore` without calling through `RemoveRouteInternal`.

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| R1 | AVRouter.cs:961-1012 | Med | First-pass format resolution `break`s on the first enabled route, so later routes with mismatched format only get the warning path after the chosen route is removed — enumeration order is non-deterministic (`ConcurrentDictionary`). | Pick a canonical format per endpoint at route creation (store in `EndpointEntry`); warn eagerly, mix only compatible routes. |
| R2 | AVRouter.cs:1060-1061 | **High** | `bufferPts = ptsBeforeFill + inp.TimeOffset + route.TimeOffset` taken only from the first input that produces samples; NDI/SMPTE sinks downstream receive wrong timecode for mixed inputs. | When mixing N inputs to a PTS-aware endpoint, compute weighted PTS (or refuse to mix and require a single "leader" input). Document contract. |
| R3 | AVRouter.cs:1022, 1065-1068, 1104 | Med | Mixing uses plain `+=` with no clip, no auto-scaling, no flush-to-zero. Two correlated inputs at 0.9 gain hard-clip; exponentially decaying silence enters denormals on x86. | Add `FlushDenormalsToZero()` on push/fill thread entry; optional `AutoAttenuate` or soft-clip in `MixInto`; expose an overflow counter on `RouterDiagnosticsSnapshot`. |
| R4 | AVRouter.cs:1074-1092 | Med | `ApplyChannelMap` only runs when `srcFormat.Channels != format.Channels`; identity-size maps with non-identity gains/swizzles are silently dropped. | Apply map whenever `route.BakedChannelMap is not null`. |
| R5 | AVRouter.cs:699-710 | Med | Auto-resampler creation is gated on `ep.Audio is IPullAudioEndpoint pullAudio` — push endpoints never get an auto-resampler even when the source rate differs (push endpoints have no advertised format). | Add `IAudioEndpoint.NegotiatedFormat` (or override hook) and create a route-level resampler whenever source ≠ negotiated. |
| R6 | AVRouter.cs:1465-1473 | Med | `GetOrCreateScratch` is racy: `TryGetValue` → `new float[]` → `_scratchBuffers[id] = newBuf`. Two concurrent pull callbacks on the same endpoint each allocate; loser's array becomes GC trash. Also first-time alloc on RT thread. | Pre-allocate in `SetupPullAudio`; use `GetOrAdd` with lock-free resize. Bound by `FramesPerBuffer × maxChannels`. |
| R7 | AVRouter.cs:117-128, 814-826 | Med | Endpoint iteration in `PushAudioTick` uses live `_endpoints.Values` while route snapshots are COW. An endpoint mid-registration (after dict insert but before clock wire-up) can be ticked without a `FillCallback` set. | COW `_endpointsSnapshot` rebuilt in the same `_lock` that owns `RebuildAudioRouteSnapshot`; push tick reads only the snapshot. |
| R8 | AVRouter.cs:270-305 | Med | `RegisterEndpoint` variants run *outside* `_lock`: dict write + `AutoRegisterEndpointClock` + `SetupPull*` are non-atomic. A concurrent `UnregisterEndpoint` can remove the dict entry while the clock remains registered. | Wrap registration in `_lock`; symmetric with unregister. |
| R9 | AVRouter.cs:609-630 | **High** | `DisposeCore` iterates `_routes.Values` but only disposes owned resamplers; subscriptions (`route.VideoSub`), per-route drift, and format-mismatch entries leak. `_pushVideoPending` is cleaned but not `_pushVideoDrift`. | Walk `_routes.Values` via `RemoveRouteInternal` (already does the full cleanup). |
| R10 | AVRouter.cs:411-467 | Med | `ResolveActiveClock` silently swaps the active clock on unregister; consumers that cached `router.Clock` get stale reference. No change event. | Add `event Action<IMediaClock>? ActiveClockChanged`; raise under `_clockLock` release. |
| R11 | AVRouter.cs:340-344 | Low | `AutoRegisterEndpointClock` uses the same `DefaultEndpointClockPriority` for every endpoint — a virtual clock outranks a `StopwatchClock` but not a real hardware clock, yet all get `ClockPriority.Hardware`. | Let `IClockCapableEndpoint` expose `DefaultPriority`; default to `External` for network, `Hardware` for local, `Internal` for virtual. |
| R12 | AVRouter.cs:180-202 | Med | No pre-roll barrier; `StartAsync` immediately pushes. With slow decoders the first few ticks deliver silence/black with valid PTS, clobbering NDI timecode alignment until real content arrives. Push-audio short-circuits via `maxFilled > 0`; push-video presents whatever is in the pending slot. | Add `AVRouterOptions.PreRollTimeout` / `MinBufferedFramesBeforeStart`; gate the first tick. |
| R13 | AVRouterOptions.cs:19 | Low | `InternalTickCadence = 10 ms` is a fixed global; aliases against 24/25 fps periods and 48 kHz tick budgets. | Expose per-axis cadence (`AudioTickCadence`, `VideoTickCadence`); auto-derive from highest-priority clock's `TickCadence`. |
| R14 | AVRouter.cs:1337, 724-785 | Med | `VideoPresentCallbackForEndpoint._drift` is per-endpoint not per-route. Current `CreateVideoRoute` enforces one-route-per-video-endpoint (last-write-wins, line 749-753) — implicit invariant that would silently break on any relaxation. | Store tracker on the route (like push path at AVRouter.cs:161). Remove the last-write-wins clamp once fixed. |
| R15 | AVRouter.cs:568-589 | Med | `GetDiagnosticsSnapshot` reads `InputEntry.PeakLevel` (float), `Volume` (float), `TimeOffset` (TimeSpan = 2×long), `Enabled` (bool) with no `Volatile`; torn reads on 32-bit runtimes. Related to B13. | Publish via `Volatile.Read` helpers or swap to `long` ticks + atomic write. |
| R16 | AVRouter.cs:507-552 | Low | `_driftEmaTicks` is a single state shared across every `(audioInput, videoInput)` pair; calling with different pairs alternately smooths across pairs. | Key EMA by `(InputId, InputId)` in a `ConcurrentDictionary`, or document single-pair-only. |
| R17 | AVRouter.cs:1158-1216 | Med | Two separate dictionary operations for pending-frame management: `TryGetValue` → `stale.MemoryOwner?.Dispose()` → `_pushVideoPending[route.Id] = candidate`. Not atomic; a concurrent `RemoveRouteInternal` can Dispose the stale entry between. Related to B12 but in the push path. | Use `AddOrUpdate` with an updater that returns the new value and disposes the old under the dictionary's bucket lock. |
| R18 | AVRouter.cs:1221-1222 | **High** | After `ep.Video.ReceiveFrame(in candidate)` the router calls `candidate.MemoryOwner?.Dispose()`. Contract in `VideoFrame` docs says "endpoints must NOT dispose" — so synchronous-use-then-router-disposes is the only safe pattern. Any endpoint that retains asynchronously without `Retain()` sees use-after-free. `RefCountedVideoBuffer` exists but AVRouter doesn't use it on fan-out. | Before `ReceiveFrame`, if `candidate.MemoryOwner is RefCountedVideoBuffer rc`, call `rc.Retain()` per endpoint; endpoints call `Release()` when done. |
| R19 | AVRouter.cs:856-899 | Low | Push thread spawns a secondary video thread and relies on `_running` for stop; no cancellation token → `WaitUntil` spin on a stopped clock during shutdown can burn 3 ms before exiting. | Use `CancellationTokenSource`; `WaitUntil` checks token. |
| R20 | AVRouter.cs:932-949 | Low | `WaitUntil` spins for the final 3 ms regardless of CPU pressure; on battery this defeats `Thread.Sleep`'s scheduling hint. | Cap spin to `<1 ms` under battery / low-power cgroup. |
| R21 | AVRouter.cs:398-403 | Low | `SetRouteEnabled` writes `route.Enabled` via `Volatile.Write` but the route could have been concurrently removed, so the write lands on a detached `RouteEntry`. Benign but silent. | Re-read after write; or take `_lock` (rare path, cheap). |
| R22 | AVRouter.cs:664-675 | Low | Format compatibility warning only triggers when `caps.SupportedFormats.Count > 0`; empty capabilities silently mean "accept anything". Hides misconfigurations. | Require `IFormatCapabilities<T>` implementers to return non-empty or throw. |
| R23 | AVRouter.cs:503, 1118-1225 | Med | `BypassVideoPtsScheduling` is a whole-router toggle — per-route "live mode" cannot coexist with a parallel scheduled route (main review Tier 4 #22). | Move to `VideoRouteOptions.BypassPtsScheduling`; read per-route in both push and pull paths. |
| R24 | AVRouter.cs:1065-1072 | Low | `MeasurePeak(filledSpan)` runs *after* gain but *before* channel map; downmix targets see inflated peaks relative to what the endpoint actually emits. | Move peak metering to just before `ep.Audio.ReceiveBuffer`, on the final mixed span. |

### 2.2 Clocks

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| C1 | MediaClockBase.cs:17-25, 55-61 | Low | `Timer` callback `OnTimerTick` can fire on an already-disposed clock if Dispose races with the scheduled callback (`Timer.Dispose()` doesn't wait). Handler runs against stale state. | Use `Timer.Dispose(WaitHandle)` or swap to `ITimer.DisposeAsync`. |
| C2 | StopwatchClock.cs:27-35 | Low | On Windows, `System.Threading.Timer` granularity is ~15 ms without `timeBeginPeriod`; `TickCadence=10ms` is silently 15 ms. Linux uses hrtimers (~1 ms). | Document Windows behaviour; optionally call `TimeBeginPeriod(1)` on Start, paired Release on Stop. |
| C3 | VideoPtsClock.cs:19-24, 74-127 | Med | `_lastPts`, `_swAtLastPts`, `_initialised`, `_running` are plain fields. `UpdateFromFrame` (render thread) and `Position` getter (router push/pull threads) race; on 32-bit runtimes `TimeSpan.Ticks` (long) tears. | Lock around pair update/read, or store as two `long` fields updated with `Interlocked.Exchange`/paired memory barriers. |
| C4 | VideoPtsClock.cs:119 | Low | `SeekThreshold = 500 ms` hard-coded; micro-seeks below 500 ms are treated as drift. Frame-stepping below threshold yields wrong position. | Expose as ctor param / settable property; 500 ms default. |
| C5 | HardwareClock.cs:53-87 | Low | Fallback stopwatch re-anchors on any `hw > 0`, but a stale cached value + live value alternation during PortAudio underrun makes the clock jitter across fallback/live boundary. | Require two consecutive "valid" reads before flipping off fallback; debounce by 10 ms. |
| C6 | StopwatchClock.cs:37-54 | Low | `Start`/`Stop`/`Reset` not re-entrant with `base.Start`/`base.Stop`. Reset while running leaves the underlying `Timer` active but `_running=false`. | Call `base.Stop()` in Reset; document. |
| C7 | HardwareClock.cs:110-114 | Low | `UpdateTickInterval` stashes a new interval on `base.SetTickInterval` but subscribers that latched `TickCadence` at Start never see the update. | Fire a `TickCadenceChanged` event; AVRouter recomputes spin budget. |
| C8 | Routing/ClockPriority.cs:10-36 | Low | `ClockPriority.Internal = 0` is reserved "users should not register at this level", but nothing enforces it. | Make Internal register-refused; or expose `InternalClock` as the sentinel. |
| C9 | IMediaClock.cs:22 | Low | `event Action<TimeSpan>? Tick` has no guarantee about execution thread; `MediaClockBase` drives from a `System.Threading.Timer` (ThreadPool). Interface doc says "NOT raised on the RT audio thread" but says nothing about where it *is* raised. | Document `Tick` as "ThreadPool"; recommend `DispatcherTimer` indirection in UI code. |

### 2.3 Channel / frame / endpoint contracts

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| CH1 | IMediaChannel.cs:18-28 | Med | `FillBuffer` contract says "non-blocking" but never states that concurrent invocations are forbidden. Multiple routes to the same `IAudioChannel` (legal!) race at `AudioChannel._currentChunk`/`_currentOffset`. | Either document single-reader and warn in AVRouter, or serialise per-channel reads inside the router. |
| CH2 | IAudioChannel.cs:24-25 vs AVRouter.cs:1049-1055 | Med | Doc: "Position is derived from samples consumed." AVRouter reads `channel.Position` *before* `FillBuffer` and treats it as the PTS of the next sample; correct only because `AudioChannel.Position` is incremented after the read — any alternate impl that increments before returning would break. | Add explicit `IAudioChannel.ReadHeadPosition` getter separated from "position of last consumed frame". |
| CH3 | IVideoChannel.cs:46-47 | **High** | Default `Subscribe` returns `FillBufferSubscription` and doc warns "at most one subscription alive at a time" — but `AVRouter.CreateVideoRoute` subscribes **per route**. Legacy channels without native fan-out (e.g. NDI) silently race when two endpoints attach to one video input. | Make the default `throw NotSupportedException("Implement native fan-out")` and audit implementations; or have `FillBufferSubscription` use a serializing shim. |
| CH4 | IAudioEndpoint.cs:37-38 | Med | Default `ReceiveBuffer(span, n, fmt, sourcePts)` discards `sourcePts`. Silent loss of timecode on sinks whose author forgot to override the PTS-aware variant. | Mark default `[Obsolete]` or make the PTS overload abstract; wrap legacy sinks in an explicit adapter. |
| CH5 | IPullAudioEndpoint.cs:18 | Low | `FillCallback { get; set; }` has no swap semantics; a concurrent re-register by the router during `UnregisterEndpoint` can dangle the callback mid-RT-fill. | Change to `SetFillCallback(IAudioFillCallback?)` with internal volatile write + short spin for in-flight fills. |
| CH6 | IPullAudioEndpoint.cs:14-30 | Low | `EndpointFormat` and `FramesPerBuffer` are not required constant after Open — AVRouter caches them implicitly. A device-format change after registration breaks scratch-buffer sizing. | Document as "frozen after Open"; throw from setter if violated. |
| CH7 | IVideoEndpoint.cs:17-22 vs VideoFrame.cs:18-21 | **High** | Contract says endpoints must NOT dispose `MemoryOwner`; router relies on this and disposes synchronously after `ReceiveFrame`. Any endpoint that retains past the call sees use-after-free unless it copies. All current sinks correctly copy, but no compile-time enforcement. | Pass a `RefCountedVideoBuffer` that endpoint must `Retain()/Release()`, or provide a `FrameHandle` wrapper. See R18. |
| CH8 | IClockCapableEndpoint.cs:7-10 | Med | No lifecycle contract: is `Clock` valid before `StartAsync`? `PortAudioOutput.Clock` throws before Open; `AVRouter.AutoRegisterEndpointClock` calls it at `RegisterEndpoint` time. | Document: "Clock must be available after construction." Fix PortAudio (P1). |
| CH9 | Media/Endpoints/IFormatCapabilities.cs | Low | Empty `SupportedFormats` list = "accept anything" is undocumented, per R22. | Document or require non-empty. |

### 2.4 Mixing math (empty `Mixing/` folder, math inside AVRouter)

- **M1 (structural):** `Media/S.Media.Core/Mixing/` exists but is empty; `MixInto`, `ApplyChannelMap`, `ApplyGain`, `MeasurePeak` all live in `AVRouter` (lines 1463-1557). Incomplete Tier-4-ish refactor — mixer logic cannot be unit-tested without standing up a router.
- **M2:** `MixInto` is a plain SIMD sum with no clip, no 1/√N auto-attenuation (see R3). At N≥3 correlated inputs, clipping inevitable at unity gain.
- **M3:** Peak order (R24): post-gain, pre-map; rework.
- **M4:** `ApplyChannelMap` accumulates `+=` onto dest (line 1497) and relies on the caller to pre-clear. `PushAudioTick` (line 1082) and pull callback (line 1307) both do, but this is an implicit contract; a hand-rolled caller will produce garbage.
- **M5:** `ApplyChannelMap` uses scalar inner loops; for large channel counts (≥8 ch Atmos beds) the inner loop fails to vectorize. `ApplyGain`/`MixInto` do vectorize. Consider a planar-channel layout internally for the map step.

Suggested structural fix: extract an `IAudioMixer` in `Mixing/` with `MixInto`, `ApplyChannelMap`, `ApplyGain`, `MeasurePeak`, `FlushDenormalsToZero`. AVRouter depends on the interface; enables unit testing the mixer in isolation.

### 2.5 PooledWorkQueue

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| PQ1 | PooledWorkQueue.cs:131 | Med | `Dispose` disposes the semaphore but does not cancel pending `WaitForItem` waiters; callers that forget to cancel their own CT first get `ObjectDisposedException`. | Add `Complete()` that signals the semaphore with a sentinel, or wrap `_signal.Wait(ct)` in a catch for `ObjectDisposedException`. |
| PQ2 | PooledWorkQueue.cs:55-74 | Med | `TryReserveSlot` increments `_count` *before* `EnqueueReserved` puts the item in the queue. `Count` read by consumers (e.g. `DriftCorrector` in `PortAudioSink.cs:209` → `_work.Count`) is a lie between reserve and enqueue. | Separate "committed" count from "reserved"; `Count` = dequeue-visible items. |
| PQ3 | PooledWorkQueue.cs:117-129 | Low | `Drain` has no thread-safety guarantee vs concurrent producers; intended only for Dispose/teardown, undocumented. | XML doc + `Debug.Assert(!_producing)` guard. |
| PQ4 | PooledWorkQueue.cs all | Low | No `CompleteAdding()` — producer shutdown must be signalled out-of-band. | Add `Complete()` + `IsCompleted`. |

### 2.6 Errors & logging consistency

- **EL1:** `Errors/` has `AudioEngineException`, `BufferException`, `MediaException` but no `RoutingException` / `ClockException`. Routing failures (`AVRouter.CreateRoute` "Endpoint X is not registered") throw `InvalidOperationException`. Extends main review §Consistency.
- **EL2:** All `MediaCoreLogging.GetLogger(nameof(Type))` loggers share the same category prefix; no correlation scope (`RouteId`, `InputId`). Tracing a single stream across FFmpeg → router → sink requires grepping.
- **EL3:** `PushVideoTick`/`PushAudioTick` catch and log *any* exception with the same message. Persistent bad frames flood the log; no rate-limit (unlike `SDL3VideoOutput.RenderLoop` which caps at 3 + every 100th).

---

## 3. S.Media.SDL3 deep dive

### 3.1 Architecture

- **Render thread** (`SDL3VideoOutput.RenderLoop`) owns the GL context, pumps SDL events, calls `PresentCallback.TryPresentNext`, performs texture upload/draw, HUD overlay, `GLSwapWindow`. Vsync controls pacing.
- **Router push thread** is an independent source of truth; `IPullVideoEndpoint.PresentCallback` implemented by `VideoPresentCallbackForEndpoint` inside AVRouter — SDL3 reads router state directly.
- **Clone sinks** each own their own SDL window, GL context, and render thread. No GL context sharing; each clone re-uploads the entire frame.
- **`_clones` array** is a COW array guarded by `_cloneLock`, but **nothing fans frames to clones** — they are `IVideoEndpoint` implementations the user must register on the router separately (S2/S4).

### 3.2 Findings

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| S1 | SDL3VideoOutput.cs:740-746, 750-798 | **High** | User-close path: render thread sets `_closeRequested=true`, breaks loop, fires `WindowClosed`. Typical handler calls `output.Dispose()`; Dispose calls `_renderThread.Join(3s)` on the thread currently in `Invoke`. Deadlock 3 s, then dispose proceeds with render thread "orphaned". | Raise `WindowClosed` via `ThreadPool.QueueUserWorkItem`; document that handlers must not Dispose synchronously. |
| S2 | SDL3VideoOutput.cs:843-864 | **High** | `CreateCloneSink` appends to `_clones` but nothing in `RenderLoop` forwards frames to clones — they are unwired. The only path to frames is the user explicitly `router.RegisterEndpoint(clone)`. Parent iteration in Dispose then double-disposes if user did register, because the clone's own Dispose is called by AVRouter's `UnregisterEndpoint` too. | Either (a) parent tees frames to clones on render, or (b) document Register-separately contract and dedupe in Dispose. |
| S3 | SDL3VideoOutput.cs:647-668 | Med | Texture-reuse gate uses `vf.Data.Equals(_lastUploadedData)` where `_lastUploadedData` is a `ReadOnlyMemory<byte>` holding a reference to a possibly-returned ArrayPool rental. `ROM<byte>.Equals` is structural (array ref + offset + length), so after the original is returned and the same array is re-rented, a coincidental match → skip upload → stale texture. | Compare `VideoFrame.Pts + Width + Height + MemoryOwner reference` (identity), not `Data.Equals`. |
| S4 | SDL3VideoOutput.cs:785-790 | Med | Dispose disposes `_clones[]`, but each clone holds an independent `_running` flag; if user also `UnregisterEndpoint`s them via AVRouter first, clone has already stopped — second Dispose is a no-op, but `ReleaseSdlVideo` refcount will decrement twice. | Guard with `_disposed` AND track whether SDL init was acquired by this instance. |
| S5 | SDL3VideoOutput.cs:313-339 | Med | `OverridePresentationClock`/`ResetClockOrigin` race: `_presentationClockOverride` volatile, `_hasPresentationClockOrigin` CAS'd, but `_presentationClockOriginTicks` is `Volatile.Write` without memory-barrier paired with the CAS. On weakly-ordered ARM64, `hasOrigin==1` may be observed before `originTicks` is visible → wrong offset for one-two frames. | Use `Interlocked.Exchange` for both fields or wrap in a small struct + CAS. |
| S6 | SDL3VideoOutput.cs:159-178 (YuvConfig setter) | Med | Setter writes `_yuvColorRange`/`_yuvColorMatrix` + `_lastAutoRange=Auto` + calls `ApplyResolvedYuvHints` which touches `_renderer.YuvColorRange` from the caller thread — but `_renderer` is owned by the render thread's GL context. Writing GL uniforms off-thread is UB on some drivers. | Queue pending-change flag; render thread applies at top of next iteration under MakeCurrent. |
| S7 | SDL3VideoOutput.cs:500-510 | Low | If `GLMakeCurrent` fails once, render thread exits. `_isRunning=false` set locally but `_closeRequested` is not — Dispose still waits 3 s on Join (fast, OK) but `WindowClosed` is never invoked. | Set `_closeRequested = true` on MakeCurrent failure. |
| S8 | SDL3VideoCloneSink.cs:133-153 | Med | `ReceiveFrame` always rents + memcpy via `ArrayPool<byte>.Shared`. 4K60 ≈ 2 GB/s per clone per frame, regardless of whether source is ref-counted. | Use `RefCountedVideoBuffer.Retain` on incoming `VideoFrame.MemoryOwner` when ref-countable; fall back to copy for heap-allocated frames. |
| S9 | SDL3VideoCloneSink.cs:163-177 | Med | Each clone's render loop calls `SDL.PollEvent` — SDL events are **process-global**. Parent and clones race on the event queue; a resize event for the parent window can be consumed by a clone's poll. | One event-pump thread per process; dispatch by window ID. |
| S10 | SDL3VideoOutput.cs:731-733 | Low | Render-exception log rate-limited to 3 + every 100th — reasonable, but doesn't differentiate exception types. Recurring `ObjectDisposedException` looks the same as a GL driver fault. | Tag exception type in log; carry first-seen / last-seen timestamps. |
| S11 | GLRenderer.cs:1734-1778 | Med | Dispose deletes GL resources but caller (`SDL3VideoOutput.Dispose` line 773) must ensure `GLMakeCurrent` is still valid. If the window was user-closed and SDL destroyed the GL surface underneath, `glDeleteTextures` into stale context is UB. | Wrap deletes in try/catch; or skip when `_closeRequested` signalled destroyed context. |
| S12 | SDL3VideoOutput.cs:580-610 | Low | Catch-up loop uses same `.Data.Equals(vf.Data)` identity as S3 to detect re-presentation; fragile contract with `VideoPresentCallbackForEndpoint` (CH3/CH7). | Add explicit "is-same-frame" bit on `VideoFrame` or on the callback return. |
| S13 | GLRenderer.cs:1593-1680 | Low | `DrawHud` uploads a fresh VBO every frame via `glBufferData(GL_DYNAMIC_DRAW, ...)` — with 50+ lines the per-frame alloc of `new float[totalGlyphs * 6 * 4]` (line 1611) is GC pressure on the render thread. | Reuse a growing scratch `float[]` pinned as a field. |
| S14 | SDL3VideoOutput.cs:866-883 | Low | `AcquireSdlVideo`/`ReleaseSdlVideo` refcount is a plain static `int`; if `SDL_Init` fails after increment the decrement restores state, but a type-load-style exception between increment and Init leaks the count. | try/finally with `Interlocked.Decrement` always on failure. |

### 3.3 Shader / texture / upload performance

- **11 shader programs** (RGBA, NV12, I420, I422P10, UYVY, P010, YUV444p, Gray8 + bicubic blit + FBO passthrough + HUD) compiled at `Initialise`; Dispose deletes all. Acceptable for desktop, heavy on Intel HD iGPUs — compilation can take 100-500 ms.
- Texture-reuse (`_hasUploadedFrame`) saves upload on stall frames; correct when the source pull callback re-presents the same `VideoFrame` instance.
- UYVY path renders via 2-pass FBO at native resolution (preserves sharp chroma).
- Integer textures (R16UI, RG16UI for I422P10 / P010) use `GL_NEAREST` correctly; filtering in shader.
- **Missing:** no persistent-mapped PBO path — uploads go direct via `glTexSubImage2D`, which stalls on some drivers for large frames. At 4K60, NV12 upload is ~6 MB every 16.6 ms; PBO round-robin would remove implicit driver sync.

### 3.4 Clone-sink contract + parent disposal cascade

Parent `SDL3VideoOutput.Dispose` disposes `_clones[]` directly, bypassing `UnregisterEndpoint`. If user registered the clone on the router, the router still holds the reference and may fan frames to a disposed sink. Clone's `_disposed=true` + `_running=false` guards the RT path (ReceiveFrame early-returns), but AVRouter's route is still "live" — `GetDiagnosticsSnapshot` shows a route to a dead endpoint. See S2/S4. Recommendation: parent Dispose **does not** touch clones; document clones as independent endpoints owned by the user (or the router).

---

## 4. S.Media.Avalonia deep dive

### 4.1 Architecture

- `AvaloniaOpenGlVideoOutput : OpenGlControlBase` runs render work inside `OnOpenGlRender(gl, fb)`, invoked by Avalonia's compositor (not a dedicated thread, not the UI dispatcher — the renderer thread).
- **Re-rendering driven by `RequestNextFrameRendering()` in a `finally` block** (line 421-423), forming a tight loop regardless of frame availability. Combined with `OnPropertyChanged → RequestNextFrameRendering` (line 432-437) it can double-invalidate.
- No separate render thread: all GL work piggy-backs on the compositor's OpenGL backend (ANGLE on Windows, GBM/EGL on Linux).
- Router's push-path delivers frames through `VideoPresentCallbackForEndpoint.TryPresentNext`, same callback as SDL3.
- Clone sink (`AvaloniaOpenGlVideoCloneSink`) is a full Avalonia control; receives frames via `ReceiveFrame` on the router thread, copies to ArrayPool buffer, stores in `VideoFrameSlot`, renders on its own compositor tick.

### 4.2 Findings

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| A1 | AvaloniaOpenGlVideoOutput.cs:419-423 | **High** | Unconditional `RequestNextFrameRendering()` in `finally` drives a 100% compositor-rate loop (60/120/144 Hz) even with no new frame and no visual change. On battery, persistent CPU/GPU draw. | Request next frame only when (a) new frame uploaded this tick, or (b) `BypassVideoPtsScheduling`. Otherwise rely on `ReceiveFrame → RequestNextFrameRendering`. |
| A2 | AvaloniaOpenGlVideoOutput.cs:380-400 | **High** | Same `ReadOnlyMemory<byte>.Equals` texture-reuse pitfall as SDL3 S3. `_lastUploadedData` may reference an ArrayPool rental already returned by the router. | Compare by `MemoryOwner` reference + `Pts` + dimensions. |
| A3 | AvaloniaOpenGlVideoCloneSink.cs:49-64 | **High** | Per-frame ArrayPool rent + full memcpy on router thread; no ref-count fast path. At 4K60 with 2 clones = 4 GB/s wasted memory bandwidth. | Honour `RefCountedVideoBuffer`; copy only when not ref-countable. |
| A4 | AvaloniaOpenGlVideoOutput.cs:266-287 | Med | `OnOpenGlInit` calls `_renderer.Initialise(gl)`; `OnOpenGlDeinit` disposes. On `OnOpenGlLost` (GPU preemption), `_renderer` is nulled but `_hasUploadedFrame` resets late. `SetYuvHints` writes still hit `_renderer?.SetYuvHints` (null-conditional), silently dropped. | Persist YUV matrix/range as fields (they are) and re-apply in `OnOpenGlInit`; reset `_lastAutoMatrix/Range` in `OnOpenGlLost` (A7). |
| A5 | AvaloniaOpenGlVideoOutput.cs:117-164 | Med | `SetYuvHints`/`YuvColorRange` setters write fields + call `_renderer?.SetYuvHints` from any thread. Avalonia compositor owns GL context; off-thread uniform writes without MakeCurrent → wrong colour for one frame or driver crash. | Queue pending hint changes; apply at top of next `OnOpenGlRender`. |
| A6 | AvaloniaOpenGlVideoOutput.cs:41-44 | Low | `_hasYuvHintsOverride`, `_yuvBt709`, `_yuvLimitedRange`, `_yuvColorMatrix` touched by UI and renderer threads; torn reads on 32-bit for `long` fields (`_catchupLagThreshold`, `_lastUploadedPts`). | `Volatile.Read` / pack into long for atomic access. |
| A7 | AvaloniaOpenGlVideoOutput.cs:289-296 | Med | `OnOpenGlLost` disposes `_renderer` but doesn't reset `_lastAutoMatrix`/`_lastAutoRange` → after restore `ApplyAutoYuvHintsIfNeeded` short-circuits on same-as-last check, leaving fresh renderer unconfigured. | Reset auto-hint cache to `Auto` in `OnOpenGlLost`. |
| A8 | AvaloniaOpenGlVideoOutput.cs:298-424 | Med | 125-line `OnOpenGlRender` combines state check, viewport compute, clock origin CAS, frame pull, catch-up loop, auto-hints, texture-reuse, upload, clock update, error handling, re-invalidation. Hard to reason about. | Extract `ResolvePresentationClock`, `TryPullFrameWithCatchUp`, `PresentFrame(vf, fb, w, h)`. |
| A9 | AvaloniaOpenGlVideoCloneSink.cs:95-114 | **High** | If frame pixel format ≠ RGBA32, clone calls `_converter.Convert(vf, Rgba32)` **on the Avalonia render thread** every frame. `BasicPixelFormatConverter` does scalar CPU YUV→RGB (for formats unsupported by libyuv: YUV420p10, P010, YUV444p). 4K NV12→RGBA ≈ 15 ms — hard frame-time budget blown. | Use the same multi-format GL shaders as `AvaloniaGlRenderer.UploadAndDraw` (it already supports them); drop the converter call. |
| A10 | AvaloniaOpenGlVideoCloneSink.cs:116-117 | Low | Clone sink calls `RequestNextFrameRendering()` from `OnOpenGlRender` and from `ReceiveFrame` — double invalidation (same as A1). | Same fix as A1. |
| A11 | AvaloniaOpenGlVideoOutput.cs:439-460 | Low | Dispose calls `StopAsync()` fire-and-forget; then disposes `_renderer` directly. If compositor thread is mid-`OnOpenGlRender`, renderer dispose races with a draw. | Rely on `OnOpenGlDeinit` for renderer teardown; Dispose should only stop the state machine. |
| A12 | AvaloniaOpenGlVideoOutput.cs:442-447 | Low | Parent Dispose cascades to clones via `_clones[i].Dispose()` — Avalonia `OpenGlControlBase` must be detached from the visual tree; disposing a live control while attached is a bad idea. | Document "detach before dispose"; raise `InvalidOperationException` if `IsAttachedToVisualTree`. |
| A13 | AvaloniaOpenGlVideoOutput.cs:305-307 | Low | `VisualRoot?.RenderScaling ?? 1.0` every frame reaches into `VisualRoot` without thread-safety. Avalonia guarantees consistent `VisualRoot` during `OnOpenGlRender`, so OK in practice. | Stash `_renderScaling` on UI thread via `OnPropertyChanged`; pick up in render. |
| A14 | AvaloniaOpenGlVideoOutput.cs:432-437 | Low | `OnPropertyChanged(BoundsProperty)` triggers `RequestNextFrameRendering()` — combined with A1, bounds changes incur double invalidation. | Remove after A1 fix; finally-block request covers bounds changes. |

### 4.3 GL resource leaks, context sharing, render-loop invalidation, DPI

- No explicit GL context sharing between parent and clone — each `OpenGlControlBase` gets its own context from Avalonia. On Linux/EGL cheap; on Windows/ANGLE, multiple D3D devices.
- No persistent PBO path (same as SDL3 §3.3).
- DPI change handling: re-reading `VisualRoot.RenderScaling` per frame (line 305) correctly picks up DPI changes, but viewport `Math.Round(Bounds.Width * scale)` can flicker by 1 px across DPI transitions.
- Bounds change + DPI change during resize drag produces 2-3 redundant redraws (A14).
- `RequestNextFrameRendering` is preferred over `InvalidateVisual` for frame animation (correct), but placement in `finally` defeats the point.

---

## 5. S.Media.PortAudio deep dive

(Skips B17/B18 from the main review.)

### 5.1 Clock-registry integration + `IClockCapableEndpoint` conformance

- `PortAudioOutput` implements `IClockCapableEndpoint.Clock` by returning `_clock` directly; `_clock` is created in `Open()` (line 126). Accessing `Clock` before `Open()` throws.
- `AVRouter.AutoRegisterEndpointClock` (AVRouter.cs:340-344) calls `clockEp.Clock` at `RegisterEndpoint` time. Therefore `RegisterEndpoint(portAudioOutput)` **must come after `portAudioOutput.Open(...)`**. No compile-time enforcement, no runtime-friendly error message.
- Clock priority defaults to `ClockPriority.Hardware`. Multiple PortAudio outputs on different devices register at the same priority → "last registered wins" (AVRouter.cs:463) — fragile but documented.
- `UpdateTickInterval` called once after the PA stream is open (line 127); if user overrides the clock at `Override` priority later, PortAudio clock's tick interval is wasted (C7/R13).

### 5.2 Findings

| ID | File:lines | Severity | Issue | Suggested fix |
|---|---|---|---|---|
| P1 | PortAudioOutput.cs:37, 126 vs AVRouter.cs:340-344 | **High** | `Clock` throws before `Open()`; `AVRouter.RegisterEndpoint` reads `Clock` eagerly → if user registers before opening, `InvalidOperationException` mid-registration after `_endpoints[id] = entry`. Partial state. | (a) Make `Clock` return a proxy that forwards to `_clock` once created; (b) defer `AutoRegisterEndpointClock` until endpoint's first `StartAsync`. See CH8. |
| P2 | PortAudioClock.cs:26-30 | Med | `Create(sampleRate)` constructs the clock with a `HandleRef` box; underlying `HardwareClock` SpinLock + `_lastValidPosition` init races with `SetStreamHandle` (line 33). If AVRouter reads `Clock.Position` between `Create` and `SetStreamHandle`, `Pa_GetStreamTime(0)` crashes natively. | `SetStreamHandle` should take a lock and set `_ref.Value` atomically; tick-interval update currently fires `base.SetTickInterval` without synchronization. |
| P3 | PortAudioOutput.cs:181-222 | Med | RT callback → `FillCallback.Fill` → `AVRouter.AudioFillCallbackForEndpoint.Fill` → may `ArrayPool<float>.Shared.Rent` (AVRouter.cs:1287, 1303). Rent is mostly lock-free but can allocate on pool depletion → RT thread GC hazard. | Pre-size pool at router construction; or have router pre-rent from `SetupPullAudio` and keep thread-local scratch. |
| P4 | PortAudioOutput.cs:136-151 | Low | `StartAsync` calls `Pa_StartStream` synchronously on caller's thread. On WASAPI this can take 100-300 ms (exclusive-mode init). Caller expects async. | Offload to `Task.Run` or document the blocking behaviour. |
| P5 | PortAudioOutput.cs:273-298 | Med | Dispose calls `Pa_AbortStream` then `Pa_CloseStream`, both synchronous. `_clock?.Dispose()` last — but the PA callback may still fire between `_isRunning=false` and `Pa_AbortStream` (callback doesn't check `_isRunning` beyond `FillCallback` null), potentially touching a disposed GCHandle target. | Set `_fillCallback=null` and spin on a "callback in progress" flag; wrap callback read in try/catch (already does for exceptions but not freed-handle UB). |
| P6 | PortAudioOutput.cs:87-127 | Low | `_gcHandle = GCHandle.Alloc(this)` pins for callback lifetime; if `Open` throws after alloc but before stream creation, `Free()` is called (line 112) — good. But if `_clock = PortAudioClock.Create(...)` throws (OOM), handle is leaked. | Wrap in try/catch; Free in failure arm. |
| P7 | PortAudioEngine.cs:36-42 | Low | `Terminate` is not refcounted; multiple engine instances tear each other down. `Pa_Initialize/Pa_Terminate` are process-global and ref-counted by PortAudio itself — managed `_initialized` isn't aware. | Document single-instance; or mirror PA's internal refcount. |
| P8 | PortAudioClock.cs:11-23 | Low | `HandleRef` box is a heap alloc per clock — fine. Lambda captures by reference → `Create` allocates delegate + closure each call. Negligible (once per device) but noted. | Replace closure with non-generic stateful method if ever hot. |

RT-thread discipline summary: the PortAudio callback itself is well-behaved (no alloc, no lock, try/catch). Risk is downstream inside `AVRouter.AudioFillCallbackForEndpoint.Fill` — see P3 and main review B17/B18.

---

## 6. Cross-cutting refactor suggestions

Integrated with existing Tier 0..6 roadmap and Tier 1-N..5-N NDI sub-tier.

### Tier 0 — Doc sync (add)
- Document `IClockCapableEndpoint.Clock` lifetime contract (CH8, P1).
- Document `IVideoEndpoint.ReceiveFrame` ownership (CH7, R18) more forcefully; add `[Experimental]` marker until ref-counted fan-out lands.
- Document `IVideoChannel.Subscribe` default-impl caveat (CH3).
- Document `PooledWorkQueue.Dispose` requires producer quiescence (PQ3).

### Tier 1 — Bug fixes (add R-series)
- R9 (teardown route leak), R18 (ref-count on fan-out), R6 (scratch race), R4 (skipped channel map), S1 (WindowClosed deadlock), A3/A9 (clone inefficiencies), P1 (PortAudio register-before-open), C3 (VideoPtsClock torn reads).

### Tier 2 — Ergonomic helpers (add)
- `AVRouter.RegisterEndpointAfterOpen(endpoint, Func<Task>)` helper that awaits ready signal before wiring clock/callbacks.
- `RefCountedVideoBuffer.Retain(int count)` to publish to N endpoints in one call.
- `IAudioMixer` extracted interface in `Mixing/` folder (M1) — unlocks unit testing.

### Tier 3 — Builder API
- Align `AVRouter`/`AVRouterOptions` with a builder: `new RouterBuilder().WithClock(...).AddEndpoint(...).AddInput(...).Build()`. Closes R8 (atomic registration) by construction.

### Tier 4 — Per-route & format negotiation (add)
- Per-route `LiveMode` (R23) replacing global `BypassVideoPtsScheduling`.
- Endpoint-advertised `NegotiatedAudioFormat` for push endpoints (R5).
- `AudioRouteOptions.Resampler` usable on push routes.

### Tier 5 — Phase 2 (timeline / multi-source)
- Proper PTS-aware mix for N inputs → one endpoint (R2); needed before timeline mixing.
- Mixer math extracted (M1-M5); auto-attenuation or soft-clip (R3).
- Per-pair drift EMA keying (R16).

### Tier 6 — Performance
- Persistent-mapped PBO upload path for both SDL3 and Avalonia (§3.3, §4.3).
- Avalonia clone sink: honour ref-counted buffers (A3, A9).
- Denormal flush-to-zero on push/fill threads (R3).

### New Tier 7 — GL/rendering robustness (proposed)
- Unified process-wide SDL event pump (S9).
- Off-thread GL state change serialisation (S6, A5).
- Context-lost recovery audit across SDL3 + Avalonia (A4, A7).

### Tier 1-N..5-N NDI sub-tier
- Unchanged; note R2 (mixed-PTS buffer) directly benefits NDI send (N7 et al.) once fixed.

---

## 7. Nice-to-haves

- **`AVRouterDiagnostics` event stream** (per tick) for a live UI dashboard — currently only `GetDiagnosticsSnapshot()` polling (AVRouter.cs:568-589).
- **`PtsDriftTracker.Snapshot`** struct exposed on the diagnostics snapshot so UI can plot origin drift vs time without reaching into internals.
- **SDL3 HUD**: add drift (ms) and current clock name; data already latched per tick.
- **Avalonia HUD**: parity with SDL3 — diagnostics fields exist, just no overlay.
- **`PooledWorkQueue<T>.TryEnqueueWithCap(item, cap)`** — combines `TryReserveSlot` + `EnqueueReserved` into one method; prevents the leaked-reservation foot-gun (PQ1, PQ2).
- **`VideoFrameSlot.SetAndReturnPrevious`** variant that returns the displaced frame instead of disposing — lets ref-counted buffers be released lazily by the caller. Closes A3/S8 without a full `RefCountedVideoBuffer` audit.
- **`StopwatchClock`**: option to call `TimeBeginPeriod(1)` on Windows for 1 ms `Tick` cadence (C2).
- **`AVRouterOptions.MinPreRollFramesPerInput`** (R12) for deterministic NDI/SMPTE startup timecode.
- **`IFormatCapabilities<T>.PreferredFormat`** used during `CreateRoute` to auto-insert a converter when source doesn't match any supported format but could be converted.
- **Unit tests** for `LinearResampler` boundary continuity (`_pendingFrames` edge cases) — delicate logic around line 144-162.

---

*Addendum 2 based on read-only inspection of `Media/S.Media.Core/**` (focus on `Routing/AVRouter.cs`, clocks, contracts, `PooledWorkQueue.cs`), `Video/S.Media.SDL3/*`, `Video/S.Media.Avalonia/*`, and `Audio/S.Media.PortAudio/*`. Line numbers refer to the checked-out revision and are approximate. Does not duplicate B1..B22 (main review) or N1..N23 (NDI-receive addendum); cross-references by ID where topics touch.*

