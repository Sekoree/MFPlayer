# NDI SDK Update Analysis

**Date:** 2026-04-20  
**SDK Version:** v6.3 (based on `NDIlib_v6_3` struct in `Processing.NDI.DynamicLoad.h`)  
**Copyright header:** 2023–2026 Vizrt NDI AB

---

## Executive Summary

The new NDI SDK headers were compared against the current `NDILib` and `S.Media.NDI` implementation. **No breaking changes** were found in any existing API. All current P/Invoke signatures, struct layouts, and enum values remain correct and binary-compatible.

The SDK adds **four new API groups** (Receiver Advertiser, Receiver Listener, Sender Advertiser, Sender Listener) for Discovery Server integration. These are entirely additive; binding them is optional but recommended for full SDK coverage.

---

## 1. Existing APIs — No Changes Required

### 1.1 Initialization (`Processing.NDI.Lib.h`)

| Function | Status |
|---|---|
| `NDIlib_initialize` | ✅ Unchanged |
| `NDIlib_destroy` | ✅ Unchanged |
| `NDIlib_version` | ✅ Unchanged |
| `NDIlib_is_supported_CPU` | ✅ Unchanged |

### 1.2 Find (`Processing.NDI.Find.h`)

| Function | Status |
|---|---|
| `NDIlib_find_create_v2` | ✅ Unchanged |
| `NDIlib_find_destroy` | ✅ Unchanged |
| `NDIlib_find_get_current_sources` | ✅ Unchanged |
| `NDIlib_find_wait_for_sources` | ✅ Unchanged |

`NDIlib_find_create_t.show_local_sources` is `bool` in C (1 byte on most ABIs). The current C# binding uses `byte` — this is correct and binary-compatible.

### 1.3 Receive (`Processing.NDI.Recv.h`)

| Function | Status |
|---|---|
| `NDIlib_recv_create_v3` | ✅ Unchanged |
| `NDIlib_recv_destroy` | ✅ Unchanged |
| `NDIlib_recv_connect` | ✅ Unchanged |
| `NDIlib_recv_capture_v2` | Not bound (v2 audio frame) — OK, v3 is used instead |
| `NDIlib_recv_capture_v3` | ✅ Unchanged |
| `NDIlib_recv_free_video_v2` | ✅ Unchanged |
| `NDIlib_recv_free_audio_v2` | Not bound — OK, v3 is used |
| `NDIlib_recv_free_audio_v3` | ✅ Unchanged |
| `NDIlib_recv_free_metadata` | ✅ Unchanged |
| `NDIlib_recv_free_string` | ✅ Unchanged |
| `NDIlib_recv_send_metadata` | ✅ Unchanged |
| `NDIlib_recv_set_tally` | ✅ Unchanged |
| `NDIlib_recv_get_performance` | ✅ Unchanged |
| `NDIlib_recv_get_queue` | ✅ Unchanged |
| `NDIlib_recv_clear_connection_metadata` | ✅ Unchanged |
| `NDIlib_recv_add_connection_metadata` | ✅ Unchanged |
| `NDIlib_recv_get_no_connections` | ✅ Unchanged |
| `NDIlib_recv_get_web_control` | ✅ Unchanged |
| `NDIlib_recv_get_source_name` | ✅ Unchanged |

### 1.4 Receive PTZ/Extensions (`Processing.NDI.Recv.ex.h`)

Not currently bound. All PTZ functions are unchanged. Optional to add.

New in this header: `NDIlib_recv_ptz_exposure_manual_v2` with iris/gain/shutter_speed parameters — this was already in SDKv5 so not truly new, but it's not bound.

### 1.5 Send (`Processing.NDI.Send.h`)

| Function | Status |
|---|---|
| `NDIlib_send_create` | ✅ Unchanged |
| `NDIlib_send_destroy` | ✅ Unchanged |
| `NDIlib_send_send_video_v2` | ✅ Unchanged |
| `NDIlib_send_send_video_async_v2` | ✅ Unchanged |
| `NDIlib_send_send_audio_v2` | Not bound — OK, v3 is used |
| `NDIlib_send_send_audio_v3` | ✅ Unchanged |
| `NDIlib_send_send_metadata` | ✅ Unchanged |
| `NDIlib_send_capture` | ✅ Unchanged |
| `NDIlib_send_free_metadata` | ✅ Unchanged |
| `NDIlib_send_get_tally` | ✅ Unchanged |
| `NDIlib_send_get_no_connections` | ✅ Unchanged |
| `NDIlib_send_clear_connection_metadata` | ✅ Unchanged |
| `NDIlib_send_add_connection_metadata` | ✅ Unchanged |
| `NDIlib_send_set_failover` | ✅ Unchanged |
| `NDIlib_send_get_source_name` | ✅ Unchanged |

