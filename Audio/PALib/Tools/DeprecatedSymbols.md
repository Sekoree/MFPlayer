# Deprecated Symbols Excluded From PALib

Only symbols explicitly marked deprecated in PortAudio headers are excluded.

| Header | Line | Symbol | Reason |
| --- | ---: | --- | --- |
| `portaudio.h` | 67 | `Pa_GetVersionText` | Comment notes deprecated in favor of `Pa_GetVersionInfo()->versionText` |
| `pa_asio.h` | 78 | `PaAsio_GetAvailableLatencyValues` | Macro alias retained for backward compatibility only |
| `pa_win_waveformat.h` | 113 | `PAWIN_SPEAKER_5POINT1_BACK` | Comment marks obsolete compatibility alias |
| `pa_win_waveformat.h` | 114 | `PAWIN_SPEAKER_7POINT1_WIDE` | Comment marks obsolete compatibility alias |

