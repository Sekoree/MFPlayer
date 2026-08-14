# A media playback thing

### HaPlay
Sort of a "demo" app for a lot of things this can do. Some parts had some feature creep so now HaCue2 exists.<br>

### HaCue2
Silly cue player with lots of input and output options. Current theme is a bit dark so might need a bit of work in the future.<br>
Things mostly work as one expects, it still needs some docs but for the most part its:
- Audio: Media Cue with audio -> Cue audio matrix -> Virtual Output -> Virtual-to-Physical audio matrix -> Physical Output<br>
- Video: Media Cue with video -> Placement on Composition -> Composition -> Real video output<br>
  - A Composition can be an intermediary video surface, real video outputs can display it either in full or only parts of it.
  - Multiple real outputs can display the same Composition.

### Disclaimer
I did use a lot of AI tools for this, mainly to experiment to see what's possible (or the usual ffmpeg boilerplate to get stream data etc. or OpenGL shaders).<br>

### Main Dependencies
(I'll probably forget something)<br>
Avalonia (for the UI)<br>
FFmpeg(.AutoGen) (decode/encode; the exact native pin lives in `.github/native-manifest/ffmpeg.lock`)<br>
SkiaSharp (inherited from Avalonia, used for text stuff)<br>
SDL3-CS (for video output, so things aren't strictly tied to the Avalonia dispatcher)<br>
Mond (for the scripting parts of the "Control" area)<br>
YoutubeExplode (for the YouTube source stuff)<br>
XRAnimator and blender_mmd_tools (to understand how to read model and motion data)<br>
libASS (for fancy subtitles)<br>
NDI (to professionally™ send audio and video over the network)<br>