### 1.6 FrameSync (`Processing.NDI.FrameSync.h`)

| Function | Status |
|---|---|
| `NDIlib_framesync_create` | ✅ Unchanged |
| `NDIlib_framesync_destroy` | ✅ Unchanged |
| `NDIlib_framesync_capture_video` | ✅ Unchanged |
| `NDIlib_framesync_free_video` | ✅ Unchanged |
| `NDIlib_framesync_capture_audio` (v2 audio frame) | Not bound — OK |
| `NDIlib_framesync_capture_audio_v2` (v3 audio frame) | ✅ Unchanged |
| `NDIlib_framesync_free_audio` | Not bound — OK |
| `NDIlib_framesync_free_audio_v2` | ✅ Unchanged |
| `NDIlib_framesync_audio_queue_depth` | ✅ Unchanged |

### 1.7 Routing (`Processing.NDI.Routing.h`)

| Function | Status |
|---|---|
| `NDIlib_routing_create` | ✅ Unchanged |
| `NDIlib_routing_destroy` | ✅ Unchanged |
| `NDIlib_routing_change` | ✅ Unchanged |
| `NDIlib_routing_clear` | ✅ Unchanged |
| `NDIlib_routing_get_no_connections` | ✅ Unchanged |
| `NDIlib_routing_get_source_name` | ✅ Unchanged |

### 1.8 Utilities (`Processing.NDI.utilities.h`)

| Function | Status |
|---|---|
| `NDIlib_util_send_send_audio_interleaved_16s` | ✅ Unchanged |
| `NDIlib_util_send_send_audio_interleaved_32s` | ✅ Unchanged |
| `NDIlib_util_send_send_audio_interleaved_32f` | ✅ Unchanged |
| `NDIlib_util_audio_to_interleaved_16s_v3` | ✅ Unchanged |
| `NDIlib_util_audio_from_interleaved_16s_v3` | ✅ Unchanged |
| `NDIlib_util_audio_to_interleaved_32s_v3` | ✅ Unchanged |
| `NDIlib_util_audio_from_interleaved_32s_v3` | ✅ Unchanged |
| `NDIlib_util_audio_to_interleaved_32f_v3` | ✅ Unchanged |
| `NDIlib_util_audio_from_interleaved_32f_v3` | ✅ Unchanged |
| `NDIlib_util_V210_to_P216` | ✅ Unchanged |
| `NDIlib_util_P216_to_V210` | ✅ Unchanged |

### 1.9 Structs

| Struct | Status |
|---|---|
| `NDIlib_source_t` | ✅ Unchanged (2 pointers) |
| `NDIlib_video_frame_v2_t` | ✅ Unchanged |
| `NDIlib_audio_frame_v3_t` | ✅ Unchanged |
| `NDIlib_metadata_frame_t` | ✅ Unchanged |
| `NDIlib_tally_t` | ✅ Unchanged |
| `NDIlib_recv_performance_t` | ✅ Unchanged |
| `NDIlib_recv_queue_t` | ✅ Unchanged |
| `NDIlib_find_create_t` | ✅ Unchanged |
| `NDIlib_recv_create_v3_t` | ✅ Unchanged |
| `NDIlib_send_create_t` | ✅ Unchanged |
| `NDIlib_routing_create_t` | ✅ Unchanged |
| `NDIlib_audio_frame_interleaved_16s_t` | ✅ Unchanged |
| `NDIlib_audio_frame_interleaved_32s_t` | ✅ Unchanged |
| `NDIlib_audio_frame_interleaved_32f_t` | ✅ Unchanged |

### 1.10 Enums

| Enum | Status |
|---|---|
| `NDIlib_frame_type_e` | ✅ Unchanged |
| `NDIlib_FourCC_video_type_e` | ✅ Unchanged |
| `NDIlib_FourCC_audio_type_e` | ✅ Unchanged |
| `NDIlib_frame_format_type_e` | ✅ Unchanged |
| `NDIlib_recv_bandwidth_e` | ✅ Unchanged |
| `NDIlib_recv_color_format_e` | ✅ Unchanged (see minor note below) |

