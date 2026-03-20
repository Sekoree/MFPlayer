# AudioVideoMixer (Audio-Led A/V)

Use `AudioVideoMixer` when audio should be the master timeline and video should follow it.

`VideoMixer` is single-output in the current design. For multi-output video fan-out, use `MultiplexVideoOutputEngine` + `VideoOutputEngineSink` behind one mixer output.

## Main pieces

- OwnAudio side:
  - `IAudioEngine`
  - `AudioMixer`
  - `MasterClock`
- Video side:
  - `MasterClockVideoClockAdapter`
  - `VideoTransportEngine` configured with `ClockSyncMode = AudioLed`
  - `VideoMixer`
  - `VideoStreamSource`
- Bridge:
  - `AudioVideoMixer`

## Minimal A/V example

```csharp
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Sources;

// Create/Start your OwnAudio engine first.
IAudioEngine audioEngine = CreateAudioEngineSomehow();
audioEngine.Start();

var audioConfig = AudioConfig.Default;
using var audioMixer = new AudioMixer(audioEngine, audioConfig.BufferSize);

using var videoDecoder = new FFVideoDecoder("/path/to/video.mov", new FFVideoDecoderOptions());
using var audioDecoder = new FFAudioDecoder("/path/to/video.mov", audioConfig.SampleRate, audioConfig.Channels);

using var videoSource = new VideoStreamSource(videoDecoder, ownsDecoder: false);
using var audioSource = new AudioStreamSource(audioDecoder, audioConfig, ownsDecoder: false);

var transportConfig = new VideoTransportEngineConfig
{
    PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
}.CloneNormalized();
transportConfig.ClockSyncMode = VideoTransportClockSyncMode.AudioLed;

var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
using var transport = new VideoTransportEngine(videoClock, transportConfig, ownsClock: false);
using var videoMixer = new VideoMixer(transport, ownsEngine: false);

var driftConfig = new AudioVideoDriftCorrectionConfig
{
    Enabled = true,
    CorrectionIntervalMs = 50,
    DeadbandSeconds = 0.008,
    HardResyncThresholdSeconds = 0.200,
    CorrectionGain = 0.12,
    MaxStepSeconds = 0.002,
    MaxAbsoluteCorrectionSeconds = 0.120
};

using var avMixer = new AudioVideoMixer(audioMixer, videoMixer, driftConfig, ownsAudioMixer: false, ownsVideoMixer: false);

audioSource.AttachToClock(audioMixer.MasterClock);

if (!avMixer.AddAudioSource(audioSource))
    throw new InvalidOperationException("Failed to add audio source.");
if (!avMixer.AddVideoSource(videoSource))
    throw new InvalidOperationException("Failed to add video source.");

audioSource.Play();
avMixer.Start();

// ... playback lifetime ...

avMixer.Seek(10.0); // default: AudioVideoSeekMode.Auto
avMixer.Seek(10.0, AudioVideoSeekMode.Safe);
avMixer.Pause();
avMixer.Start();

var outputCount = avMixer.GetVideoOutputs().Length;
```

## Seek modes

`AudioVideoMixer` supports per-call seek policy selection:

- `AudioVideoSeekMode.Auto`
  - fast on forward seek, safe pause/resume seek on backward seek.
- `AudioVideoSeekMode.Fast`
  - always fast seek.
- `AudioVideoSeekMode.Safe`
  - always safe pause/resume seek.

Use `Safe` for stress testing seek stability; use `Auto` for normal playback UX.

## Drift correction config quick guide

- `Enabled`
  - turn auto correction on/off.
- `CorrectionIntervalMs`
  - correction tick cadence (lower = more reactive).
- `DeadbandSeconds`
  - ignore tiny drift under this threshold.
- `HardResyncThresholdSeconds`
  - large drift fallback boundary (seek + reset correction).
- `CorrectionGain`
  - proportional response strength.
- `MaxStepSeconds`
  - max micro-adjust per correction tick.
- `MaxAbsoluteCorrectionSeconds`
  - cap on accumulated correction offset.
- `PostSeekSuppressionMs`
  - holds drift-correction ticks briefly after seek/hard-resync so decode queues can recover (post-seek suppression window).

## Practical tuning tips

