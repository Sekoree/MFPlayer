# FirstAudioPlayback.Smoke

Minimal end-to-end smoke runner for first audible playback validation:

- `S.Media.FFmpeg` decode/read path (`FFMediaItem` -> `FFAudioSource`)
- `S.Media.PortAudio` output push path (`PortAudioEngine` -> `IAudioOutput`)

This is intentionally a small manual loop runner for local bring-up and diagnostics.

## Build

```fish
dotnet build Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj
```

## Run

```fish
dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --input "/home/seko/Videos/shootingstar_0611_1.mov" --seconds 8 --decode-threads 6 --frames-per-read 1024 --engine-buffer-frames 1024
```

You can also pass a URI:

```fish
dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --input "file:///home/seko/Videos/shootingstar_0611_1.mov"
```

## Useful Options

- `--device-index <int>` choose output device index from engine list (default `-1`, discovered default output)
- `--host-api <id-or-name>` filter PortAudio devices by host API (`alsa`, `jack`, `wasapi`, `coreaudio`, ...)
- `--ffmpeg-root <path>` set FFmpeg native library root for `FFmpeg.AutoGen` binding
- `--decode-threads <int>` FFmpeg decode threads (`0` = auto)
- `--max-queued-packets <int>` FFmpeg packet queue size
- `--seconds <double>` loop duration target
- `--frames-per-read <int>` audio chunk size per read
- `--engine-buffer-frames <int>` `AudioEngineConfig.FramesPerBuffer` for PortAudio stream open
- `--list-devices` print host APIs and output devices, then exit

## Notes

- This validates first playback wiring and basic drift/throughput behavior, not final production sync policy.
- Native runtime/environment differences can affect whether native FFmpeg/PortAudio paths are active or fallback paths are used.
- If output sounds like static/noise and the tool prints `codec=pcm_f32le`, FFmpeg is in placeholder fallback mode.
  On Linux, try:

```fish
dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --input "/home/seko/Videos/shootingstar_0611_1.mov" --ffmpeg-root "/usr/lib"
```

Or set once per shell:

```fish
set -x SMEDIA_FFMPEG_ROOT /usr/lib
```

For Linux ALSA bring-up, this profile has been stable in local validation:

```fish
dotnet run --project Test/FirstAudioPlayback.Smoke/FirstAudioPlayback.Smoke.csproj -- --input "/home/seko/Videos/shootingstar_0611_1.mov" --ffmpeg-root "/usr/lib" --host-api alsa --device-index -1 --seconds 10 --decode-threads 6 --frames-per-read 1024 --engine-buffer-frames 1024
```

