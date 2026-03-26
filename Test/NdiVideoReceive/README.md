# NdiVideoReceive (S.Media)

Migration harness for NDI source discovery plus SDL3 video preview via `S.Media.OpenGL.SDL3`.

## Build

```fish
cd /home/sekoree/RiderProjects/MFPlayer
dotnet build Test/NdiVideoReceive/NdiVideoReceive.csproj --no-restore
```

## List Sources

```fish
cd /home/sekoree/RiderProjects/MFPlayer
dotnet run --project Test/NdiVideoReceive/NdiVideoReceive.csproj -- --list-sources --discover-seconds 10
```

## Run SDL3 Preview

```fish
cd /home/sekoree/RiderProjects/MFPlayer
dotnet run --project Test/NdiVideoReceive/NdiVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 15
```

## Run Until Ctrl+C

```fish
cd /home/sekoree/RiderProjects/MFPlayer
dotnet run --project Test/NdiVideoReceive/NdiVideoReceive.csproj -- --discover-seconds 10 --preview-seconds 0
```

