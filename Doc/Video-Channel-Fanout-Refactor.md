# Video Channel Fan-out Refactor

Replace the current shared-ring video channel (one `Channel<VideoFrame>` drained by all endpoints) with a per-subscriber fan-out: decoder publishes each frame once, every subscribed consumer gets its own bounded queue with its own overflow policy, and `VideoFrame` pixel buffers are ref-counted so the pool rental is returned exactly when the last subscriber releases. This removes the pull-vs-push race that currently pegs SDL3/Avalonia pull output at 12 fps on 24 fps content, eliminates the `LastVideoFrame` / `HasPullVideoEndpoint` / `_pushVideoPending` borrow mitigations, and decouples slow consumers from fast ones.

## 1. Goals and non-goals

**Goals**
- Eliminate shared-ring contention between pull and push consumers.
- Remove mitigation bookkeeping: `InputEntry.LastVideoFrame`, `InputEntry.HasPullVideoEndpoint`, the "borrow" branch in `PushVideoTick`, the `RebuildVideoRouteSnapshot` `HasPullVideoEndpoint` recompute.
- Keep `IVideoChannel` callable by existing code paths during the migration via a compat shim.
- Preserve per-endpoint drift-tracking, early-gate caching, catch-up loop, and `_lastPresentedFrame` re-present on empty.

**Non-goals**
- No changes to audio routing (`IAudioChannel`, `PushAudioTick`, `AudioFillCallbackForEndpoint`).
- No changes to endpoint interfaces (`IPullVideoEndpoint`, `IVideoEndpoint`, `IPullAudioEndpoint`, `IAVEndpoint`).
- Do not unify push and pull — the split is correct.
- No changes to `VideoFrame`'s public record shape beyond the semantic meaning of `MemoryOwner` (still `IDisposable?`).

## 2. Proposed API

### 2.1 `IVideoSubscription`

```csharp
public interface IVideoSubscription : IDisposable
{
    int  FillBuffer(Span<VideoFrame> dest, int frameCount);
    bool TryRead(out VideoFrame frame);
    int  Count { get; }
    int  Capacity { get; }
    bool IsCompleted { get; }
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
}
```

On `Dispose`: unregister from the owning channel, drain queued frames (release refcounts).

### 2.2 `VideoSubscriptionOptions`

```csharp
public enum VideoOverflowPolicy { Wait, DropOldest, DropNewest }

public sealed record VideoSubscriptionOptions(
    int Capacity = 4,
    VideoOverflowPolicy OverflowPolicy = VideoOverflowPolicy.DropOldest,
    string? DebugName = null);
```

Recommended per-consumer defaults applied by `AVRouter`:
- Push endpoints (NDI, clone sinks): `Capacity=4`, `DropOldest` — slow/bursty push must not stall the decoder.
- Pull endpoints (SDL3, Avalonia): `Capacity=8`, `Wait` — pull is vsync-paced; buffer ~8 frames ahead. Expose via `VideoRouteOptions` so apps needing "never stall the decoder" can pick `DropOldest`.

### 2.3 `IVideoChannel.Subscribe`

Add `IVideoSubscription Subscribe(VideoSubscriptionOptions)`. Keep `FillBuffer` as a deprecated shim on a lazy default subscription during phases 1–3.

## 3. Reference-counted `VideoFrame` buffers

`RefCountedVideoBuffer : IDisposable` wraps the existing `IDisposable?` owner. `Dispose()` aliases `Release()`. `Retain()` increments. `Release()` decrements and, on zero, disposes the inner rental. `MemoryOwner` stays typed `IDisposable?` so existing `frame.MemoryOwner?.Dispose()` callers keep working unchanged.

Frames with `MemoryOwner == null` stay null; publisher's "acquire N refs" is a no-op in that case.

## 4. Publisher-side change in `FFmpegVideoChannel`

- Replace the single `Channel<VideoFrame>` + `_ringReader`/`_ringWriter` with `ImmutableArray<VideoSubscription> _subs` + `Lock _subsLock`.
- `Subscribe` creates a new `VideoSubscription` (internal class), atomically appends to `_subs`, returns.
- Decoder write (inside `DecodePacketAndEnqueue`):
  1. Snapshot `var subs = _subs;`.
  2. If empty, release and drop.
  3. Acquire N-1 extra refs via `RefCountedVideoBuffer.Retain()`.
  4. For each sub, `sub.TryPublish(frame, ct)`; on fail, release.
