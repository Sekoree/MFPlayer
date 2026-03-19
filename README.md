This is a bit of a AI-sloppy silly weekend project that started as "I wonder if MDK.SDK could be cool for a video player".
Then realizing it doesnt let you change audio devices.
After that building an ffmpeg based audio decoder for [OwnAudioSharp](https://github.com/ModernMube/OwnAudioSharp).
And finally abusing its clock and mixer functions to hack in video playback to OpenGL surfaces.
This is not clean and very experimental.
I tested it with 4k60 ProRes YUV422P10 files and it ran smoothly on my main PC but I cant guarantee anything.
