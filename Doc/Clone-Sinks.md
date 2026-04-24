# Clone Video Endpoints (Parent-Owned)

Clone video endpoints are secondary video targets created by a parent endpoint
(e.g. a clone of an SDL3 window used for a preview pane or a fan-out render).

> Terminology note (§1.1): the public surface uses **endpoint** uniformly.
> Earlier docs called these "sinks"; the class names still contain the word
> `CloneSink` until the §1.2 renames land.

## Wiring contract (§3.40a / S2, S4)

A clone endpoint is a **standalone endpoint** from the router's point of view.
The parent does **not** tee frames to it internally; instead, the user
registers the clone on the router like any other endpoint and creates a
per-source route so the router pushes frames to it through the normal
`ReceiveFrame` path. This is model (b) in the §3.40a review finding.

- A clone's `CreateCloneSink(...)` factory is a convenience that enforces
  backend compatibility (pixel format, thread model, renderer assumptions).
- The parent keeps a tracking list of the clones it issues so
  `parent.Dispose()` **cascade-disposes** the clones. This is a safety net
  for teardown — callers who need finer control should unregister and
  dispose clones explicitly before disposing the parent (see below).
- `ReceiveFrame` on an already-disposed clone is a no-op (the clone checks
  its `_disposed` flag), so a parent-dispose-during-active-route is safe
  but noisy — the router may log a few push-tick exceptions before it
  realises the endpoint is gone.

## Why this model

- Enforces backend compatibility (pixel format, thread model, renderer assumptions).
- Keeps lifecycle safe: the parent endpoint can track and dispose all clones.
- Simplifies app code: no manual clone constructor wiring.

## Avalonia

```csharp
var clone = avaloniaEndpoint.CreateCloneSink("Preview");
await clone.StartAsync();

var cloneId = router.RegisterEndpoint(clone);
var routeId = router.CreateRoute(videoInputId, cloneId);
```

## SDL3

```csharp
var clone = sdlEndpoint.CreateCloneSink(title: "Program", width: 960, height: 540);
await clone.StartAsync();

var cloneId = router.RegisterEndpoint(clone);
var routeId = router.CreateRoute(videoInputId, cloneId);
```

## Ownership and disposal

- Treat clones as parent-owned.
- Stop clones before teardown when possible.
- Disposing the parent endpoint cascade-disposes tracked clones (safety
  net); the router continues to push frames to them until it observes the
  route-level side effect of dispose, so unregistering the clone first is
  always the cleaner path.

**Recommended teardown order** (when you want zero push-tick warnings):

```csharp
router.RemoveRoute(cloneRouteId);
router.UnregisterEndpoint(cloneId);
await clone.StopAsync();
clone.Dispose();
// ...later, when the parent really goes away:
parent.Dispose();            // cascade is a no-op; clone is already disposed
```

## Practical tip

If a clone is only for temporary preview, remove the route first, then stop and
dispose to reduce render and copy work:

```csharp
router.RemoveRoute(routeId);
await clone.StopAsync();
clone.Dispose();
```

