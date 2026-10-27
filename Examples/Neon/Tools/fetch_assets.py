#!/usr/bin/env python3
"""Fetches Neon's CC0 texture sets into Examples/Neon/Assets/Art/Apartment/Textures (see docs/design/neon.md#assets).

Source: ambientCG only (https://ambientcg.com/api/v2/full_json, https://ambientcg.com/get?file=...); every asset there is
CC0 1.0 (https://docs.ambientcg.com/license/).

    python3 Examples/Neon/Tools/fetch_assets.py [--cache DIR] [--only textures,metas]

Needs curl and Pillow. Downloads are cached (default: a temp folder), so re-running only re-processes. Each set becomes
<Id>_Color.jpg, <Id>_NormalGL.jpg and <Id>_ORM.png (R = AO or 255, G = roughness, B = metallic or 0) at 1024², the
names the apartment's script (Tools/apartment/build_apartment.py) and NeonApartment.ToPbr expect. The NOTICE rows are
printed for what it fetched. `--only metas` fetches nothing and writes the missing `.meta` sidecars (after
`just neon-art`).
"""

import argparse
import io
import json
import os
import secrets
import subprocess
import sys
import tempfile
import zipfile

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.normpath(os.path.join(HERE, "..", "Assets", "Art"))
TEXTURES = os.path.join(ART, "Apartment", "Textures")

# ambientCG ids: the materials of Tools/apartment/build_apartment.py (TEXTURED there says which material uses which).
SETS = [
    "Concrete042A",       # grey, lightly polished concrete: the floors and the balcony
    "Concrete034",        # pale smooth concrete: walls and ceiling (tinted darker in the script)
    "CorrugatedSteel005",  # vertical corrugated steel: the loft's accent walls (tinted near black)
    "MetalWalkway010",    # perforated steel walkway: the mezzanine and the stairs' treads
    "Metal009",           # brushed steel: frames, rails, appliances
    "Wood026",            # dark walnut: cabinets, shelves, desks
    "WoodFloor041",       # dark worn planks: bedroom 2's floor
    "Tiles140",           # dark stone tiles: the bathroom
    "Leather037",         # leather (tinted near black): the sectional, the chair
    "Fabric028",          # woven fabric: bedding, the chair's seat
    "Carpet016",          # short pile carpet (tinted): the rugs
    "Rubber004",          # black rubber: gear cases, desk mats, the stereo
    "PaintedMetal004",    # chipped red paint: gear cases' latches, accents
]
SIZE = 1024


def curl(url, dest=None):
    args = ["curl", "-sS", "-L", "--fail", "-m", "900", url]
    if dest is None:
        return subprocess.check_output(args)
    tmp = dest + ".part"
    subprocess.check_call(args + ["-o", tmp])
    os.replace(tmp, dest)
    return None


def cached(cache, name, url):
    path = os.path.join(cache, name)
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        curl(url, path)
    return path


def get_json(url):
    return json.loads(curl(url))


def save_jpg(img, path, quality, subsampling=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    kw = {"quality": quality, "optimize": True}
    if subsampling is not None:
        kw["subsampling"] = subsampling
    img.save(path, "JPEG", **kw)


def save_png(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, "PNG", optimize=True)


def textures(cache, rows, wanted=None):
    size = (SIZE, SIZE)

    def fit(img):
        return img.resize(size, Image.LANCZOS) if img.size != size else img

    for asset in SETS:
        if wanted and asset not in wanted:
            continue
        meta = get_json(f"https://ambientcg.com/api/v2/full_json?id={asset}&include=downloadData,displayData")
        found = meta["foundAssets"][0]
        name = f"{asset}_2K-JPG.zip"
        url = f"https://ambientcg.com/get?file={name}"
        path = cached(cache, os.path.join("ambientcg", name), url)
        with zipfile.ZipFile(path) as zf:
            def read(suffix, mode):
                entry = next((n for n in zf.namelist() if n.endswith(f"_{suffix}.jpg")), None)
                return None if entry is None else fit(Image.open(io.BytesIO(zf.read(entry))).convert(mode))

            color = read("Color", "RGB")
            normal = read("NormalGL", "RGB")
            rough = read("Roughness", "L") or Image.new("L", size, 160)
            ao = read("AmbientOcclusion", "L") or Image.new("L", size, 255)
            metal = read("Metalness", "L") or Image.new("L", size, 0)
        save_jpg(color, os.path.join(TEXTURES, f"{asset}_Color.jpg"), 90)
        save_jpg(normal, os.path.join(TEXTURES, f"{asset}_NormalGL.jpg"), 95, subsampling=0)
        save_png(Image.merge("RGB", (ao, rough, metal)), os.path.join(TEXTURES, f"{asset}_ORM.png"))
        rows.append((found["displayName"], asset, "Assets/Art/Apartment/Textures/", found["shortLink"], url, "ambientCG (no author listed)"))
        print(f"texture {asset}", file=sys.stderr)


def write_metas():
    """A `.meta` sidecar (AssetDatabase's format: a UID) for every file without one; existing UIDs are kept."""
    prefixes = {".png": "tex", ".jpg": "tex", ".gltf": "mdl"}
    for folder, _, names in os.walk(ART):
        for name in names:
            if name.endswith(".meta") or name.startswith("."):
                continue
            meta = os.path.join(folder, name + ".meta")
            if os.path.exists(meta):
                continue
            prefix = prefixes.get(os.path.splitext(name)[1].lower(), "ast")
            with open(meta, "w", newline="\n") as f:
                f.write(json.dumps({"uid": f"{prefix}_{secrets.token_hex(6)}"}, indent=2) + "\n")
    for folder, _, names in os.walk(ART):  # drop sidecars whose asset is gone
        for name in names:
            if name.endswith(".meta") and not os.path.exists(os.path.join(folder, name[:-5])):
                os.remove(os.path.join(folder, name))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache", default=os.path.join(tempfile.gettempdir(), "neon-assets"))
    parser.add_argument("--only", default="textures,metas")
    parser.add_argument("--assets", default="")
    args = parser.parse_args()
    only = set(args.only.split(","))
    wanted = set(a for a in args.assets.split(",") if a)
    rows = []
    if "textures" in only:
        textures(args.cache, rows, wanted)
    if "metas" in only:
        write_metas()
    for name, asset, where, page, url, author in rows:
        print(f"| {name} | `{asset}` | `{where}` | [{page}]({page}) | {author} | CC0 1.0 |")


if __name__ == "__main__":
    main()