- `VideoSubscription` backed by `Channel.CreateBounded<VideoFrame>` with `SingleWriter=true, SingleReader=true, FullMode=Wait`. DropOldest/DropNewest implemented manually (not via `BoundedChannelFullMode`, which silently drops without giving us the evicted frame).
- Seek / Dispose / ApplySeekEpoch iterate subs and call `sub.Flush()` (drain with release).
- Back-pressure is now per-subscriber: a slow push consumer on `DropOldest` no longer stalls the decoder; pull on `Wait` may still stall the decoder on its own queue only.

## 5. Consumer-side migration (`AVRouter`)

- `RouteEntry.VideoSub` — new field, one sub per (input, endpoint) pair.
- Subscribe in `CreateVideoRoute` with per-kind defaults; dispose in `RemoveRouteInternal`.
- `PushVideoTick` reads `route.VideoSub.TryRead(...)` — delete borrow path, `HasPullVideoEndpoint` early-return, `routerOwnedFrames` per-tick cache, `_pushOneFrameBuf`.
- `VideoPresentCallbackForEndpoint.TryPresentNext` reads `route.VideoSub.TryRead(...)` — delete `inp.LastVideoFrame` publication, `_oneFrameBuf`.
- Rekey `_pushVideoPending` / `_pushVideoDrift` from `InputId` to `RouteId` (N push endpoints on one input each get their own pace).
- Delete `InputEntry.LastVideoFrame`, `InputEntry.HasPullVideoEndpoint`, `RebuildVideoRouteSnapshot` HasPull recompute.
- Keep: per-endpoint `_pendingFrame` / `_lastPresentedFrame` / drift tracker / catch-up loop — those are correctness machinery, not contention fixes.

## 6. Thread-safety

- Subscription queue: bounded `Channel<VideoFrame>` with single-writer/single-reader; the `SingleWriter=true` holds because only the decoder publishes to a given sub.
- Publisher list mutation: `ImmutableArray` atomic reads on the decoder side; `_subsLock` serialises subscribe/unsubscribe. No lock held across `WriteAsync`.
- DropOldest implementation: `while (!TryWrite(frame)) { if (TryRead(out old)) old.MemoryOwner?.Dispose(); }` (bounded by sub.IsCompleted guard).
- Dispose ordering: remove from `_subs`, `TryComplete()` the writer, drain the reader with `Release()`.

## 7. Migration / rollout

- **Phase 1** — add `IVideoSubscription`, `VideoSubscriptionOptions`, `RefCountedVideoBuffer`. Implement `Subscribe` on `FFmpegVideoChannel` / `NDIVideoChannel`. Keep `FillBuffer` as a default-sub shim. All existing tests pass.
- **Phase 2** — migrate push path to use per-route subs. Pull still on legacy shim. Confirm SDL3 is no longer starved by NDI via the two-consumer fan-out test.
- **Phase 3** — migrate pull path. Delete `LastVideoFrame` / `HasPullVideoEndpoint` / borrow.
- **Phase 4** (optional) — remove shim; require `Subscribe` at compile time.

## 8. Tests to add

1. Two-consumer fan-out: pull@60 + push@100 on 24/60 fps synthetic — both get 100 % of frames.
2. Race-free invariant: `published == received_A + overflow_A` per sub.
3. Slow consumer on `DropOldest` doesn't stall publisher.
4. Seek flush empties all subs, returns all pool rentals.
5. Subscription `Dispose` releases queued frames.
6. Refcount invariant: each rental released exactly once after N subs consume.
7. Ring smoke regression via the compat shim.

## 9. Open questions / answers chosen

1. Keep `IVideoChannel`, add `Subscribe`; defer `IVideoPublisher` split to phase 4. ✅
2. `VideoFrame.MemoryOwner` stays typed `IDisposable?`; concrete value becomes `RefCountedVideoBuffer` whose `Dispose()` forwards to `Release()`. Zero source churn. ✅
3. Only `AVRouter` and the FFmpeg tests call video `FillBuffer`. Safe to deprecate. ✅
4. Leave audio alone (non-goal). ✅
5. Default pull sub = `Wait, cap=8`; expose via `VideoRouteOptions` for `DropOldest`. ✅
6. `NDIVideoChannel` keeps its internal ring as its default sub; minimise NDI churn. ✅

