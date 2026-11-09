#!/usr/bin/env python3
"""Collect third-party license texts for a release zip.

Reads the RID-specific package graph from a project.assets.json and writes, under
--out, one folder per NuGet package holding that package's license/notice files,
plus the .NET runtime's third-party notices.

  <out>/<PackageId>/...              license/notice files shipped inside the package
  <out>/<PackageId>/LICENSE-EXPRESSION.txt
                                     only for a package that ships no license file:
                                     its nuspec license expression, copyright, authors, URLs
  <out>/_spdx/<SPDX-ID>.txt          one canonical text per license expression above
                                     (copied from --spdx-dir)
  <out>/<PackageId>/<text>.txt       for a package in EXTRA_LICENSE_TEXTS: license text of
                                     content the package bundles under a different license
                                     than the package itself (copied from --spdx-dir)
  <out>/dotnet-runtime/...           THIRD-PARTY-NOTICES.TXT + LICENSE.TXT of the runtime

Exits non-zero (and prints every problem found) if the assets file is missing or
yields no packages, a package folder is missing, a license text cannot be
determined, or the .NET runtime notices cannot be found, so a release can never
ship without them.

Standard library only; runs on the Linux and Windows runners.
"""
import argparse
import fnmatch
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# Case-insensitive file-name globs for license / notice files inside a package.
FILE_PATTERNS = ["licen[cs]e*", "third-party-notices*", "thirdpartynotices*", "notice*"]

# Legacy <licenseUrl>-only packages (no <license> element): map a URL fingerprint
# to the SPDX id of the license it points at. A package that is neither in this
# table nor ships a license file fails the run, so a new dependency can never
# slip through without a license text.
LEGACY_URL_SPDX = [(re.compile(r"mpl-1\.1", re.I), "MPL-1.1")]

# Packages that bundle content under a license other than the package's own. The
# file is copied from --spdx-dir into licenses/<PackageId>/ in addition to whatever
# the package ships. Avalonia.Fonts.Inter is MIT, but the bundled Inter font is under
# the SIL OFL 1.1, which requires the copyright notice and license text to accompany
# the font. The text is Inter's own LICENSE.txt at tag v3.19, the version the package
# embeds ("Version 3.019" in the font's name table).
EXTRA_LICENSE_TEXTS = {"Avalonia.Fonts.Inter": ["OFL-1.1-Inter.txt"]}

RUNTIME_PACK = "Microsoft.NETCore.App.Runtime.{rid}"
# (notices pattern, license pattern) in the runtime pack, then in the dotnet root.
RUNTIME_NOTICE_NAMES = ["third-party-notices.txt", "thirdpartynotices.txt"]
RUNTIME_LICENSE_NAMES = ["license.txt"]


def matches_license_file(name):
    low = name.lower()
    return any(fnmatch.fnmatchcase(low, p) for p in FILE_PATTERNS)


def find_file(folder, names):
    """Case-insensitive lookup of the first of `names` directly inside `folder`."""
    if not folder.is_dir():
        return None
    by_lower = {p.name.lower(): p for p in folder.iterdir() if p.is_file()}
    for n in names:
        if n in by_lower:
            return by_lower[n]
    return None


def parse_nuspec(folder):
    files = sorted(folder.glob("*.nuspec"))
    if not files:
        return {}
    root = ET.parse(str(files[0])).getroot()
    meta = None
    for el in root:
        if el.tag.split("}")[-1] == "metadata":
            meta = el
    info = {}
    if meta is None:
        return info
    for el in meta:
        tag = el.tag.split("}")[-1]
        text = (el.text or "").strip()
        if tag == "license":
            info["license_type"] = el.get("type", "")
            info["license"] = text
        elif tag in ("licenseUrl", "projectUrl", "copyright", "authors"):
            info[tag] = text
    return info


def spdx_ids(expression):
    return sorted({t for t in re.split(r"[\s()]+", expression) if t and t.upper() not in ("AND", "OR", "WITH")})


