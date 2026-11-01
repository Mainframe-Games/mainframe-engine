#!/usr/bin/env python3
"""Puts what build_resident.py needs into this user's Blender: the MPFB extension and the CC0 asset packs she is made from.

    python3 Examples/Neon/Tools/apartment/fetch_mpfb_packs.py       (BLENDER=<path> overrides; `just neon-art` runs it)

Nothing here ships: only what build_resident.py derives from it (see NOTICE.md). Each pack is about 5-280 MB and is skipped
when its marker (packs/<pack>.json in MPFB's data) is there. Archives are extracted by hand so no entry can leave the data
folder, and variants with explicit anatomy are left out: she never uses them.
"""

import json
import os
import subprocess
import sys
import tempfile
import zipfile

BLENDER = os.environ.get("BLENDER", "/Applications/Blender.app/Contents/MacOS/Blender")
MIRROR = "https://files.makehumancommunity.org/asset_packs"
PACKS = ("makehuman_system_assets", "skins01", "hair01", "eyebrows01", "eyelashes01", "underwear01")
SKIP = "genital"


def extensions_root():
    """Blender's user extensions folder, as Blender itself reports it."""
    code = "import bpy, json; print('EXTENSIONS=' + json.dumps(bpy.utils.user_resource('EXTENSIONS')))"
    out = subprocess.run([BLENDER, "-b", "--factory-startup", "--python-expr", code], capture_output=True, text=True, check=True).stdout
    line = next(x for x in out.splitlines() if x.startswith("EXTENSIONS="))
    return json.loads(line.split("=", 1)[1])


def extract(archive, target):
    root = os.path.realpath(target)
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            if SKIP in entry.filename.lower():
                continue
            path = os.path.realpath(os.path.join(root, entry.filename))
            if not path.startswith(root + os.sep):
                sys.exit(f"{archive}: {entry.filename} leaves the data folder")
            z.extract(entry, root)


def main():
    root = extensions_root()
    if not os.path.isdir(os.path.join(root, "blender_org", "mpfb")):
        print("installing MPFB")
        subprocess.run([BLENDER, "--online-mode", "--command", "extension", "sync"], check=True)
        subprocess.run([BLENDER, "--online-mode", "--command", "extension", "install", "-s", "-e", "mpfb"], check=True)
    data = os.path.join(root, ".user", "blender_org", "mpfb", "data")
    os.makedirs(data, exist_ok=True)
    for pack in PACKS:
        if os.path.exists(os.path.join(data, "packs", pack + ".json")):
            continue
        print("fetching", pack)
        with tempfile.TemporaryDirectory() as tmp:
            archive = os.path.join(tmp, pack + ".zip")
            subprocess.run(["curl", "-sSL", "--fail", "--retry", "5", "--connect-timeout", "20", "-o", archive, f"{MIRROR}/{pack}/{pack}_cc0.zip"], check=True)
            extract(archive, data)
    print("MPFB and its packs are in", data)


main()
