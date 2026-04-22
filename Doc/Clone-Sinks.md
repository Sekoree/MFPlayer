# Clone Video Endpoints (Parent-Owned)

Clone video endpoints are secondary video targets created by a parent endpoint
(e.g. a clone of an SDL3 window used for a preview pane or a fan-out render).

> Terminology note (§1.1): the public surface uses **endpoint** uniformly.
> Earlier docs called these "sinks"; the class names still contain the word
> `CloneSink` until the §1.2 renames land.

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
- Disposing the parent endpoint disposes tracked clones.

## Practical tip

If a clone is only for temporary preview, remove the route first, then stop and
dispose to reduce render and copy work:

```csharp
router.RemoveRoute(routeId);
await clone.StopAsync();
clone.Dispose();
```

