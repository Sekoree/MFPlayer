#!/usr/bin/env bash
# F-17: generate a deterministic CycloneDX inventory from NuGet's restored project.assets.json
# files. No global tool is needed, so the SBOM gate cannot disappear because a tool feed is down.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$repo_root/artifacts/managed-sbom.cdx.json}"
solution_filter="${2:-$repo_root/MFPlayer.NoAndroid.slnf}"
mkdir -p "$(dirname "$output")"

python3 - "$repo_root" "$output" "$solution_filter" <<'PY'
import base64
import datetime as dt
import fnmatch
import hashlib
import json
import os
import sys
import urllib.parse
import uuid
import xml.etree.ElementTree as ET

repo_root, output, solution_filter = sys.argv[1:]
allowlist = json.load(open(os.path.join(
    repo_root, "scripts", "dependency-audit-allowlist.json"), encoding="utf-8"))["exceptions"]
solution = json.load(open(solution_filter, encoding="utf-8"))["solution"]
assets = [os.path.join(repo_root, os.path.dirname(project.replace("\\", os.sep)),
                       "obj", "project.assets.json")
          for project in solution["projects"]]
packages = {}
allowed_licenses = {"MIT", "Apache-2.0", "BSD-3-Clause", "Zlib", "MS-PL", "Unlicense OR MIT"}

for path in assets:
    try:
        document = json.load(open(path, encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        continue
    for key, library in document.get("libraries", {}).items():
        if library.get("type") != "package" or "/" not in key:
            continue
        name, version = key.rsplit("/", 1)
        packages[(name.lower(), version)] = (name, version, library)

global_packages = os.environ.get("NUGET_PACKAGES", os.path.expanduser("~/.nuget/packages"))

def license_of(name, version):
    nuspec = os.path.join(global_packages, name.lower(), version.lower(), f"{name.lower()}.nuspec")
    try:
        root = ET.parse(nuspec).getroot()
        metadata = next((element for element in root.iter() if element.tag.endswith("metadata")), root)
        license_node = next((element for element in metadata if element.tag.endswith("license")), None)
        if license_node is not None and license_node.text:
            declared = license_node.text.strip()
            if license_node.attrib.get("type", "").lower() == "file":
                license_path = os.path.join(global_packages, name.lower(), version.lower(), declared)
                try:
                    text = open(license_path, encoding="utf-8-sig", errors="replace").read(16_384)
                except OSError:
                    return "NOASSERTION"
                if "MIT License" in text or "The MIT License" in text:
                    return "MIT"
                if "Redistribution and use in source and binary forms" in text:
                    return "BSD-3-Clause"
                if ("provided 'as-is'" in text.lower()
                        and "alter it and redistribute it" in text.lower()
                        and "freely" in text.lower()):
                    return "Zlib"
                return "NOASSERTION"
            return declared
        url_node = next((element for element in metadata if element.tag.endswith("licenseUrl")), None)
        if url_node is not None and url_node.text:
            legacy_url = url_node.text.strip().lower()
            if "xunit" in legacy_url:
                return "Apache-2.0"
            if "microsoft" in legacy_url or "dotnet" in legacy_url:
                return "MIT"
        return "NOASSERTION"
    except (OSError, ET.ParseError):
        return "NOASSERTION"

components = []
missing_license = []
disallowed_license = []
for _, (name, version, library) in sorted(packages.items()):
    license_value = license_of(name, version)
    if license_value == "NOASSERTION":
        missing_license.append(f"{name} {version}")
    elif license_value not in allowed_licenses:
        disallowed_license.append(f"{name} {version}: {license_value}")
    component = {
        "type": "library",
        "bom-ref": f"pkg:nuget/{urllib.parse.quote(name, safe='')}@{urllib.parse.quote(version, safe='')}",
        "name": name,
        "version": version,
        "purl": f"pkg:nuget/{urllib.parse.quote(name, safe='')}@{urllib.parse.quote(version, safe='')}",
        "licenses": ([{"license": {"name": license_value}}]
                     if "://" in license_value or license_value == "NOASSERTION"
                     else [{"expression": license_value}]),
    }
    sha = library.get("sha512", "")
    if sha.startswith("sha512-"):
        try:
            component["hashes"] = [{"alg": "SHA-512", "content": base64.b64decode(sha[7:]).hex()}]
        except ValueError:
            pass
    components.append(component)

if not components:
    raise SystemExit("managed SBOM: no restored non-Android project.assets.json files found")
unapproved_missing = []
for package in missing_license:
    name = package.rsplit(" ", 1)[0]
    exception = next((item for item in allowlist
                      if item.get("kind") == "missing-license"
                      and fnmatch.fnmatchcase(name.lower(), item.get("package", "").lower())), None)
    if exception is None:
        unapproved_missing.append(package)
        continue
    expiry = dt.date.fromisoformat(exception["expires"])
    if expiry < dt.date.today():
        unapproved_missing.append(f"{package} (exception expired {expiry})")
        continue
    print(f"managed SBOM: ALLOW missing license metadata for {package} until {expiry} "
          f"({exception['owner']}): {exception['reason']}")
if unapproved_missing:
    raise SystemExit("managed SBOM: package license metadata missing without a current exception: "
                     + ", ".join(unapproved_missing))
if disallowed_license:
    raise SystemExit("managed SBOM: license outside the approved policy: "
                     + ", ".join(disallowed_license))

serial_seed = "\n".join(component["bom-ref"] for component in components).encode()
document = {
    "bomFormat": "CycloneDX",
    "specVersion": "1.6",
    "serialNumber": "urn:uuid:" + str(uuid.UUID(bytes=hashlib.md5(
        serial_seed, usedforsecurity=False).digest())),
    "version": 1,
    "metadata": {
        "component": {
            "type": "application",
            "bom-ref": "pkg:github/Sekoree/MFPlayer",
            "name": "MFPlayer non-Android managed graph",
        }
    },
    "components": components,
}
with open(output, "w", encoding="utf-8") as stream:
    json.dump(document, stream, indent=2, sort_keys=True)
    stream.write("\n")
print(f"managed SBOM: wrote {len(components)} package component(s) -> {output}")
PY
