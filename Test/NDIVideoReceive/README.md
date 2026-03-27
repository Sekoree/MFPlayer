# NDIVideoReceive (S.Media)

Migration harness for NDI source discovery plus SDL3 video preview via `S.Media.OpenGL.SDL3`.

## Build

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet build Test/NDIVideoReceive/NDIVideoReceive.csproj --no-restore
```

## List Sources

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --list-sources --discover-seconds 10
```

## List Audio Host APIs

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --list-host-apis
```

## List Audio Devices (Default First)

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --list-audio-devices
```

## Run SDL3 Preview

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 15
```

## Sync Mode Selection

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 15 --sync-mode stable
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 15 --sync-mode hybrid
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 15 --sync-mode strict
```

## Automatic Drift Correction (Enabled By Default)

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 30 --sync-mode stable --auto-drift-correction true --drift-deadband-ms 20 --drift-gain 0.08
```

## Run Preview With Explicit Host API + Default Device

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --host-api alsa --audio-device-index -1 --discover-seconds 10 --preview-seconds 15
```

## Run Until Ctrl+C

```fish
cd /home/seko/RiderProjects/MFPlayer
dotnet run --project Test/NDIVideoReceive/NDIVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 0
```
