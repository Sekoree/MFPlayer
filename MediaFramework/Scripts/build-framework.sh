#!/usr/bin/env bash
# Build all MFPlayer framework assemblies into MediaFramework/FrameworkBuilds/<tfm>/ for consumption
# by external solutions (DLL references). Requires .NET SDK matching TargetFramework.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

CONFIG="${CONFIG:-Release}"
TFM="${TFM:-net10.0}"
OUT="${OUT:-$REPO_ROOT/MediaFramework/FrameworkBuilds/$TFM}"
PUBLISH_PROJ="$REPO_ROOT/MediaFramework/Build/MFPlayer.Framework.Publish/MFPlayer.Framework.Publish.csproj"

usage() {
  echo "Usage: CONFIG=Release TFM=net10.0 OUT=<dir> $0 [--no-restore]"
  echo "  Produces a flat drop of all framework DLLs and dependencies under OUT."
}

NO_RESTORE=0
for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    --no-restore) NO_RESTORE=1 ;;
    *) echo "Unknown option: $arg" >&2; usage >&2; exit 1 ;;
  esac
done

RESTORE_FLAG=()
if [[ "$NO_RESTORE" -eq 1 ]]; then
  RESTORE_FLAG=(--no-restore)
fi

echo "[build-framework] repo=$REPO_ROOT"
echo "[build-framework] CONFIG=$CONFIG TFM=$TFM OUT=$OUT"

mkdir -p "$OUT"
dotnet publish "$PUBLISH_PROJ" -c "$CONFIG" -o "$OUT" "${RESTORE_FLAG[@]}"

echo "[build-framework] done. Assemblies in: $OUT"
count="$(find "$OUT" -maxdepth 1 -name '*.dll' 2>/dev/null | wc -l)"
echo "[build-framework] top-level DLL count: $count"