- If sync converges too slowly:
  - increase `CorrectionGain` slightly (for example `0.12` -> `0.16`).
- If sync oscillates/jitters:
  - reduce `CorrectionGain` or increase `DeadbandSeconds`.
- If correction keeps saturating:
  - increase `MaxAbsoluteCorrectionSeconds` a bit, and verify decode/render throughput.

## Drift tuning table (quick presets)

| Scenario | CorrectionIntervalMs | DeadbandSeconds | HardResyncThresholdSeconds | CorrectionGain | MaxStepSeconds | MaxAbsoluteCorrectionSeconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Balanced default | 50 | 0.008 | 0.200 | 0.12 | 0.002 | 0.120 |
| Low-power / noisy timing | 60 | 0.010 | 0.220 | 0.10 | 0.0015 | 0.140 |
| Fast convergence (stable machine) | 40 | 0.006 | 0.180 | 0.16 | 0.0025 | 0.120 |
| Aggressive lab/debug | 30 | 0.004 | 0.150 | 0.20 | 0.0030 | 0.100 |

### Symptom -> adjustment

- Video slowly drifts over minutes:
  - raise `CorrectionGain` by small steps (`+0.02`) and/or reduce `CorrectionIntervalMs`.
- Video hunts around sync point (tiny oscillation):
  - increase `DeadbandSeconds` slightly and reduce `CorrectionGain`.
- Frequent hard-resync events:
  - increase `HardResyncThresholdSeconds` and verify decode queue stability.
- Correction offset pegs near max for long periods:
  - increase `MaxAbsoluteCorrectionSeconds` modestly and inspect underlying decode/render bottlenecks.

Keep changes small and test one parameter at a time to avoid introducing oscillation.

Default post-seek suppression is currently tuned to `500 ms`.

## Quick compare: choosing the right playback model

| Model | Clock authority | Best use case | Tradeoffs |
| --- | --- | --- | --- |
| `AudioVideoMixer` (audio-led) | Audio (`AudioMixer.MasterClock`) | Full media playback where lip-sync matters | Most moving parts (audio engine, transport, drift config) |
| `VideoMixer` (video-only) | Video transport local clock | No-audio playback with seek/pause/start orchestration | No audio timeline to sync against |
| Direct push (`FFVideoDecoder` -> output) | Caller loop cadence | Smallest possible prototype/debug path | No shared transport orchestration by default |

## Common pitfalls and fixes

- Video stutters when drift correction is enabled:
  - verify correction is applied only to active/routed sources in playlist scenarios.
  - if still too reactive, increase `DeadbandSeconds` and reduce `CorrectionGain`.
- Frequent hard-resyncs:
  - raise `HardResyncThresholdSeconds` slightly and check decode queue pressure (`drop`, `q`).
  - optionally raise `PostSeekSuppressionMs` if seek recovery is still jittery.
- Video looks consistently late/early but stable:
  - this is often an intentional `StartOffset` issue; confirm source offsets before tuning drift gain.
- Large correction offset growth over time:
  - increase `MaxAbsoluteCorrectionSeconds` modestly and inspect decode/render bottlenecks.
- Seek behavior feels jumpy after long playback:
  - ensure your app uses mixer `Seek(...)` and lets source-level correction reset after seek.

## Recommended tuning workflow

1. Start from `Balanced default` values.
2. Observe `v-m`, `v-a`, and `corr` for at least 30-60 seconds.
3. Change one parameter only, in small increments.
4. Re-test both normal playback and seek-heavy interaction.
5. Save per-machine/profile presets once stable.

## Diagnostics counters (new)

When using `AudioEx`/`VideoTest` diagnostics, you will now see:

- audio hard-sync counters:
  - `a_hseek` (hard-sync seek attempts)
  - `a_hsup` (hard-sync seeks suppressed during the post-seek suppression window)
  - `a_hfail` (hard-sync seek failures)
- video drift hard-resync counters:
  - `v_rseek` (hard-resync attempts)
  - `v_rok` (hard-resync successes)
  - `v_rfail` (hard-resync failures)
  - `v_rsup` (drift-correction ticks suppressed during the post-seek suppression window)

The apps also print a `[Burst10s]` summary line with aggregated counter totals and drift ranges (`v-m`, `v-a`).

