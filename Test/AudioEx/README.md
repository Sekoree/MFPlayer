# AudioEx (S.Media)

Migration harness for FFmpeg audio decode + PortAudio output push.

## Build

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet build Test/AudioEx/AudioEx.csproj --no-restore
```

## List Devices

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/AudioEx/AudioEx.csproj -- --list-devices
```

## Run

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/AudioEx/AudioEx.csproj -- --input /path/to/media.mp4 --seconds 8
```

