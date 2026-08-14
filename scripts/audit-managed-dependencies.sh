#!/usr/bin/env bash
# F-17: fail on known vulnerable or deprecated managed packages. `dotnet list package` itself
# exits zero when it REPORTS findings, so parse its JSON and apply only reviewed, expiring exceptions.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution="${1:-$repo_root/MFPlayer.NoAndroid.slnf}"
audit_dir="$(mktemp -d)"
trap 'rm -rf "$audit_dir"' EXIT

dotnet list "$solution" package --vulnerable --include-transitive --format json \
  > "$audit_dir/vulnerable.json"
dotnet list "$solution" package --deprecated --include-transitive --format json \
  > "$audit_dir/deprecated.json"

python3 - "$repo_root/scripts/dependency-audit-allowlist.json" \
  "$audit_dir/vulnerable.json" "$audit_dir/deprecated.json" <<'PY'
import datetime as dt
import fnmatch
import json
import sys

allow_path, vulnerable_path, deprecated_path = sys.argv[1:]
exceptions = json.load(open(allow_path, encoding="utf-8"))["exceptions"]
today = dt.date.today()

for item in exceptions:
    missing = {"kind", "package", "owner", "expires", "reason"} - item.keys()
    if missing:
        raise SystemExit(f"dependency audit: malformed exception missing {sorted(missing)}")
    try:
        item["expiry"] = dt.date.fromisoformat(item["expires"])
    except ValueError as failure:
        raise SystemExit(f"dependency audit: bad expiry for {item['package']}: {failure}")

def findings(path, kind):
    document = json.load(open(path, encoding="utf-8"))
    found = set()
    for project in document.get("projects", []):
        for framework in project.get("frameworks", []):
            for bucket in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(bucket, []):
                    relevant = package.get("vulnerabilities") if kind == "vulnerable" \
                        else package.get("deprecationReasons")
                    if relevant:
                        found.add((kind, package["id"], package.get("resolvedVersion", "?")))
    return found

all_findings = findings(vulnerable_path, "vulnerable") | findings(deprecated_path, "deprecated")
blocked = []
allowed = []
for kind, package, version in sorted(all_findings):
    match = next((item for item in exceptions
                  if item["kind"] == kind
                  and fnmatch.fnmatchcase(package.lower(), item["package"].lower())), None)
    if match is None:
        blocked.append((kind, package, version, "no reviewed exception"))
    elif match["expiry"] < today:
        blocked.append((kind, package, version,
                        f"exception owned by {match['owner']} expired {match['expires']}"))
    else:
        allowed.append((kind, package, version, match))

for kind, package, version, item in allowed:
    print(f"ALLOW {kind:10} {package} {version} until {item['expires']} "
          f"({item['owner']}): {item['reason']}")
for kind, package, version, reason in blocked:
    print(f"BLOCK {kind:10} {package} {version}: {reason}", file=sys.stderr)

print(f"dependency audit: {len(all_findings)} finding(s), {len(allowed)} reviewed, "
      f"{len(blocked)} blocking")
sys.exit(1 if blocked else 0)
PY
