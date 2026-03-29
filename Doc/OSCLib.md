# OSCLib — Issues & Fix Guide

> **Scope:** `OSCLib` — `OSCClient`, `OSCServer`, `OSCPacketCodec`, `OSCArgs`
> **Cross-references:** See `API-Review.md` §13 for the full analysis.

---

## Table of Contents

1. [Logging Strategy](#1-logging-strategy)
2. [Lifecycle & Pipeline Composability](#2-lifecycle--pipeline-composability)
3. [Performance — High-Frequency Use Cases](#3-performance--high-frequency-use-cases)
4. [OSCRouter Design Issues](#4-oscrouter-design-issues)
5. [OSCPacketCodec — Decode Error Handling](#5-oscpacketcodec--decode-error-handling)
6. [Naming & API Consistency](#6-naming--api-consistency)

---

## 1. Logging Strategy

### Issue 1.1 — OSCLib uses MEL; S.Media.* does not

`OSCClient` accepts `ILogger<OSCClient>?` and uses `NullLogger` as a default. No `S.Media.*` project uses `Microsoft.Extensions.Logging` directly — they use diagnostic event callbacks and debug info structs instead.

Note that the native wrappers (`PALib`, `NDILib`, `PMLib`) **do** use MEL internally. So MEL is already present in the solution.

**Decision matrix:**

| Direction | Pros | Cons |
|---|---|---|
| Keep MEL in OSCLib | Consistent with native wrappers; structured logging out of the box | Inconsistent with `S.Media.*` high-level diagnostic pattern |
| Remove MEL from OSCLib | Consistent with `S.Media.*` high level | Loses structured logging; have to add a diagnostic event |
| Adopt MEL across all S.Media.* | Full consistency; industry standard | Large change; current diagnostic event system is purposefully decoupled from MEL |

**Recommended fix:** Keep MEL in `OSCLib` and adopt it uniformly across the native wrappers (already done) and eventually across `S.Media.*` high-level components. When `S.Media.*` projects add logging, they should use MEL rather than introducing a fourth logging pattern. Document the decision:

```xml
<!-- OSCLib README / XML doc note: -->
<!-- OSCLib uses Microsoft.Extensions.Logging (MEL) for diagnostic output.
     Configure via the ILogger<T> constructor parameter or register a factory
     via MediaNativeLogging.Configure(). -->
```

---

## 2. Lifecycle & Pipeline Composability

### Issue 2.1 — `OSCClient`/`OSCServer` have no `Initialize`/`Terminate` lifecycle

Unlike `PortAudioEngine`, `NDIEngine`, and `MIDIEngine`, `OSCClient` and `OSCServer` are directly constructed and disposed. This is the correct idiomatic .NET approach for network clients and servers — no change is needed here.

However, if OSC is to be used as a media pipeline element (e.g. for tempo sync, parameter automation), a thin `IOSCEventSource` interface would allow it to be composed with the mixer's event system:

```csharp
// Optional future interface — only if OSC pipeline composability is needed:
public interface IOSCEventSource : IDisposable
{
    int Start();
    int Stop();
    event EventHandler<OSCMessage>? MessageReceived;
}
```

This is not a blocking issue today. Document the current construction/disposal pattern clearly and revisit when composability is required.

---

## 3. Performance — High-Frequency Use Cases

### Issue 3.1 — `OSCPacketCodec` allocates `byte[]` on every send

Every outbound OSC packet allocates a new `byte[]` via the packet codec. For typical OSC use cases (a few control messages per second), this is inconsequential. For high-frequency A/V automation (100+ Hz, e.g. sending position updates every video frame), this generates sustained GC pressure.

**OSCArgs uses boxed `object` values**, compounding the allocation pressure for value-type arguments.

**Fix for high-frequency paths:** Provide a `Rent`/`Return` buffer API:

```csharp
// Option A: ArrayPool-based encode:
public static int Encode(OSCMessage message, ArrayPool<byte> pool, out byte[]? buffer, out int length)
{
    var estimate = EstimateSize(message);
    buffer = pool.Rent(estimate);
    try
    {
        length = EncodeInto(message, buffer.AsSpan());
        return MediaResult.Success;
    }
    catch
    {
        pool.Return(buffer);
        buffer = null;
        length = 0;
        return (int)MediaErrorCode.OSCEncodeError;
    }
}
// Caller must return buffer to pool after sending
```

```csharp
// Option B: Span-based encode into caller-provided buffer:
public static bool TryEncode(OSCMessage message, Span<byte> destination, out int bytesWritten)
{
    // ...encode directly into destination...
}
```

Option B is zero-allocation for the caller and integrates well with `IMemoryOwner<byte>` pipelines.

**Fix for OSCArgs boxing:** Use a discriminated union instead of `object`:

```csharp
public readonly struct OSCArg
{
    private readonly int _type;
    private readonly long _intValue;
    private readonly double _floatValue;
    private readonly string? _stringValue;
    private readonly byte[]? _blobValue;

    public static OSCArg Int(int v)    => new(0, intValue: v);
    public static OSCArg Float(float v) => new(1, floatValue: v);
    public static OSCArg String(string v) => new(2, stringValue: v);
    // etc.

    public int AsInt()     => (int)_intValue;
    public float AsFloat() => (float)_floatValue;
    public string AsString() => _stringValue ?? string.Empty;
}
```

This eliminates boxing for `int` and `float` arguments — the two most common types in A/V automation OSC messages.

---

### Issue 3.2 — No send queue / rate limiting

`OSCClient` sends each message synchronously on the calling thread. For A/V sync over OSC (e.g. sending timecode 30× per second), the caller must manage its own send scheduling. If the network is congested, sends block.

**Recommendation:** Add an optional async send queue:

```csharp
public sealed class OSCClient : IDisposable
{
    // ADD — optional background sender:
    public Task SendAsync(OSCMessage message, CancellationToken ct = default);

    // Keep existing synchronous send for low-frequency use:
    public void Send(OSCMessage message);
}
```

This is optional for typical usage. For the current use cases in the solution, the synchronous API is sufficient.

---

## 4. OSCRouter Design Issues

### Issue 4.1 — `OSCRouter` is public but embedded privately inside `OSCServer`

`OSCServer` creates its own private `OSCRouter _router = new()` and delegates `RegisterHandler` to it. Meanwhile `OSCRouter` is a `public` class that can be constructed independently. This creates a disconnect: you cannot pass a shared `OSCRouter` instance to `OSCServer` — the server always uses its own.

In practice this means:
- If you want to share routing logic between multiple servers, you must register the same handlers twice.
- The public `OSCRouter.DispatchAsync` API implies callers can manually drive dispatch, but `OSCServer`'s internal router is not accessible.

**Fix option A — expose the router on `OSCServer`:**

```csharp
public sealed class OSCServer : IOSCServer
{
    private readonly OSCRouter _router;

    // ADD — allow injection of a shared router:
    public OSCServer(OSCServerOptions options, OSCRouter? router = null, ILogger<OSCServer>? logger = null)
    {
        _router = router ?? new OSCRouter();
        // ...
    }

    // ADD — expose for external dispatch:
    public OSCRouter Router => _router;
}
```

**Fix option B — make `OSCRouter` internal:**

If `OSCRouter` is only ever driven by `OSCServer`, make it `internal`. Expose routing registration only through `IOSCServer.RegisterHandler`. This simplifies the public API surface.

**Recommendation:** Option B is better for API clarity. If cross-server routing is needed later, introduce a `SharedOSCRouter` or publish the `OSCServer.MessageReceived` event pattern.

---

### Issue 4.2 — `OSCRouter` uses `object _gate` instead of `Lock`

```csharp
// Current:
private readonly object _gate = new();
lock (_gate) ...

// Rest of codebase:
private readonly Lock _gate = new();
lock (_gate) ...
```

All other locking in the solution uses the .NET 9 `Lock` type for better performance characteristics. `OSCRouter` uses the older `object` pattern.

**Fix:** Replace with `Lock`:

```csharp
private readonly Lock _gate = new();
```

---

### Issue 4.3 — `OSCRouter.DispatchAsync` copies the route list on every message

```csharp
public async ValueTask<int> DispatchAsync(OSCMessageContext context, CancellationToken cancellationToken)
{
    Route[] snapshot;
    lock (_gate)
        snapshot = [.. _routes];   // ← full allocation every call
    // ...
}
```

For 30–100+ Hz dispatch (e.g. one message per video frame), this allocates a new `Route[]` for every incoming packet. At 60 Hz with 10 routes that's 600 allocations per second.

**Fix:** Use an immutable-replace pattern instead of snapshot-on-read:

```csharp
private volatile Route[] _routes = [];

// On register:
private void AddRoute(Route route)
{
    lock (_gate)
        _routes = [.. _routes, route];
}

// On dispatch — no allocation, no lock:
public async ValueTask<int> DispatchAsync(OSCMessageContext context, CancellationToken ct)
{
    var routes = _routes;  // single volatile read
    var hits = 0;
    foreach (var route in routes) { ... }
    return hits;
}
```

This is safe because `Route[]` is never mutated — only replaced atomically. Dispatch becomes lock-free.

---

### Issue 4.4 — No route count limit or duplicate detection

`OSCRouter` accepts unlimited registrations with no duplicate check. Calling `Register` twice with the same pattern and handler creates two entries. For long-running services this is a leak.

**Consideration:** Add an optional `MaxRoutes` limit (e.g. 1024) and log a warning when the same pattern is registered more than once:

```csharp
public IDisposable Register(string addressPattern, OSCMessageHandler handler)
{
    if (_routes.Length >= MaxRoutes)
        throw new InvalidOperationException($"OSCRouter route limit ({MaxRoutes}) reached.");

    // optional: warn on duplicate pattern
    if (_routes.Any(r => r.AddressPattern == addressPattern))
        _logger?.LogWarning("Pattern '{Pattern}' already has a registered handler.", addressPattern);
    // ...
}
```

---

## 5. OSCPacketCodec — Decode Error Handling

### Issue 5.1 — `TryDecode` swallows all exceptions including `OutOfMemoryException`

```csharp
public static bool TryDecode(ReadOnlySpan<byte> packetBytes, OSCDecodeOptions options,
    out OSCPacket? packet, out string? error)
{
    try
    {
        // ...
        return true;
    }
    catch (Exception ex)      // ← catches everything, including OOM and StackOverflow
    {
        packet = null;
        error = ex.Message;
        return false;
    }
}
```

The `catch (Exception)` pattern swallows catastrophic exceptions that should propagate. A malformed packet that triggers deep recursion (e.g. a nested bundle with depth 10,000) could cause a `StackOverflowException` that is silently caught and discarded.

**Fix:** Catch only `FormatException` (and possibly `ArgumentException`/`OverflowException`) — the expected decode failure modes:

```csharp
catch (FormatException ex)
{
    packet = null;
    error = ex.Message;
    return false;
}
catch (ArgumentException ex)
{
    packet = null;
    error = ex.Message;
    return false;
}
// All other exceptions propagate normally
```

The bundle depth is already limited by `OSCDecodeOptions.MaxArrayDepth`. Add a similar `MaxBundleDepth` limit and enforce it during decode to prevent deep-recursion attacks.

---

### Issue 5.2 — `EncodeToRented` has no size limit

`OSCPacketCodec.EncodeToRented` can grow its internal buffer to arbitrary size for a deeply nested bundle. There is no equivalent of `OSCServerOptions.MaxPacketBytes` on the encode path.

**Fix:** Add an encode size limit and return an error code instead of throwing:

```csharp
public static bool TryEncodeToRented(OSCPacket packet, int maxBytes,
    out RentedBuffer buffer, out string? error)
{
    var writer = new OSCBufferWriter(Math.Max(256, EstimatePacketSize(packet)));
    WritePacket(ref writer, packet);

    if (writer.Length > maxBytes)
    {
        writer.Dispose();
        buffer = default;
        error = $"Encoded packet ({writer.Length} bytes) exceeds limit ({maxBytes} bytes).";
        return false;
    }

    buffer = writer.Detach();
    error = null;
    return true;
}
```

---

## 6. Naming & API Consistency

Cross-reference to `Naming-and-Consolidation.md`:

| Issue | Section |
|---|---|
| `OSCArgs` → remove, use `OSCArgument` factories | §1.5 |
| `OSCDecodeOptions` / `OSCServerOptions` `set` → `init` | §4.1 |
| `OSCAddressMatcher` → `OSCAddressPattern` | §4.2 |
| `OSCPackets.cs` contains three types | §3.3 |

### API Design Consistency

`OSCClient` and `OSCServer` correctly expose `IOSCClient` and `IOSCServer` interfaces — good for testability. Ensure `OSCRouter` either:
- Becomes `internal` (preferred — see §4.1), or
- Gets its own `IOSCRouter` interface and is injectable into `OSCServer`.

The current state (public class, but not injectable into `OSCServer`) is the worst of both options.

---

*See also `Naming-and-Consolidation.md` §§1.5, 3.3, 4.1–4.2 for naming and structural recommendations.*

---

## 7. OSC Spec Compliance — Time Tag Handling

> **Source:** OSC 1.0 spec §"Temporal Semantics and OSC Time Tags"; cross-verified against `OSCServer.cs` `DispatchPacketAsync`.

### Issue 7.1 — Bundle time-tag scheduling not implemented (CRITICAL)

The OSC 1.0 specification states:

> *"If the time represented by the OSC Time Tag is before or equal to the current time, the OSC Server should invoke the methods immediately… Otherwise the OSC Time Tag represents a time in the future, and the OSC server must store the OSC Bundle until the specified time and then invoke the appropriate OSC Methods."*

`OSCServer.DispatchPacketAsync` dispatches **all** bundles immediately:

```csharp
var bundle = packet.Bundle!;
foreach (var child in bundle.Elements)
    await DispatchPacketAsync(child, remote, bundle.TimeTag, cancellationToken).ConfigureAwait(false);
```

The time tag is forwarded into `OSCMessageContext.BundleTimeTag` for handlers to observe, but the server itself never delays delivery for future-dated bundles.

**Decision required:** Two implementation strategies exist:

| Option | Description | Trade-offs |
|---|---|---|
| A — Server-side scheduler | `OSCServer` maintains a `SortedList<OSCTimeTag, ...>` priority queue; a timer fires pending bundles | Full spec compliance; complex; requires `DateTimeOffset`/NTP clock access |
| B — Documented handler contract | Server always delivers immediately; `BundleTimeTag` in context is the application's responsibility | Simple; spec-non-compliant but common in practice (Max/MSP, TouchOSC all allow "deliver now") |

**Recommendation:** Option B is defensible for this project's use cases (A/V sync, MIDI-over-OSC), but the behavior must be explicitly documented on `IOSCServer.StartAsync` and `OSCMessageContext.BundleTimeTag`. Add an `OSCServerOptions.IgnoreTimeTagScheduling` flag (default `true`) so future implementors can opt in to Option A without a breaking change.

---

### Issue 7.2 — Nested bundle time-tag ordering not validated

OSC 1.0 spec: *"When bundles contain other bundles, the OSC Time Tag of the enclosed bundle must be greater than or equal to the OSC Time Tag of the enclosing bundle."*

`DecodeBundle` never checks this. A receiver that does implement scheduled dispatch (§7.1 Option A) would behave incorrectly for malformed nested bundles where the inner time tag is earlier than the outer. Even without scheduling, the spec mandates rejection in strict mode.

**Fix:** In `DecodeBundle`, pass the parent time tag and validate:

```csharp
private static OSCBundle DecodeBundle(ref OSCSpanReader reader, OSCDecodeOptions options,
    OSCTimeTag? parentTimeTag = null)
{
    // ...decode marker and timeTag...
    if (options.StrictMode && parentTimeTag.HasValue && timeTag.Value < parentTimeTag.Value)
        throw new FormatException(
            $"Nested bundle timetag {timeTag.Value} precedes parent {parentTimeTag.Value}.");
    // ...decode elements, passing timeTag as parentTimeTag for nested DecodeBundle calls...
}
```

---

## 8. Security & Stability

### Issue 8.1 — No `MaxBundleDepth` limit (CRITICAL — DoS vector)

`DecodeBundle` calls `DecodePacket`, which calls `DecodeBundle` for nested bundle elements. There is **no recursion depth limit** for bundle nesting. A crafted packet with 1 000 levels of bundle nesting will cause a `StackOverflowException`, which cannot be caught.

Note: `OSCDecodeOptions.MaxArrayDepth` (default 16) limits array nesting **only**. Bundle nesting has no equivalent guard.

**Fix:** Add `MaxBundleDepth` to `OSCDecodeOptions` and pass a depth counter through the recursive decode:

```csharp
/// <summary>Maximum nesting depth for OSC bundles. Default: <c>8</c>.</summary>
public int MaxBundleDepth { get; set; } = 8;
```

```csharp
private static OSCBundle DecodeBundle(ref OSCSpanReader reader, OSCDecodeOptions options,
    int bundleDepth = 0)
{
    if (bundleDepth > options.MaxBundleDepth)
        throw new FormatException($"Maximum OSC bundle depth {options.MaxBundleDepth} exceeded.");
    // ...
    // When recursing into a nested bundle, pass bundleDepth + 1
}
```

---

### Issue 8.2 — `OSCAddressMatcher` Regex cache is unbounded

`PartRegexCache` is a `ConcurrentDictionary<string, Regex>` with no eviction policy. For long-running servers where routes are registered and deregistered dynamically (or where malformed/untrusted pattern strings are cached), this is an unbounded memory growth path.

**Fix options:**

- Cap the dictionary with a `ConditionalWeakTable` or a bounded LRU (simple option: if count > 512, skip cache and compile on the fly).
- Alternatively, build regexes eagerly at `Register` time rather than lazily at `IsMatch` time. Since routes are registered once, each regex is only constructed once — the cache becomes unnecessary.

**Recommended fix:** Compile the `Regex` inside `OSCRouter.Register` and store it on the `Route` record. `OSCAddressMatcher.BuildPartRegex` becomes an internal utility rather than a cached lookup.

---

### Issue 8.3 — Non-ASCII bytes in OSC strings silently replaced in strict mode

`Encoding.ASCII.GetString()` silently substitutes `?` (U+003F) for any byte ≥ 0x80. In `StrictMode = true`, a packet containing non-ASCII bytes in string fields is decoded without error — the resulting string silently loses data.

Per spec: *"A sequence of non-null ASCII characters"* — bytes in the range 0x80–0xFF are illegal.

**Fix:** In `ReadPaddedAsciiString`, validate the raw bytes when strict mode is active:

```csharp
// In ReadPaddedAsciiString, before decoding:
if (options.StrictMode)
{
    foreach (var b in _span[start.._offset])
        if (b > 0x7F)
            throw new FormatException($"OSC string contains non-ASCII byte 0x{b:X2}.");
}
```

This requires threading `options` into `ReadPaddedAsciiString`. Alternatively, validate the raw slice before calling `GetString`.

---

## 9. Resource Management

### Issue 9.1 — `OSCBufferWriter` leaks `ArrayPool` buffer on exception (CRITICAL)

`OSCPacketCodec.EncodeToRented` rents a buffer and calls `WritePacket`. If `WritePacket` throws (e.g., unsupported argument type, custom `Unknown` argument with unexpected data), the rented buffer is never returned:

```csharp
public static RentedBuffer EncodeToRented(OSCPacket packet)
{
    var writer = new OSCBufferWriter(Math.Max(256, EstimatePacketSize(packet)));
    WritePacket(ref writer, packet);   // ← exception here leaks the buffer
    return writer.Detach();
}
```

`OSCBufferWriter` is a `ref struct` and cannot implement `IDisposable`. A try/finally is required:

```csharp
public static RentedBuffer EncodeToRented(OSCPacket packet)
{
    var writer = new OSCBufferWriter(Math.Max(256, EstimatePacketSize(packet)));
    try
    {
        WritePacket(ref writer, packet);
        return writer.Detach();
    }
    catch
    {
        writer.ReturnToPool();   // new method: returns _buffer without detaching
        throw;
    }
}
```

Add a `ReturnToPool()` method to `OSCBufferWriter` that returns the rented buffer to `ArrayPool<byte>.Shared` without exposing it as a `RentedBuffer`.

Additionally, `WriteBundle` calls `EncodeToRented` recursively for each bundle element inside a loop. If a later element throws, the `using var rented` for that element is never disposed because the exception propagates before `Detach()` is called. The fix above covers the outer writer, but the outer `writer` buffer (from the containing call) is also leaked. The try/finally pattern must be applied consistently at every level.

---

### Issue 9.2 — `ReceivedAtUtc` captured after decode, not at network receive

`OSCMessageContext.ReceivedAtUtc` is set inside `DispatchPacketAsync`:

```csharp
var context = new OSCMessageContext(packet.Message!, remote, bundleTimeTag, DateTimeOffset.UtcNow);
```

This timestamp is captured **after** the datagram has been received, decoded, and the dispatch recursion has descended to the leaf message. For a bundle with 10 messages, the last message's `ReceivedAtUtc` can be several milliseconds later than the first — and all of them are later than the actual socket receive time.

For time-tag-aware applications (e.g., measuring network jitter), this makes `ReceivedAtUtc` unreliable.

**Fix:** Capture the receive timestamp immediately after `ReceiveAsync` returns and flow it through `DispatchPacketAsync`:

```csharp
// In ReceiveLoopAsync:
received = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
var receivedAt = DateTimeOffset.UtcNow;  // ← capture immediately

// ...decode...

await DispatchPacketAsync(packet!, received.RemoteEndPoint, null, receivedAt, cancellationToken);
```

`DispatchPacketAsync` signature gains `DateTimeOffset receivedAt` and passes it to each `OSCMessageContext`.

---

### Issue 9.3 — `WriteBundle` double-encodes nested elements (performance)

For each bundle element, `WriteBundle` calls `EncodeToRented` (which rents a buffer, encodes, returns a `RentedBuffer`) and then copies the result into the outer `OSCBufferWriter`. This means a bundle with N elements allocates N inner `RentedBuffer`s:

```csharp
foreach (var element in bundle.Elements)
{
    using var rented = EncodeToRented(element);      // allocates + rents
    writer.WriteInt32BigEndian(rented.Length);
    writer.WriteRaw(rented.Memory.Span);             // copies into outer
}
```

**Preferred fix:** Write each element directly into the outer writer at a reserved size slot:

```csharp
// Reserve 4 bytes for element size, write element, then back-patch the size:
var sizeOffset = writer.CurrentOffset;
writer.WriteInt32BigEndian(0);          // placeholder
var startOffset = writer.CurrentOffset;
WritePacket(ref writer, element);
var elementSize = writer.CurrentOffset - startOffset;
writer.PatchInt32BigEndian(sizeOffset, elementSize);   // back-patch
```

This requires `OSCBufferWriter` to expose `CurrentOffset` and a `PatchInt32BigEndian(int offset, int value)` back-patch method. Eliminates all inner `RentedBuffer` allocations.

---

## 10. Network Transport

### Issue 10.1 — Synchronous DNS resolution in `OSCClient` constructor

`OSCClient(string host, int port, ...)` calls:

```csharp
var address = Dns.GetHostAddresses(host).FirstOrDefault();
```

`Dns.GetHostAddresses` is **synchronous** and will block the calling thread for the duration of DNS resolution (potentially 30+ seconds on timeout). On thread-pool threads this can cause thread-pool starvation.

**Fix options:**

- **Option A** — Add a static async factory: `public static async Task<OSCClient> CreateAsync(string host, int port, ...)` that uses `Dns.GetHostAddressesAsync`. Keep the sync constructor but restrict it to `IPEndPoint` overload only (which doesn't need DNS).
- **Option B** — Make the `string host` constructor `[Obsolete]` and document that callers should resolve DNS themselves before passing an `IPEndPoint`.

The `IPEndPoint` overload is already available and avoids DNS entirely — Option B is simplest.

---

### Issue 10.2 — No multicast or broadcast support

OSC is widely deployed on multicast UDP (e.g., TUIO multicast group `224.0.0.1`, custom app multicast groups). The current `OSCServer` does not expose multicast join:

```csharp
_udpClient = new UdpClient(options.Port);
// ← no JoinMulticastGroup call
```

`OSCClient` similarly does not set `EnableBroadcast`.

**Fix:** Add optional fields to `OSCServerOptions`:

```csharp
/// <summary>
/// If set, the server will join this multicast group on start.
/// </summary>
public IPAddress? MulticastGroup { get; set; }

/// <summary>
/// Network interface to use for multicast (null = any).
/// </summary>
public IPAddress? MulticastLocalAddress { get; set; }
```

And in `OSCServer.StartAsync`:

```csharp
if (Options.MulticastGroup != null)
    _udpClient.JoinMulticastGroup(Options.MulticastGroup, Options.MulticastLocalAddress ?? IPAddress.Any);
```

For `OSCClient`:

```csharp
/// <summary>Enables sending to broadcast addresses. Default: false.</summary>
public bool EnableBroadcast { get; set; }
```

---

### Issue 10.3 — No TCP / SLIP framing (OSC 1.1 transport requirement)

OSC 1.1 specifies that **stream-based transports** (TCP, serial) must use **SLIP framing** (RFC 1055 with double-END encoding). The current implementation is UDP-only.

This is a known scope limitation but should be explicitly documented on `IOSCClient` and `IOSCServer`. Applications that need TCP transport (lower port overhead for high-frequency parameter automation) have no path forward without this.

**Consideration:** A `SlipFramer` utility class could be provided as a static helper without requiring full TCP client/server classes:

```csharp
public static class OSCSlipFramer
{
    public static void Encode(ReadOnlySpan<byte> packet, IBufferWriter<byte> output);
    public static bool TryDecode(ref SequenceReader<byte> input, out ReadOnlySequence<byte> packet);
}
```

This would allow callers to integrate OSC SLIP framing with their own `NetworkStream`, `System.IO.Pipelines.PipeReader`, or serial port.

---

### Issue 10.4 — `OSCClient` calls `Connect()` then bypasses it with `SendToAsync`

```csharp
_udpClient.Connect(remoteEndPoint);   // constructor — sets remote filter
// ...
await _udpClient.Client.SendToAsync(encoded.Memory, SocketFlags.None, _remoteEndPoint, ...);
```

`Connect()` on a UDP socket does two things: (1) filters *received* datagrams to the connected endpoint, (2) sets the default destination for `Send`. Using `SendToAsync` with an explicit endpoint parameter bypasses (2). The `Connect()` call is partially redundant.

More critically, `_udpClient.Client.SendToAsync` is the low-level `Socket` API. The higher-level `UdpClient.SendAsync(byte[], IPEndPoint)` would be more idiomatic. The behavior is functionally identical but the intent is clearer.

**Fix:** Either use `_udpClient.SendAsync(data, remoteEndPoint)` and drop `Connect()`, or call `_udpClient.SendAsync(data)` (no endpoint, uses connected default) and keep `Connect()`. Do not mix both.

---

## 11. API Design — Missing Details

### Issue 11.1 — No send-side logging in `OSCClient`

`OSCServer` logs received packets at `Debug` and decode failures at `Warning`. `OSCClient.SendAsync` logs **nothing** — not on success, not on failure (the `InvalidOperationException` for oversize packets is thrown but not logged).

**Fix:** Add `Debug`-level logging around sends:

```csharp
_logger.LogDebug("Sending OSC {Kind} to {Endpoint} ({Bytes}B)",
    packet.Kind, _remoteEndPoint, encoded.Length);
```

and `Warning` for the oversize throw before rethrowing.

---

### Issue 11.2 — `OSCDecodeOptions.StrictMode` interaction with `AllowMissingTypeTagString` is undocumented

`StrictMode = true` (the default) signals full spec compliance, but `AllowMissingTypeTagString = true` (also the default) quietly allows a known spec deviation regardless of `StrictMode`. A user who sets `StrictMode = true` explicitly expecting all non-compliant messages to fail will be surprised when messages without type tag strings are silently accepted.

**Fix:** Document the interaction explicitly, and consider having `AllowMissingTypeTagString` default to `false` when `StrictMode = true`. Or add a note in the XML doc:

```xml
/// <summary>
/// Allows decoding messages that omit the OSC type tag string.
/// Default: <see langword="true"/> for compatibility with older senders.
/// Note: this flag takes precedence over <see cref="StrictMode"/> for the
/// specific case of missing type tag strings.
/// </summary>
```

---

### Issue 11.3 — `OSCMessage` address validation does not check per-component forbidden characters

The `OSCMessage` constructor checks that the address begins with `/` and is not blank. It does not validate that each path component (the parts between `/` separators) is free of the characters forbidden by the OSC 1.0 spec for OSC Method and Container names:

> *Space (0x20), `#` (35), `*` (42), `,` (44), `?` (63), `[` (91), `]` (93), `{` (123), `}` (125)*

These are the same characters used as wildcards in OSC Address Patterns. A valid OSC address (as opposed to a pattern) must not contain them. Without validation, a caller can accidentally create a message whose address looks like a pattern — this address will then incorrectly match multiple routes.

**Fix:** Add address validation (strict constructor flag, or a separate `OSCAddressValidator.Validate(string)` utility):

```csharp
private static readonly SearchValues<char> ForbiddenAddressChars =
    SearchValues.Create(" #*,?[]{}");

public static bool IsValidAddress(string address)
{
    if (string.IsNullOrEmpty(address) || address[0] != '/')
        return false;

    // Address may contain '/' as separator — only validate each component
    foreach (var component in address.AsSpan(1).Split('/'))
    {
        if (component.ContainsAny(ForbiddenAddressChars))
            return false;
    }

    return true;
}
```

---

## 12. Resolution Summary — New Findings

| # | Issue | Severity | Status |
|---|---|---|---|
| 4.1 | `OSCRouter` was public but embedded privately inside `OSCServer` | ℹ️ API clarity | ✅ Fixed — made `internal` |
| 4.2 | `OSCRouter` used `object _gate` instead of `Lock` | ℹ️ Code quality | ✅ Fixed |
| 4.3 | `OSCRouter.DispatchAsync` copied route list on every message | ⚠️ Performance | ✅ Fixed — volatile immutable array, lock-free dispatch |
| 4.4 | No route count limit | ℹ️ Stability | ✅ Fixed — `MaxRoutes = 1024` default |
| 5.1 | `TryDecode` swallowed all exceptions including OOM | 🔴 Critical | ✅ Fixed — catch `FormatException`/`ArgumentException` only |
| 6.x | `OSCDecodeOptions`/`OSCServerOptions`/`OSCClientOptions` `set` → `init` | ℹ️ API clarity | ✅ Fixed |
| 7.1 | Bundle time-tag scheduling not implemented | ⚠️ Spec non-compliance | ✅ Fixed — `IgnoreTimeTagScheduling = true` documented; `IOSCServer.StartAsync` XML doc updated |
| 7.2 | Nested bundle time-tag ordering not validated during decode | ⚠️ Spec non-compliance | ✅ Fixed — strict-mode validation in `DecodeBundle` |
| 8.1 | No `MaxBundleDepth` — recursive bundle decode is unbounded | 🔴 Critical (DoS) | ✅ Fixed — `MaxBundleDepth = 8` in `OSCDecodeOptions`, enforced in `DecodeBundle` |
| 8.2 | `OSCAddressMatcher` regex cache unbounded growth | ⚠️ Memory leak | ✅ Fixed — bounded cache (≤ 256); patterns pre-compiled in `OSCRouter.Register` via `OSCAddressMatcher.Compile()` |
| 8.3 | Non-ASCII bytes in OSC strings silently replaced in strict mode | ⚠️ Spec non-compliance | ✅ Fixed — `ReadPaddedAsciiString(strictAscii)` validates bytes ≥ 0x80 |
| 9.1 | `OSCBufferWriter` ArrayPool buffer leaked on encode exception | 🔴 Critical (resource) | ✅ Fixed — try/finally with `ReturnToPool()` in `EncodeToRented` |
| 9.2 | `ReceivedAtUtc` captured after decode, not at socket receive | ⚠️ Accuracy | ✅ Fixed — timestamp captured immediately after `ReceiveAsync`, flowed through `DispatchPacketAsync` |
| 9.3 | `WriteBundle` per-element double-encode allocation | ℹ️ Performance | ✅ Fixed — back-patch approach; `OSCBufferWriter.CurrentOffset` + `PatchInt32BigEndian` |
| 10.1 | Synchronous `Dns.GetHostAddresses` blocks thread in `OSCClient` constructor | ⚠️ Thread-safety | ✅ Fixed — `OSCClient.CreateAsync()` factory; sync string-host constructor marked `[Obsolete]` |
| 10.2 | No multicast/broadcast support | ℹ️ Missing feature | ✅ Fixed — `MulticastGroup`/`MulticastLocalAddress` on `OSCServerOptions`; `EnableBroadcast` on `OSCClientOptions` |
| 10.3 | No TCP/SLIP framing (OSC 1.1 stream transport) | ℹ️ Missing feature | 📋 Out of scope — document on `IOSCClient`/`IOSCServer` |
| 10.4 | `OSCClient` mixes `Connect()` and `SendToAsync(explicit endpoint)` | ℹ️ Code clarity | ✅ Fixed — uses `_udpClient.SendAsync(encoded.Memory, ct)` (connected default) |
| 11.1 | `OSCClient` no send-side logging | ℹ️ Observability | ✅ Fixed — `Debug` log on every send, `Warning` before oversize throw |
| 11.2 | `StrictMode`/`AllowMissingTypeTagString` interaction undocumented | ℹ️ API clarity | ✅ Fixed — XML doc explains precedence; strict-mode address validation also added to `DecodeMessage` |
| 11.3 | `OSCMessage` doesn't validate forbidden characters in address components | ⚠️ Correctness | ✅ Fixed — `OSCMessage.IsValidAddress()` utility; enforced in `DecodeMessage` when `StrictMode = true` |

### Summary of all changes made

| File | Changes |
|---|---|
| `OSCOptions.cs` | `set` → `init` on all option properties; added `MaxBundleDepth`; added `IgnoreTimeTagScheduling`, `MulticastGroup`, `MulticastLocalAddress` to `OSCServerOptions`; added `EnableBroadcast` to `OSCClientOptions`; improved XML docs; `IOSCServer.StartAsync` documents time-tag scheduling behavior |
| `OSCPackets.cs` | Added `OSCMessage.IsValidAddress(string)` static utility using `SearchValues<char>` |
| `OSCAddressMatcher.cs` | Bounded `PartRegexCache` at 256 entries; added `internal Compile(string)` that pre-compiles all segment regexes into a `Func<string, bool>` for zero-allocation hot-path dispatch |
| `OSCRouter.cs` | Made `internal`; `object _gate` → `Lock`; route table changed to `volatile Route[]` with immutable-replace (lock-free dispatch); `Route` record stores compiled `Func<string, bool>` matcher; `MaxRoutes = 1024` limit enforced on `Register` |
| `OSCPacketCodec.cs` | `TryDecode` catches `FormatException`/`ArgumentException` only; `EncodeToRented` try/finally with `ReturnToPool`; `OSCBufferWriter` gains `CurrentOffset`, `PatchInt32BigEndian`, `ReturnToPool`; `WriteBundle` uses back-patch (eliminates per-element `RentedBuffer`); `DecodeBundle` enforces `MaxBundleDepth` and validates nested timetag ordering in strict mode; `ReadPaddedAsciiString` validates non-ASCII bytes in strict mode; string/symbol args use strict validation; `DecodeMessage` validates address forbidden chars in strict mode |
| `OSCClient.cs` | `CreateAsync` static factory with async DNS; sync string-host constructor marked `[Obsolete]`; uses `UdpClient.SendAsync(memory, ct)` (connected default); sets `EnableBroadcast`; `Debug`/`Warning` logging around sends |
| `OSCServer.cs` | `receivedAt` captured immediately after `ReceiveAsync`, flowed through `DispatchPacketAsync`; multicast group joined in constructor when `MulticastGroup` is set |
