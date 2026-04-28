1. YUV422p10 seems to get converted to BGRA32 on local video (should normally just passthrough) (tested with heavy 4k60 yuv422p10 prores file)
2. Maybe option to lock video output fps to source video fps on Avalonia unless that stalls the entire app
3. (local video only) Drift in the HUD value seems very broken, while playback looks fine (when not running slow), it seems to be at a random very high value (currently in my testing hovering around 9000ms and increasing more and more).
4. (local video only) Playback doesnt seem to detect the end of the video and now the drift in the HUD just runs higher and higher.
5. (local video + audio) When running slow audio and video seem to get out of sync