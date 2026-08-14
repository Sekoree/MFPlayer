#!/usr/bin/env bash
# F-01/F-17: prove the documented INTERNAL distribution contract end to end. Pack the framework
# graph into a blank staged feed, then restore/build/run a consumer that has no project references.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
feed="$work_dir/feed"
consumer="$work_dir/consumer"
mkdir -p "$feed" "$consumer"

dotnet pack "$repo_root/MFPlayer.Framework.slnf" -c Release -o "$feed" \
  -p:SkipProjectMNativeBuild=true
cp "$repo_root"/packages/*.nupkg "$feed"/

version="$(dotnet msbuild "$repo_root/MediaFramework/Packages/S.Media.Full/S.Media.Full.csproj" \
  -getProperty:Version -nologo | tail -1)"

dotnet new console --framework net10.0 --no-restore -o "$consumer" >/dev/null
dotnet add "$consumer" package S.Media.Full --version "$version" --no-restore \
  --source "$feed" >/dev/null

printf '%s\n' \
  'using S.Media.Core.Audio;' \
  'var format = new AudioFormat(48_000, 2);' \
  'Console.WriteLine($"S.Media.Full consumer OK: {format.SampleRate} Hz / {format.Channels} ch");' \
  > "$consumer/Program.cs"

dotnet restore "$consumer" --source "$feed" --source https://api.nuget.org/v3/index.json
output="$(dotnet run --project "$consumer" -c Release --no-restore)"
printf '%s\n' "$output"
grep -q 'S.Media.Full consumer OK: 48000 Hz / 2 ch' <<<"$output"
