# HaViz: removing the default-audio-device dependence (clocking rework)

Status: findings + proposal, 2026-07-27. Code references are against commit `c56f0619`
(`test-enhancements`, since merged to master as `a83d1b74` — references apply to current
master as-is).

## TL;DR

The suspicion is correct: HaViz should need no local audio output, but **both heads open one anyway
and use it as the playback clock**. The engine itself (render loop, NDI pump) is already
wallclock-paced and device-free — only the *audio feed* path is slaved to the local device. The
framework already ships every piece needed to fix this (wallclock router clock is even the
default; the device only takes over via auto-promotion), so this is a wiring change, not a
framework rework. A 10s pacing benchmark confirms a software wallclock pacer is well within
audio-callback tolerances.

## What is actually device-dependent today

Four clocks run in a HaViz desktop session; three are already correct:

| Clock | Source | Device-dependent? |
|---|---|---|
| projectM render loop | `Stopwatch` deadline loop, own thread (`ProjectMOffscreenRenderer.cs:238-385`) | No |
| NDI video pump + PTS | `Stopwatch` (`VizNdiEngine.cs:240-307`, PTS at `:265`) | No |
| NDI SDK clock | `NDIOutput` defaults `clockVideo/clockAudio = true` (`NDIOutput.cs:106`) | No (but redundant, see below) |
| **Decode/mix/viz-tap pacing** | **Local PortAudio device ring drain** | **Yes — the problem** |

The dependence chain (desktop):

1. `DesktopMiniPlayer.Play` → `MediaPlayer.OpenAudio(...)` (`DesktopMiniPlayer.cs:103`) always
   attaches a real device output as `"_master"`; `deviceId == null` (the UI's "System default"
   entry) resolves to `Pa_GetDefaultOutputDevice()` (`MediaPlayer.cs:595-601` →
   `PortAudioOutput.cs:219`). The stream is **started immediately**, muted or not.
2. `AudioRouter.AutoWirePrimaryOutputIfNeeded` promotes the first `IClockedOutput` to primary,
   calls `SlaveTo(...)` and re-masters the `MediaClock` to it (`AudioRouter.Playback.cs:80-113`).
   This displaces the router's **default** `WallClockRouterClock` (`AudioRouter.cs:187`).
3. The router loop's only pacing gate is `_clock.WaitForNextChunk` → `WaitForCapacity` on the
   PortAudio ring (`AudioRouter.cs:1441`, `OutputSlavedRouterClock.cs:68-70`), plus producer
   backpressure against the primary pump (`AudioRouter.cs:1526-1528`).
4. The viz/NDI tap (`"viz-tap"`) is a **non-primary** pump: it receives PCM at whatever rate the
   sound card drains, and drops on overflow (`AudioRouter.cs:1524`).

So: decode rate, mix rate, viz PCM rate, `Position`, `IsRunning`, and end-of-track detection are
all timetabled by a sound card whose audio nobody is supposed to hear.