def runtime_pack_version(assets, tfm, rid):
    """Version of the runtime pack restore selected, from downloadDependencies."""
    want = RUNTIME_PACK.format(rid=rid).lower()
    deps = assets.get("project", {}).get("frameworks", {}).get(tfm, {}).get("downloadDependencies", [])
    for d in deps:
        if d.get("name", "").lower() == want:
            m = re.search(r"\d+(\.\d+)+\S*?(?=[,\]\s]|$)", d.get("version", ""))
            if m:
                return m.group(0)
    return None


def find_runtime_notices(assets, tfm, rid, packages_path, dotnet_root, problems):
    """Return (notices_file, license_file, where), or None if either file is missing."""
    pack_id = RUNTIME_PACK.format(rid=rid)
    version = runtime_pack_version(assets, tfm, rid)
    candidates = []
    if version:
        candidates.append(packages_path / pack_id.lower() / version.lower())
    # Hosted-runner / SDK location of installed packs.
    if dotnet_root:
        packs = dotnet_root / "packs" / pack_id
        if version:
            candidates.append(packs / version)
        elif packs.is_dir():
            candidates.extend(sorted((p for p in packs.iterdir() if p.is_dir()), reverse=True))
    for folder in candidates:
        notices = find_file(folder, RUNTIME_NOTICE_NAMES)
        lic = find_file(folder, RUNTIME_LICENSE_NAMES)
        if notices and lic:
            return notices, lic, str(folder)
    # Last resort: the dotnet install root ships the same notices.
    if dotnet_root:
        notices = find_file(dotnet_root, RUNTIME_NOTICE_NAMES)
        lic = find_file(dotnet_root, RUNTIME_LICENSE_NAMES)
        if notices and lic:
            return notices, lic, str(dotnet_root)
    problems.append(
        ".NET runtime notices (THIRD-PARTY-NOTICES.TXT + LICENSE.TXT) not found; looked in: "
        + ", ".join(str(c) for c in candidates + ([dotnet_root] if dotnet_root else []))
    )
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--assets", required=True, help="path to the app's obj/project.assets.json")
    ap.add_argument("--rid", required=True, help="runtime identifier, e.g. linux-x64 or win-x64")
    ap.add_argument("--out", required=True, help="licenses/ folder to create")
    ap.add_argument("--spdx-dir", required=True, help="folder of canonical <SPDX-ID>.txt texts")
    ap.add_argument("--tfm", default="net8.0", help="target framework key in the assets file")
    ap.add_argument("--dotnet-root", default=None, help="dotnet install root (default: $DOTNET_ROOT, else from PATH)")
    args = ap.parse_args()

    assets_file = Path(args.assets)
    if not assets_file.is_file():
        sys.exit("ERROR: assets file not found: %s" % assets_file)
    try:
        assets = json.loads(assets_file.read_text(encoding="utf-8-sig"))
    except ValueError as e:
        sys.exit("ERROR: %s is not valid JSON: %s" % (assets_file, e))

    target_key = "%s/%s" % (args.tfm, args.rid)
    target = assets.get("targets", {}).get(target_key)
    if target is None:
        sys.exit("ERROR: no target '%s' in %s (have: %s)" % (target_key, assets_file, ", ".join(assets.get("targets", {}))))
    pkg_keys = sorted(k for k, v in target.items() if v.get("type") == "package")
    if not pkg_keys:
        sys.exit("ERROR: target '%s' lists zero packages; refusing to produce an empty licenses folder" % target_key)

    packages_path = assets.get("project", {}).get("restore", {}).get("packagesPath")
    if not packages_path:
        sys.exit("ERROR: no project.restore.packagesPath in %s" % assets_file)
    packages_path = Path(packages_path)

    dotnet_root = args.dotnet_root or os.environ.get("DOTNET_ROOT")
    if not dotnet_root:
        exe = shutil.which("dotnet")
        dotnet_root = str(Path(exe).resolve().parent) if exe else None
    dotnet_root = Path(dotnet_root) if dotnet_root else None

    spdx_dir = Path(args.spdx_dir)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    libraries = assets.get("libraries", {})
    problems = []
    spdx_used = set()
    n_files = 0
    n_expr = 0

    for key in pkg_keys:
        pkg_id, _, pkg_ver = key.partition("/")
        rel = libraries.get(key, {}).get("path") or ("%s/%s" % (pkg_id.lower(), pkg_ver.lower()))
        folder = packages_path / rel
        if not folder.is_dir():
            problems.append("%s: package folder not found: %s" % (key, folder))
            continue
        dest = out / pkg_id
        info = parse_nuspec(folder)

        extras = EXTRA_LICENSE_TEXTS.get(pkg_id, [])
        missing_extras = [x for x in extras if not (spdx_dir / x).is_file()]
        if missing_extras:
            problems.append("%s: no bundled-content license text %s in %s" % (key, ", ".join(missing_extras), spdx_dir))
            continue
        if extras:
            dest.mkdir(parents=True, exist_ok=True)
            for x in extras:
                shutil.copyfile(str(spdx_dir / x), str(dest / x))
                n_files += 1

        to_copy = {p.name: p for p in folder.iterdir() if p.is_file() and matches_license_file(p.name)}
        # A nuspec <license type="file"> names its license file explicitly.
        if info.get("license_type") == "file" and info.get("license"):
            ref = folder / info["license"]
            if ref.is_file():
                to_copy.setdefault(ref.name, ref)
        if to_copy:
            dest.mkdir(parents=True, exist_ok=True)
            for name, src in sorted(to_copy.items()):
                shutil.copyfile(str(src), str(dest / name))
                n_files += 1
            continue

        # No license file in the package: record the nuspec's license data and
        # ship the canonical text for the license it names.
        ids = []
        if info.get("license_type") == "expression" and info.get("license"):
            ids = spdx_ids(info["license"])
            expr = info["license"]
        else:
            expr = None
            url = info.get("licenseUrl", "")
            for rx, spdx in LEGACY_URL_SPDX:
                if rx.search(url):
                    ids, expr = [spdx], spdx
                    break
        if not ids:
            problems.append("%s: ships no license file and its nuspec names no recognized license "
                            "(licenseUrl=%r); add handling in collect-licenses.py" % (key, info.get("licenseUrl")))
            continue
        missing = [i for i in ids if not (spdx_dir / (i + ".txt")).is_file()]
        if missing:
            problems.append("%s: no canonical text for %s in %s" % (key, ", ".join(missing), spdx_dir))
            continue
        for i in ids:
            spdx_used.add(i)
        lines = ["Package: %s %s" % (pkg_id, pkg_ver),
                 "License: %s" % expr,
                 "License text: " + ", ".join("../_spdx/%s.txt" % i for i in ids)]
        if info.get("copyright"):
            lines.append("Copyright: %s" % info["copyright"])
        if info.get("authors"):
            lines.append("Authors: %s" % info["authors"])
        if info.get("projectUrl"):
            lines.append("Project URL: %s" % info["projectUrl"])
        if info.get("license_type") != "expression" and info.get("licenseUrl"):
            lines.append("License URL: %s" % info["licenseUrl"])
        dest.mkdir(parents=True, exist_ok=True)
        (dest / "LICENSE-EXPRESSION.txt").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        n_expr += 1

    if spdx_used:
        (out / "_spdx").mkdir(parents=True, exist_ok=True)
        for i in sorted(spdx_used):
            shutil.copyfile(str(spdx_dir / (i + ".txt")), str(out / "_spdx" / (i + ".txt")))

    runtime_from = "(not found)"
    found = find_runtime_notices(assets, args.tfm, args.rid, packages_path, dotnet_root, problems)
    if found is not None:
        notices, lic, runtime_from = found
        rt = out / "dotnet-runtime"
        rt.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(str(notices), str(rt / notices.name))
        shutil.copyfile(str(lic), str(rt / lic.name))

    if problems:
        for p in problems:
            print("ERROR: " + p, file=sys.stderr)
        sys.exit(1)

    print("collect-licenses: rid=%s packages=%d (license files copied=%d, expression-only=%d, spdx texts=%s) "
          "runtime notices from %s" % (args.rid, len(pkg_keys), n_files, n_expr, ",".join(sorted(spdx_used)) or "-", runtime_from))


if __name__ == "__main__":
    main()
