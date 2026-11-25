#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.txt: every package the desktop app ships, with its licence and the licence text found in
the package (health check 2026-11-12, dependencies-1). Run after changing packages:

    python3 scripts/third-party-notices.py

It reads `dotnet list package --include-transitive` for the desktop project and the packages in the local NuGet cache
(restore first). A package whose licence text is not in the package is listed with its licence expression or URL only.
"""
import glob
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
PROJECT = os.path.join(ROOT, "src", "Mindweave.Desktop")
OUT = os.path.join(ROOT, "THIRD-PARTY-NOTICES.txt")
CACHE = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")

listing = json.loads(subprocess.run(["dotnet", "list", PROJECT, "package", "--include-transitive", "--format", "json"],
                                    capture_output=True, text=True, check=True).stdout)
packages = sorted({(p["id"], p["resolvedVersion"])
                   for project in listing["projects"] for fw in project.get("frameworks", [])
                   for kind in ("topLevelPackages", "transitivePackages") for p in fw.get(kind, [])},
                  key=lambda p: p[0].lower())

# Standard texts for packages that name an SPDX licence but ship no text of it (2026-11-17: Apache-2.0 and MIT require the
# licence text to go along with the software, so a name alone is not enough). Printed once at the end of the file.
STANDARD = {name: os.path.join(ROOT, "scripts", "licences", f"{name}.txt") for name in ("Apache-2.0", "BSD-2-Clause", "MIT", "OFL-1.1")}
standard_used = []

# What a package carries inside under another licence and gives no text of (2026-11-24): name, copyright and licence as
# the font's own name table states them.
EMBEDDED = {"Avalonia.Fonts.Inter": [("Inter 3.019 (font)", "Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)", "OFL-1.1")]}

# Third-party files kept in the repository and shipped inside the app: name, licence, project, copyright, the licence file
# and what it carries under another licence (docs/design/sharing.md 6.7).
VENDORED = [("KaTeX 0.19.1", "MIT", "https://katex.org", "Copyright (c) 2013-2020 Khan Academy and other contributors",
             os.path.join("src", "Mindweave.Infrastructure", "Share", "katex", "LICENSE"),
             ("KaTeX fonts", "Copyright (c) 2009-2010, Design Science, Inc. (<www.mathjax.org>); Copyright (c) 2014-2018 Khan Academy "
              "(<www.khanacademy.org>), with Reserved Font Names KaTeX_*", "OFL-1.1"))]


def standard(licence):
    if licence not in standard_used:
        standard_used.append(licence)
    return f"the standard {licence} text is at the end of this file"


sections = []
seen = {}  # licence text -> the package it was first printed under; later packages refer to it
for name, version in packages:
    folder = os.path.join(CACHE, name.lower(), version.lower())
    nuspec = next(iter(glob.glob(os.path.join(folder, "*.nuspec"))), None)
    if nuspec is None:
        sys.exit(f"{name} {version} is not in {CACHE}; run dotnet restore first.")
    meta = ET.parse(nuspec).getroot()
    ns = {"n": meta.tag.split("}")[0].strip("{")}
    def field(tag):
        node = meta.find(f"n:metadata/n:{tag}", ns)
        return node.text.strip() if node is not None and node.text else None
    lic = meta.find("n:metadata/n:license", ns)
    licence = lic.text.strip() if lic is not None and lic.get("type") == "expression" else None
    files = []
    if lic is not None and lic.get("type") == "file":
        files.append(os.path.join(folder, lic.text.strip()))
    files += [f for f in glob.glob(os.path.join(folder, "**", "*"), recursive=True)
              if os.path.isfile(f) and re.search(r"(^|/)(licen[cs]e|notice|copying|third.?party)[^/]*$|/licen[cs]es?/[^/]+$", f, re.I)
              and not f.endswith((".dll", ".so", ".dylib", ".nupkg", ".p7s"))]
    texts = []
    for f in dict.fromkeys(files):
        try:
            text = open(f, encoding="utf-8", errors="replace").read().strip()
        except OSError:
            continue
        if not text:
            continue
        where = os.path.relpath(f, folder)
        if text in seen:
            entry = f"[{where}] Same text as under {seen[text]}."
        else:
            seen[text] = f"{name} {version}"
            entry = f"[{where}]\n{text}"
        if entry not in texts:
            texts.append(entry)
    head = f"{name} {version}\n" + "".join(f"{k}: {v}\n" for k, v in (
        ("Licence", licence), ("Licence URL", field("licenseUrl") if not licence else None),
        ("Project", field("projectUrl")), ("Copyright", field("copyright"))) if v)
    if not texts and licence in STANDARD:
        texts.append(f"(No licence text in the package; {standard(licence)}.)")
    for part, owner, terms in EMBEDDED.get(name, []):
        texts.append(f"It contains {part}\n{owner}\nLicence: {terms} ({standard(terms)})")
    sections.append(head + ("\n" + "\n\n".join(texts) if texts else "(No licence text in the package; see the licence or URL above.)"))

for name, licence, project, owner, file, (part, part_owner, terms) in VENDORED:
    text = open(os.path.join(ROOT, file), encoding="utf-8").read().strip()
    sections.append(f"{name}\nLicence: {licence}\nProject: {project}\nCopyright: {owner}\n\n[LICENSE]\n{text}\n\n"
                    f"It contains {part}\n{part_owner}\nLicence: {terms} ({standard(terms)})")

for licence in standard_used:
    text = open(STANDARD[licence], encoding="utf-8").read().strip("\n")
    sections.append(f"Standard licence text: {licence}\n\n{text}")

rule = "\n\n" + "=" * 100 + "\n\n"
with open(OUT, "w", encoding="utf-8") as out:
    out.write("Mindweave includes the following third-party software. Each is used under its own licence, given below.\n"
              "Generated by scripts/third-party-notices.py from the packages the desktop application ships." + rule)
    out.write(rule.join(sections) + "\n")
print(f"{len(packages)} packages -> {os.path.relpath(OUT, ROOT)}")
