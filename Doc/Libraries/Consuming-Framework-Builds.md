# Consuming `MediaFramework/FrameworkBuilds` from another project

This describes how to use the output of **`MediaFramework/Scripts/build-framework.sh`** / **`MediaFramework/Scripts/build-framework.ps1`**, which publishes into **`MediaFramework/FrameworkBuilds/net10.0/`** (by default).

## What you get

- Managed assemblies for all MFPlayer framework projects (`S.Media.*`, `NDILib`, `OSCLib`, `PMLib`, `PALib`, `JackLib`, …).
- Transitive NuGet dependencies (for example `FFmpeg.AutoGen`, Avalonia assemblies when you include `S.Media.Avalonia`).
- A **`runtimes/`** directory with native libraries (for example SDL3) required at **process** load time.

Your host application’s **output directory** (next to the `.exe` / app host) must contain the same layout: all DLLs the app references, plus **`runtimes`** when native packages are used.

## Option A — Project references (recommended when the repo is available)

If the MFPlayer repository is on disk next to your app, prefer **`<ProjectReference>`** to the individual `.csproj` files. You get correct dependency flow, IntelliSense, and debugging without copying binaries.

## Option B — DLL references from `MediaFramework/FrameworkBuilds`

Use this when you distribute only a **binary drop** (or a CI artifact) without the full source tree.

### 1. Build the drop

```bash
cd /path/to/MFPlayer
./MediaFramework/Scripts/build-framework.sh
```

### 2. Reference assemblies you need

In your `.csproj`, point `HintPath` at the drop folder. Example for playback + PortAudio:

```xml
<PropertyGroup>
  <MFPlayerLibDir>$(MSBuildProjectDirectory)..\third_party\MFPlayer\MediaFramework\FrameworkBuilds\net10.0</MFPlayerLibDir>
</PropertyGroup>
<ItemGroup>
  <Reference Include="S.Media.Playback">
    <HintPath>$(MFPlayerLibDir)\S.Media.Playback.dll</HintPath>
    <Private>true</Private>
  </Reference>
  <Reference Include="S.Media.PortAudio">
    <HintPath>$(MFPlayerLibDir)\S.Media.PortAudio.dll</HintPath>
    <Private>true</Private>
  </Reference>
</ItemGroup>
```

Add **`Reference`** entries for **every** MFPlayer assembly you use directly. Transitive assemblies from the same folder are normally copied to your output when **`Private`** is true and MSBuild resolves dependencies; if anything is missing at runtime, copy the entire contents of `MediaFramework/FrameworkBuilds/net10.0` (including **`runtimes`**) into your publish output.

### 3. Copy step (safe default)

To guarantee the full layout matches the published drop:

```xml
<Target Name="CopyMFPlayerFramework" AfterTargets="Build">
  <ItemGroup>
    <_Mf Include="$(MFPlayerLibDir)\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(_Mf)" DestinationFolder="$(OutputPath)%(RecursiveDir)" SkipUnchangedFiles="true" />
</Target>
```

Adjust `MFPlayerLibDir` and consider scoping the glob if you want to avoid copying unused Avalonia assets.

### 4. Target framework

Consumer projects should target **`net10.0`** (or a compatible band you verify) to match these assemblies.

### 5. FFmpeg native binaries

`FFmpeg.AutoGen` is managed bindings only. Your **process** still needs **FFmpeg native** libraries (`libav*`, etc.) on `PATH` or discoverable by the OS. That is an environment/deployment concern, not part of `MediaFramework/FrameworkBuilds`.

### 6. NDI SDK

`NDILib` depends on the **NewTek NDI** native SDK being available at runtime on the target machine. Licensing and redistribution follow NDI’s terms.

## Option C — `dotnet pack` / private NuGet feed

For larger teams, wrapping each library in **`dotnet pack`** and pushing to an internal feed is often cleaner than a flat `MediaFramework/FrameworkBuilds` folder. The MFPlayer repo does not generate packages by default; you can add `PackageId` / `Version` to individual `.csproj` files if you adopt that workflow.