The "Mute local output" commit (`6fdceabc`, `DesktopMiniPlayer.cs:77-95`) zeroes the `_master`
route gain — an honest workaround (its own comment says the clock is why it can't detach), but the
default device is still opened, started, and drained on a box whose only job is to feed NDI.

Failure modes this causes:

- **Headless box (no output device): file playback is impossible.** `PortAudioOutput.cs:219-221`
  throws, `MainViewModel` error-skips, and after 3 consecutive errors playback stops entirely
  (`MainViewModel.cs:65,335-348`). Line-in → viz still works (input is a legitimate source).
- Default-device changes/pulseaudio suspend affect show timing on the NDI output.
- Android (`MediaCodecMiniPlayer.cs`): an `AudioTrack` is always created (`:467-511`), the
  **blocking `AudioTrack.Write` is the decode loop's clock** (`:456`), local "off" is
  `SetVolume(0)` (`:225`), and playlist advance waits for the *hardware playback head* to reach
  the end (`DrainToEnd`, `:515-524`) — on a muted device.

## What the framework already provides (all unused by HaViz)

- `MonotonicWallClock` (`S.Media.Time/MonotonicWallClock.cs:11`) — pausable Stopwatch-backed
  `IPlaybackClock`, documented for exactly the "no output to slave to" case.
- `WallClockRouterClock` — already the router default; only displaced by auto-promotion.
- `AudioRouter.AutoWirePrimary` (`AudioRouter.Playback.cs:17`, public bool, default true) — **the
  single switch** that causes device promotion.
- `AudioRouter.SlaveToIngest(IPlaybackClock)` (`AudioRouter.cs:810-823`) and
  `PlaybackSlavedRouterClock` — pace the router from any clock.
- `MediaPlayerOpenOptions` + `IncludeAudioRouter` (`MediaPlayer.cs:752-758`) — already wires a
  `DiscardingAudioOutput` so a source is consumed and the clock advances **with no device at
  all**. This is the exact shape HaViz needs; `OpenAudio` is the wrong entry point.
- `NDIOutput` pacing knobs (`clockVideo/clockAudio/minimumVideoSubmitSpacing`,
  `NDIOutput.cs:92-107`) and `NDIEgressPresentationTimeline` for a shared A/V anchor.

## Proposal

### P1 — Desktop: clockless-by-default mini player

Replace `MediaPlayer.OpenAudio` in `DesktopMiniPlayer` with an open path that:

1. Uses `MediaPlayerOpenOptions`/`IncludeAudioRouter` so the player runs with the router's
   wallclock pacing (`WallClockRouterClock` or `SlaveToIngest(new MonotonicWallClock())`) and a
   discarding sink instead of a mandatory `"_master"` device output. The viz-tap becomes the
   *only* mandatory output.
2. Attaches a real device output **only when local monitoring is enabled**, as a secondary,
   non-clock output: either keep `AutoWirePrimary = false` so attach/detach never re-masters the
   clock, or explicitly `SlaveToIngest` first. Toggling "local output" then genuinely
   opens/closes the device instead of gain-muting a stream that keeps running.
3. Derives end-of-track from the wallclock-mastered `MediaClock` (no behavior change needed in
   `Poll`, the clock keeps advancing — this also removes the `StartGraceMs` fragility, since a
   wallclock master can't report "not running" while the device warms up).

Risk note: when local monitoring *is* enabled, the device output becomes a resample/drift
consumer rather than the pacer. The router already handles non-primary pumps with
drop-on-overflow; for a monitor path that is acceptable (and could later get a small
rate-adaptive resampler if drift clicks ever matter). The show-critical path (NDI) stops being
hostage to the monitor path — the right trade for this app.

### P2 — Android: same inversion

- Pace the decode loop with the same absolute-deadline wallclock scheme the desktop engine
  already uses (deliver `_floatScratch` to the tap on schedule), and make the `AudioTrack`
  optional: create/start it only while local monitoring is on, `Write` with non-blocking mode (or
  a bounded queue) so it can never become the loop clock.
- Replace `DrainToEnd`'s hardware-head wait with the wallclock position (track ends when the
  scheduled position reaches the decoded duration; keep the head wait only when monitoring is
  audible).
- Drop the unconditional `EnsureAudioTrack` in `DeliverPcm` (`:392`) so a HAL-less device can
  still run viz→NDI.

### P3 — Pick ONE video pacer

Today the NDI video path is double-clocked: the engine's Stopwatch deadline loop *and* the NDI
SDK (`clockVideo = true`) both throttle. That works (SDK clock only kicks in if the engine runs
hot) but hides latency: a slow SDK send blocks the pump thread and the Stopwatch loop then
"catches up". Recommendation: keep the engine Stopwatch as the single pacer and create the sender
with `clockVideo: false`; keep `clockAudio: true` only if the audio submit path stays
threaded-off the pump (today `SubmitPcm` can block a decoder thread on the SDK audio clock —
worth moving behind a small SPSC ring + dedicated sender thread regardless).

### P4 — Cleanups found along the way

- `HaViz.Desktop.csproj:70` references `S.Media.Audio.MiniAudio` but no HaViz source uses it —
  dead reference, remove.
- `MediaPlayer.OpenAudio` with an **empty device list** silently passes `device?.Id == null` down
  and lets `PortAudioOutput` throw late (`MediaPlayer.cs:595-601`). If `OpenAudio` keeps existing,
  fail fast with a clear "no output devices" error — or better, fall back to the discarding-sink
  path with a wallclock master (that would make *every* OpenAudio caller headless-safe, HaPlay
  included).
- Desktop input auto-select uses the *default input* (`MainViewModel.cs:384-388`); fine as a
  default but should be persisted per box (a show machine's "default" moves with USB churn).

## Benchmark evidence (this box, Release, .NET 10)

A software pacer standing in for the device callback — 512 frames @ 48kHz (10.667ms period),
10s runs, absolute deadlines (`next = start + n·period`):

| Strategy | p50 jitter | p99 jitter | max | cumulative drift |
|---|---|---|---|---|
| `Task.Delay` to absolute deadline | 0.43 ms | 0.92 ms | 2.03 ms | **-0.47 ms** (none) |
| `Task.Delay(-1.5ms)` + SpinWait | ~0 ms | ~0 ms | 2.48 ms | 0 (burns ~14% of a core) |
| `PeriodicTimer(10.667ms)` | 312 ms | 618 ms | 625 ms | **-625 ms** — unusable |

Conclusions:
- Plain `Task.Delay` with absolute deadlines is sufficient for a wallclock audio pacer: sub-ms
  typical error, no drift; a 2-chunk ring absorbs the worst case. No spinning needed.
- **Never** use `PeriodicTimer` for fractional-ms audio cadence — it quantizes the period and the
  error compounds (~0.67ms *per tick* here).
- `MonotonicWallClock` + `WallClockRouterClock` already implement this scheme in-framework, so P1
  needs no new pacing code at all.

## Suggested order

1. P1 (desktop wiring swap) — smallest change, kills the headless-box failure and the mute hack.
2. P4 fail-fast/fallback in `MediaPlayer.OpenAudio` — benefits HaPlay too.
3. P2 (Android) — same idea, more code movement (blocking-write loop restructure).
4. P3 (single video pacer + audio submit ring) — polish, do with the NDI latency pass.

## Implementation status (2026-07-27)

**P1 and P4 (first two bullets) are implemented.** `DesktopMiniPlayer` now opens via
`MediaPlayer.Open` (wallclock router pacing, discarding sink, free-running `MediaClock`),
sets `AutoWirePrimary = false`, and treats the viz-tap as the only mandatory output. Local
monitoring genuinely attaches/detaches a device output (`"monitor"`, never the clock;
click-free fade + deferred detach in `Poll`). `MainViewModel` builds the player without a
backend (PortAudio failure no longer sinks the registry), the dead MiniAudio project
reference is gone, `StartGraceMs` is gone (no hardware warm-up to wait for), and
`MediaPlayer.OpenAudio` fails fast with a clear message on an empty device list.
**P2 (Android) and P3 (video pacer) implemented and device-verified 2026-07-27** on the
NW-A300: `MediaCodecMiniPlayer` now paces PCM delivery on absolute wall-clock deadlines
(re-anchoring instead of burst catch-up after pause/stall/format change); the `AudioTrack`
exists only while "Play on this device" is on (non-blocking writes, creation failure is
non-fatal — a HAL-less device keeps feeding NDI), and `DrainToEnd`/playlist advance run on
the wall-clock schedule, with the hardware-head wait only for an attached monitor's audible
tail. `VizNdiEngine` now creates its sender with `clockVideo: false` (the pump's Stopwatch
loop is the single video pacer; audio keeps the SDK throttle). On-device: playback with no
AudioTrack at all, live monitor attach/detach mid-track, and track skip all verified with a
clean error log. **Still open: input-device persistence (desktop, minor).**
