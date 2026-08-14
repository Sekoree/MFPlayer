#!/usr/bin/env bash
# Stages the FFmpeg shared build this repo's bindings require into External/ffmpeg/<rid>/.
#
# WHY THIS EXISTS: FFmpeg.AutoGen loads VERSIONED sonames (libavcodec.so.63 / avcodec-63.dll), so the
# ABI major must match EXACTLY. A distro on a different FFmpeg major does not "mostly work" - it does
# not load at all, and because DynamicallyLoadedBindings.Initialize() succeeds anyway, the failure
# surfaces as every media file appearing offline. This script gets a matching build without touching
# the system FFmpeg, so the machine can keep whatever version everything else on it wants.
#
# F-02: the exact build (dated BtbN autobuild release + asset + SHA-256 + soname set) comes from
# .github/native-manifest/ffmpeg.lock - the SAME lock CI downloads and gates by, so a local checkout
# stages byte-identical libraries to what a release artifact bundles. Never a `latest` rolling ref.
#
# Usage:
#   scripts/fetch-ffmpeg.sh            # host rid, exact build from the lock
#
# The resolver finds External/ffmpeg/<rid>/ automatically when running from a checkout. For a deployed
# app, or to override everything, export the MFP_FFMPEG_LIB line this prints.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

lock="$repo_root/.github/native-manifest/ffmpeg.lock"
[[ -f "$lock" ]] || { echo "error: missing $lock (the FFmpeg native lock)." >&2; exit 2; }
# shellcheck source=/dev/null
source "$lock"

case "$(uname -s)" in
    Linux)  os=linux ;;
    Darwin) echo "error: BtbN publishes no macOS builds; use Homebrew's ffmpeg and export MFP_FFMPEG_LIB." >&2; exit 2 ;;
    *)      echo "error: unsupported platform $(uname -s)." >&2; exit 2 ;;
esac

case "$(uname -m)" in
    x86_64)         arch=x64;   asset="$FFMPEG_LINUX_X64_ASSET";   sha256="$FFMPEG_LINUX_X64_SHA256" ;;
    aarch64|arm64)  arch=arm64; asset="$FFMPEG_LINUX_ARM64_ASSET"; sha256="$FFMPEG_LINUX_ARM64_SHA256" ;;
    *)              echo "error: unsupported architecture $(uname -m)." >&2; exit 2 ;;
esac

rid="${os}-${arch}"
install_dir="$repo_root/External/ffmpeg/$rid"
work_dir="$repo_root/External/ffmpeg/download"
url="https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_RELEASE_TAG}/${asset}"

# The set the bindings ask the loader for, from the lock. Staging a PARTIAL set is worse than staging
# none: the loader would mix a bundled avcodec with a system avutil, which is undefined behaviour
# rather than a clean failure. So this is verified after extraction and rolled back if it is short.
read -r -a required <<< "$FFMPEG_SONAMES"

echo "==> FFmpeg $FFMPEG_SNAPSHOT ($FFMPEG_SNAPSHOT_DATE snapshot, $rid)"

if [[ -d "$install_dir" ]]; then
    missing=0
    for lib in "${required[@]}"; do
        [[ -e "$install_dir/$lib" ]] || missing=1
    done
    if [[ $missing -eq 0 ]]; then
        echo "    already staged at $install_dir"
        echo "    export MFP_FFMPEG_LIB=\"$install_dir\"   # only needed outside a checkout"
        exit 0
    fi
    echo "    incomplete or outdated staging found - refetching"
    rm -rf "$install_dir"
fi

mkdir -p "$work_dir"
echo "==> downloading $asset"
curl -fSL --retry 3 --retry-delay 2 -o "$work_dir/$asset" "$url"
echo "$sha256  $work_dir/$asset" | sha256sum -c -

echo "==> extracting"
rm -rf "$work_dir/extract"
mkdir -p "$work_dir/extract"
tar -xf "$work_dir/$asset" -C "$work_dir/extract"

src_lib="$(find "$work_dir/extract" -maxdepth 2 -type d -name lib | head -1)"
if [[ -z "$src_lib" ]]; then
    echo "error: no lib/ directory in $asset." >&2
    exit 1
fi

mkdir -p "$install_dir"
cp -a "$src_lib"/*.so* "$install_dir/"

for lib in "${required[@]}"; do
    if [[ ! -e "$install_dir/$lib" ]]; then
        echo "error: $asset is missing $lib - this is not the ABI set these bindings need." >&2
        echo "       Staging rolled back rather than leaving a partial set to half-load." >&2
        echo "       (Did FFmpeg.AutoGen or the lock move majors without the other?)" >&2
        rm -rf "$install_dir"
        exit 1
    fi
done

rm -rf "$work_dir/extract" "$work_dir/$asset"

echo "==> staged $(ls -1 "$install_dir"/*.so.* 2>/dev/null | wc -l) libraries in $install_dir"
echo
echo "    Running from this checkout needs nothing further - the resolver probes External/ffmpeg/$rid."
echo "    For a deployed app, or to force this build:"
echo "        export MFP_FFMPEG_LIB=\"$install_dir\""
