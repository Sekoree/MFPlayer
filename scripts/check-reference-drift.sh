#!/usr/bin/env bash
# F-18 (2026-08-14 review): report how the git-ignored Reference/ cache has drifted from the
# authoritative pins. Reference/ folder names are NEVER the truth - Directory.Packages.props and
# .github/native-manifest/ffmpeg.lock are - but people (and code comments) read the cache, so this
# makes the drift visible instead of tribal:
#   STALE        snapshot version != the authoritative package pin
#   NO-IDENTITY  a -master/-main clone whose name carries no version at all
#   UNKNOWN      an on-disk directory scripts/reference-manifest.json does not list
#   OK / ABSENT  matches its pin / not present locally (absent is fine - fetch on demand)
#
# Advisory by default (always exits 0); --strict exits 1 when anything is STALE, NO-IDENTITY,
# or UNKNOWN. A rolling clone is not auditable merely because its manifest entry admits that fact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
strict=0
[[ "${1:-}" == "--strict" ]] && strict=1

python3 - "$repo_root" "$strict" <<'PY'
import json, os, re, sys

repo_root, strict = sys.argv[1], sys.argv[2] == "1"
reference = os.path.join(repo_root, "Reference")
manifest = json.load(open(os.path.join(repo_root, "scripts", "reference-manifest.json")))["entries"]

pins = dict(re.findall(
    r'Include="([^"]+)"\s+Version="([^"]+)"',
    open(os.path.join(repo_root, "Directory.Packages.props")).read()))

def match_entry(name):
    for pattern, entry in manifest.items():
        if pattern.endswith("*"):
            if name.startswith(pattern[:-1]):
                return pattern, entry
        elif name == pattern:
            return pattern, entry
    return None, None

failures = 0
rows = []
on_disk = sorted(os.listdir(reference)) if os.path.isdir(reference) else []
seen_patterns = set()

for name in on_disk:
    pattern, entry = match_entry(name)
    if entry is None:
        rows.append(("UNKNOWN", name, "not in scripts/reference-manifest.json - add or remove it"))
        failures += 1
        continue
    seen_patterns.add(pattern)
    if name.endswith(("-master", "-main")):
        rows.append(("NO-IDENTITY", name, "a rolling clone carries no version; re-snapshot with a tag"))
        failures += 1
        continue
    package = entry.get("package")
    if not package:
        rows.append(("OK", name, entry["kind"]))
        continue
    pin = pins.get(package)
    version = re.search(r"-([0-9][0-9A-Za-z.]*)$", name)
    if pin is None or version is None:
        rows.append(("OK", name, f"{entry['kind']} (no comparable version)"))
        continue
    if version.group(1) == pin:
        rows.append(("OK", name, f"matches {package} {pin}"))
    else:
        rows.append(("STALE", name, f"snapshot {version.group(1)} vs authoritative {package} {pin}"))
        failures += 1

for pattern, entry in manifest.items():
    if pattern not in seen_patterns and not any(match_entry(n)[0] == pattern for n in on_disk):
        rows.append(("ABSENT", pattern, "not present locally (fine - fetch on demand)"))

width = max((len(r[1]) for r in rows), default=10)
for status, name, note in rows:
    print(f"{status:12} {name:{width}}  {note}")

print()
print(f"{sum(1 for r in rows if r[0] == 'STALE')} stale · "
      f"{sum(1 for r in rows if r[0] == 'NO-IDENTITY')} no-identity · "
      f"{sum(1 for r in rows if r[0] == 'UNKNOWN')} unknown · "
      f"{sum(1 for r in rows if r[0] == 'OK')} ok · "
      f"{sum(1 for r in rows if r[0] == 'ABSENT')} absent")

sys.exit(1 if strict and failures else 0)
PY
