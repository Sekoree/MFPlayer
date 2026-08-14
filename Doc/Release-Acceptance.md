# Release acceptance

What a release candidate is proven by AUTOMATION, and the short reproducible HARDWARE checklist
covering what automation cannot see (2026-08-14 review, F-20). The split matters: the automated
matrix runs on every push and is not negotiable; the hardware checklist runs once per release
candidate on a real rig, and its results (with hardware/driver versions) are attached to the
release notes.

## Automated matrix (every push, `.github/workflows/build.yml` + `codeql.yml`)

| Surface | linux-x64 | win-x64 | What it proves |
|---|---|---|---|
| Solution build, warnings-as-errors, full recompile | ✔ | ✔ | No suppressed analyzer/AOT drift |
| Full test sweep (serialized hosts, blame-hang, retry-flagged) | ✔ | ✔ | ~4,400 unit/integration/headless-UI tests |
| NativeAOT smoke (Mond error path executed in native image) | ✔ | ✔ | Trim/AOT compatibility is real, not declared |
| ABI plugin + software-GL layer-surface smoke (xvfb) | ✔ | — | Native plugin host + compositor GL path |
| Subtitle decode smoke (FFmpeg → libass → pixels) | ✔ | — | The full text pipeline against committed fixtures |
| Outbound C ABI (`s_media_player`) empty-show smoke | ✔ | — | The interop deliverable actually drives a show |
| HaCue2 seed + status check (expected exit 1) | ✔ | ✔ | Fixture generator and status pass agree |
| Native manifest presence + load-probe + runtime-version gates | ✔ | ✔ | Shipped natives exist, load, and report the pinned versions (libass ≥ 0.17.5, miniaudio 0.11.25, projectM 4.1.6, avcodec ABI from `ffmpeg.lock`) |
| Native SBOM generated from `ffmpeg.lock` + pins | ✔ | ✔ | Every artifact carries auditable provenance |
| **HaPlay exact-artifact launch gate** (backend enumeration + first frame + clean teardown) | ✔ | ✔ | The uploaded bytes start |
| **HaCue2 exact-artifact launch gate** (audio backend enumerated + first frame + clean teardown) | ✔ | ✔ | The uploaded bytes start |
| CodeQL C# analysis | ✔ | n/a | Static security floor |
| Doc-truth assertions (`NativeTruthDocTests`) | ✔ | ✔ | Docs cannot contradict `ffmpeg.lock` |
| Responsive/accessibility floors (headless) | ✔ | ✔ | HaCue2 title bar @900px, HaPlay command bars @540px view, automation names on safety controls |

Not covered by automation, by construction: real GPU drivers, real audio interfaces, hot-plug,
projector EDID behavior, NDI over a real network, physical control surfaces, screen readers, DPI
scaling on real displays. That is the checklist below.

## Hardware checklist (once per release candidate)

Record for every run: machine, GPU + driver version, audio interface + driver, OS/compositor
(X11/Wayland/Windows build), NDI SDK version on receivers, controller firmware. A checked box with
no versions recorded does not count.

### Show safety (both apps; do these FIRST)

- [ ] GO fires the standby cue; the next cue arms; the displayed "current / next" identities match
      what actually sounds and shows.
- [ ] Panic (hold-to-stop in HaCue2) takes everything down; a GO immediately after panic behaves.
- [ ] A targeted cue/group stop leaves an UNRELATED list's in-flight fire running (F-04 semantics,
      on hardware timing).
- [ ] GO on list B is instant while list A is mid-open on a cold network file (F-08 semantics).
- [ ] Lock mode: authoring blocked, notes editable, GO/preview/audition untouched.
- [ ] Save under load; kill the process; relaunch offers recovery naming the project and time;
      recovery restores.

### Outputs under churn (while media is PLAYING)

- [ ] Unplug the active projector/display mid-clip: the app reports the loss (no hang, no crash);
      replug: output recovers or is re-openable from the UI.
- [ ] Change an output's layout/warp live: the running composition follows without a restart.
- [ ] Unplug/replug the audio interface: the affected lines report; other lines keep playing;
      the device re-attaches or re-selects without an app restart.
- [ ] Pull the NDI network cable mid-send: sender-side health reports; receivers resync on replug.
- [ ] A GPU-context loss (monitor sleep/resume on the output head) does not wedge the canvas.

### Control surfaces

- [ ] MIDI controller hot-plug: mappings survive unplug/replug (name-based re-match).
- [ ] Remote API from a real controller (Companion or curl from another box): status, GO, stop,
      panic; a token change locks the old client out; HaPlay's LAN mode is token-gated (F-15) and
      the tokenless exception is loud.
- [ ] MTC chase: input off / no signal / undecodable / held read distinctly on the chip.

### Operator surface

- [ ] 125% and 150% display scaling: both shells fully usable at their minimum window sizes.
- [ ] A keyboard-only pass through each app's primary flow (load → arm → GO → stop → save).
- [ ] A screen-reader pass over the transport and safety controls (names announced, not glyphs).
- [ ] Long project/file names: title bars and status bars truncate, never push controls out.

### Performance floor

- [ ] The show's real composition shapes hold their target frame rate on the rig's GPU
      (Diagnostics screen 15: achieved/target green, backend column names the expected backend,
      no amber "pass-through" effects).
- [ ] Cold start to interactive shell within the app's own logged thresholds on the show machine.

## Failure policy

A failed safety or output-churn item blocks the release. A failed operator-surface item ships only
with a release-notes caveat and an issue. Performance-floor failures block if the rig is the show's
actual hardware.