### 1.11 Library Names & Runtime

| Platform | Library Name | Runtime Env Var |
|---|---|---|
| Windows x64 | `Processing.NDI.Lib.x64.dll` | `NDI_RUNTIME_DIR_V6` |
| Windows x86 | `Processing.NDI.Lib.x86.dll` | `NDI_RUNTIME_DIR_V6` |
| macOS | `libndi.dylib` | `NDI_RUNTIME_DIR_V6` |
| Linux | `libndi.so.6` | `NDI_RUNTIME_DIR_V6` |

All match the current `NDILibraryNames.cs` values. ✅

---

## 2. New APIs — Available for Binding

These are new in SDK v6.2 / v6.3 and are **not currently bound** in the project. They are all related to **NDI Discovery Server** integration for centralized monitoring and control of senders/receivers.

### 2.1 `NDIlib_listener_event` (new struct, `Processing.NDI.structs.h`)

```c
typedef struct NDIlib_listener_event {
    const char* p_uuid;
    const char* p_name;
    const char* p_value;
} NDIlib_listener_event;
```
Used by both RecvListener and SendListener event APIs.

### 2.2 Receiver Advertiser (`Processing.NDI.RecvAdvertiser.h`) — SDK v6.2

Advertises receivers to an NDI Discovery Server for monitoring/control purposes.

| Function | Signature |
|---|---|
| `NDIlib_recv_advertiser_create` | `(const NDIlib_recv_advertiser_create_t*) → instance` |
| `NDIlib_recv_advertiser_destroy` | `(instance) → void` |
| `NDIlib_recv_advertiser_add_receiver` | `(instance, recv, allow_controlling, allow_monitoring, p_input_group_name) → bool` |
| `NDIlib_recv_advertiser_del_receiver` | `(instance, recv) → bool` |

New struct: `NDIlib_recv_advertiser_create_t` — contains `p_url_address` (Discovery Server URL).

### 2.3 Receiver Listener (`Processing.NDI.RecvListener.h`) — SDK v6.2

Discovers advertised receivers on the Discovery Server and allows remote control.

| Function | Signature |
|---|---|
| `NDIlib_recv_listener_create` | `(const NDIlib_recv_listener_create_t*) → instance` |
| `NDIlib_recv_listener_destroy` | `(instance) → void` |
| `NDIlib_recv_listener_is_connected` | `(instance) → bool` |
| `NDIlib_recv_listener_get_server_url` | `(instance) → const char*` |
| `NDIlib_recv_listener_get_receivers` | `(instance, &count) → const NDIlib_receiver_t*` |
| `NDIlib_recv_listener_wait_for_receivers` | `(instance, timeout_ms) → bool` |
| `NDIlib_recv_listener_subscribe_events` | `(instance, p_receiver_uuid) → void` |
| `NDIlib_recv_listener_unsubscribe_events` | `(instance, p_receiver_uuid) → void` |
| `NDIlib_recv_listener_get_events` | `(instance, &count, timeout_ms) → const NDIlib_recv_listener_event*` |
| `NDIlib_recv_listener_free_events` | `(instance, p_events) → void` |
| `NDIlib_recv_listener_send_connect` | `(instance, p_receiver_uuid, p_source_name) → bool` |

New enums:
- `NDIlib_receiver_type_e` — `none=0, metadata=1, video=2, audio=3`
- `NDIlib_receiver_command_e` — `none=0, connect=1`

New struct: `NDIlib_receiver_t` — describes a discovered receiver (uuid, name, input group, address, streams, commands, events_subscribed).

### 2.4 Sender Advertiser (`Processing.NDI.SendAdvertiser.h`) — SDK v6.3

Advertises senders to an NDI Discovery Server for monitoring purposes. (Note: this is distinct from the normal mDNS/Discovery Server advertisement that `NDIlib_send_create` does automatically.)

| Function | Signature |
|---|---|
| `NDIlib_send_advertiser_create` | `(const NDIlib_send_advertiser_create_t*) → instance` |
| `NDIlib_send_advertiser_destroy` | `(instance) → void` |
| `NDIlib_send_advertiser_add_sender` | `(instance, sender, allow_monitoring) → bool` |
| `NDIlib_send_advertiser_del_sender` | `(instance, sender) → bool` |

New struct: `NDIlib_send_advertiser_create_t` — contains `p_url_address`.

### 2.5 Sender Listener (`Processing.NDI.SendListener.h`) — SDK v6.3

