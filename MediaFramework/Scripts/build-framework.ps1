# Build all MFPlayer framework assemblies into MediaFramework\FrameworkBuilds\<tfm>\
# Usage: .\MediaFramework\Scripts\build-framework.ps1 [-Configuration Release] [-NoRestore]

param(
    [string] $Configuration = "Release",
    [string] $Tfm = "net10.0",
    [string] $Out = "",
    [switch] $NoRestore
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

if (-not $Out) {
    $Out = Join-Path $RepoRoot "MediaFramework\FrameworkBuilds\$Tfm"
}

$PublishProj = Join-Path $RepoRoot "MediaFramework\Build\MFPlayer.Framework.Publish\MFPlayer.Framework.Publish.csproj"
$restoreArgs = @()
if ($NoRestore) { $restoreArgs += "--no-restore" }

Write-Host "[build-framework] repo=$RepoRoot"
Write-Host "[build-framework] Configuration=$Configuration Tfm=$Tfm Out=$Out"

New-Item -ItemType Directory -Force -Path $Out | Out-Null
dotnet publish $PublishProj -c $Configuration -o $Out @restoreArgs

Write-Host "[build-framework] done. Assemblies in: $Out"
$dllCount = (Get-ChildItem -Path $Out -Filter "*.dll" -File).Count
Write-Host "[build-framework] top-level DLL count: $dllCount"
