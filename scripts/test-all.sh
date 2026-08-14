#!/usr/bin/env bash
# N-11 (2026-08-14 review): the ONE way to run the whole suite locally. Device-touching suites do
# not tolerate parallel test hosts (ALSA contention crashed a host; timing suites flake under
# CPU starvation), so this mirrors CI's serialized invocation:
#   -m:1                             one test PROJECT at a time (MSBuild-level; the half a
#                                    .runsettings file cannot express)
#   RunConfiguration.MaxCpuCount=1   no parallelism inside an invocation (also in .runsettings)
# and uses the NoAndroid solution filter so it runs without the Android workload (F-16).
#
# Usage: scripts/test-all.sh [extra dotnet-test args...]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

exec dotnet test "$repo_root/MFPlayer.NoAndroid.slnf" -c Release -m:1 "$@" \
    -- RunConfiguration.MaxCpuCount=1