Discovers advertised senders on the Discovery Server and allows event subscriptions.

| Function | Signature |
|---|---|
| `NDIlib_send_listener_create` | `(const NDIlib_send_listener_create_t*) → instance` |
| `NDIlib_send_listener_destroy` | `(instance) → void` |
| `NDIlib_send_listener_is_connected` | `(instance) → bool` |
| `NDIlib_send_listener_get_server_url` | `(instance) → const char*` |
| `NDIlib_send_listener_get_senders` | `(instance, &count) → const NDIlib_sender_t*` |
| `NDIlib_send_listener_wait_for_senders` | `(instance, timeout_ms) → bool` |
| `NDIlib_send_listener_subscribe_events` | `(instance, p_sender_uuid) → void` |
| `NDIlib_send_listener_unsubscribe_events` | `(instance, p_sender_uuid) → void` |
| `NDIlib_send_listener_get_events` | `(instance, &count, timeout_ms) → const NDIlib_send_listener_event*` |
| `NDIlib_send_listener_free_events` | `(instance, p_events) → void` |

New struct: `NDIlib_sender_t` — describes a discovered sender (uuid, name, metadata, address, port, groups, events_subscribed).

---

## 3. Minor / Non-Critical Differences

### 3.1 Windows-only flipped color format

`Processing.NDI.Recv.h` adds under `#ifdef _WIN32`:
```c
NDIlib_recv_color_format_BGRX_BGRA_flipped = 1000 + NDIlib_recv_color_format_BGRX_BGRA,
```
This is Windows-specific. The current `NDIRecvColorFormat` enum doesn't include it. **Not needed** unless Windows flipped-image support is desired.

### 3.2 PTZ functions not bound

`Processing.NDI.Recv.ex.h` contains ~20 PTZ control functions (zoom, pan/tilt, focus, white balance, exposure). These are unchanged from the previous SDK and are currently not bound. Optional to add if PTZ camera control is needed.

### 3.3 `NDIlib_recv_recording_*` functions deprecated

All recording control functions in `Processing.NDI.Recv.ex.h` are marked `PROCESSINGNDILIB_DEPRECATED`. NDI v4+ provides external recording via a separate application. These should **not** be bound.

---

## 4. Recommendations

### Required changes: **None**

The existing implementation is fully compatible with the updated SDK. No code changes are needed for continued operation.

### Optional additions (ordered by likely usefulness):

1. **PTZ control bindings** — If your NDI sources include PTZ cameras, binding the `NDIlib_recv_ptz_*` functions would be valuable. These are stable and unchanged.

2. **Discovery Server APIs** — The four new API groups (RecvAdvertiser, RecvListener, SendAdvertiser, SendListener) enable centralized monitoring and remote control of NDI endpoints via an NDI Discovery Server. Useful for:
   - Building NDI management/monitoring dashboards
   - Remote switching of receiver inputs
   - Centralized sender/receiver event streams
   
   These require an NDI Discovery Server to be running and are most relevant in large-scale production environments.

3. **Windows flipped color format** — Only needed if consuming from legacy Windows sources that produce vertically flipped frames.

### Not recommended:

- Do **not** bind `NDIlib_recv_recording_*` — fully deprecated, replaced by external recording.
- Do **not** bind `NDIlib_recv_capture_v2` or `NDIlib_framesync_capture_audio` (v2 audio frame) — the v3 variants are already bound and preferred.

---

## 5. Summary

| Category | Count | Action |
|---|---|---|
| Existing functions verified unchanged | ~50 | ✅ None needed |
| Existing structs verified unchanged | ~14 | ✅ None needed |
| Existing enums verified unchanged | ~6 | ✅ None needed |
| Library names verified unchanged | All platforms | ✅ None needed |
| New API: RecvAdvertiser | 4 functions | 🔵 Optional — bind when Discovery Server support is needed |
| New API: RecvListener | 11 functions | 🔵 Optional — bind when Discovery Server support is needed |
| New API: SendAdvertiser | 4 functions | 🔵 Optional — bind when Discovery Server support is needed |
| New API: SendListener | 10 functions | 🔵 Optional — bind when Discovery Server support is needed |
| New structs | 7 | 🔵 Only needed for new APIs above |
| New enums | 2 | 🔵 Only needed for RecvListener |
| PTZ control | ~20 functions | 🔵 Optional |
| Deprecated recording | ~7 functions | ⛔ Do not bind |

