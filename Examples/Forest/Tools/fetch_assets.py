#!/usr/bin/env python3
"""Fetches and imports the Forest's CC0 art into Examples/Forest/Content/Art (see docs/design/forest.md#assets).

Sources (nothing else): ambientCG (https://ambientcg.com/api/v2/full_json, https://ambientcg.com/get?file=...) and
Poly Haven (https://api.polyhaven.com, https://dl.polyhaven.org). Every asset on both sites is CC0 1.0
(https://docs.ambientcg.com/license/, https://polyhaven.com/license); neither API carries a per-asset licence field.

    python3 Examples/Forest/Tools/fetch_assets.py [--cache DIR]

Needs curl and Pillow. Downloads are cached (default: a temp folder), so re-running only re-processes.

- Terrain layers (ambientCG 2K JPG sets): Color and NormalGL re-encoded at 1024² (TerrainSplatMaterial3D packs every
  layer at LayerTextureSize, 1024 by default), an ORM PNG (R = AmbientOcclusion or 255, G = Roughness, B = 0) and an
  8-bit Height PNG from Displacement.
- Props (Poly Haven glTF): the glTF, its .bin and textures; the grey roughness image is replaced by Poly Haven's ARM
  map (R = AO, G = roughness, B = metallic: glTF's occlusion + metallicRoughness packing), wired as both textures.
- Sky (Poly Haven HDRI): the tonemapped JPG, resized to 4096 × 2048 (the engine's panorama path is LDR).
"""

import argparse
import hashlib
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
ART = os.path.normpath(os.path.join(HERE, "..", "Content", "Art"))

# (folder / tag, ambientCG id). Order = splat channel in ForestAssets.TerrainLayers.
TERRAIN = [
    ("grass", "Ground037"),   # mossy forest grass (meadow)
    ("leaves", "Ground023"),  # forest floor: leaf litter, sticks, dark soil
    ("moss", "Moss002"),
    ("rock", "Rock063"),      # mossy layered cliff rock
    ("dirt", "Ground067"),    # brown forest dirt (paths)
    ("gravel", "Ground108"),  # riverbed gravel (stream bed)
    ("mud", "Ground051"),     # dark wet mud with pebbles (banks)
    ("needles", "Ground082S"),  # forest soil covered in needles and twigs
]
LAYER_SIZE = 1024

# (Poly Haven id, resolution)
PROPS = [
    ("rock_moss_set_01", "2k"),
    ("rock_moss_set_02", "2k"),
    ("boulder_01", "2k"),
    ("rock_07", "1k"),
    ("rock_09", "1k"),
    ("dead_tree_trunk", "2k"),
    ("dead_tree_trunk_02", "2k"),
    ("tree_stump_01", "2k"),
    ("tree_stump_02", "1k"),
    ("dry_branches_medium_01", "1k"),
    ("fern_02", "2k"),
]

HDRI = "lilienstein"
SKY_SIZE = (4096, 2048)


def curl(url, dest=None):
    args = ["curl", "-sS", "-L", "--fail", "-m", "900", url]
    if dest is None:
        return subprocess.check_output(args)
    tmp = dest + ".part"
    subprocess.check_call(args + ["-o", tmp])
    os.replace(tmp, dest)
    return None


def cached(cache, name, url, md5=None):
    path = os.path.join(cache, name)
    if not os.path.exists(path) or (md5 and file_md5(path) != md5):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        curl(url, path)
        if md5 and file_md5(path) != md5:
            sys.exit(f"md5 mismatch: {url}")
    return path


def file_md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def get_json(url):
    return json.loads(curl(url))


def save_jpg(img, path, quality, subsampling=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    kwargs = {"quality": quality, "optimize": True}
    if subsampling is not None:
        kwargs["subsampling"] = subsampling
    img.save(path, "JPEG", **kwargs)


def save_png(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, "PNG", optimize=True)


def terrain(cache, rows):
    for tag, asset in TERRAIN:
        meta = get_json(f"https://ambientcg.com/api/v2/full_json?id={asset}&include=downloadData,displayData")
        found = meta["foundAssets"][0]
        name = f"{asset}_2K-JPG.zip"
        url = f"https://ambientcg.com/get?file={name}"
        path = cached(cache, os.path.join("ambientcg", name), url)
        with zipfile.ZipFile(path) as zf:
            def read(suffix, mode):
                entry = next((n for n in zf.namelist() if n.endswith(f"_{suffix}.jpg")), None)
                if entry is None:
                    return None
                img = Image.open(io.BytesIO(zf.read(entry))).convert(mode)
                return img.resize((LAYER_SIZE, LAYER_SIZE), Image.LANCZOS) if img.size != (LAYER_SIZE, LAYER_SIZE) else img

            out = os.path.join(ART, "Terrain", tag.capitalize())
            color = read("Color", "RGB")
            normal = read("NormalGL", "RGB")
            rough = read("Roughness", "L")
            ao = read("AmbientOcclusion", "L") or Image.new("L", (LAYER_SIZE, LAYER_SIZE), 255)
            height = read("Displacement", "L")
            save_jpg(color, os.path.join(out, f"{asset}_Color.jpg"), 90)
            save_jpg(normal, os.path.join(out, f"{asset}_NormalGL.jpg"), 95, subsampling=0)
            save_png(Image.merge("RGB", (ao, rough, Image.new("L", (LAYER_SIZE, LAYER_SIZE), 0))), os.path.join(out, f"{asset}_ORM.png"))
            save_png(height, os.path.join(out, f"{asset}_Height.png"))
        rows.append((found["displayName"], asset, f"Content/Art/Terrain/{tag.capitalize()}/", found["shortLink"], url, "ambientCG (no author listed)"))
        print(f"terrain {tag:8} {asset}", file=sys.stderr)


def props(cache, rows):
    for asset, res in PROPS:
        info = get_json(f"https://api.polyhaven.com/info/{asset}")
        files = get_json(f"https://api.polyhaven.com/files/{asset}")
        entry = files["gltf"][res]["gltf"]
        out = os.path.join(ART, "Props", asset)
        os.makedirs(os.path.join(out, "textures"), exist_ok=True)
        gltf = json.loads(open(cached(cache, os.path.join("polyhaven", asset, os.path.basename(entry["url"])), entry["url"], entry["md5"]), "rb").read())
        for rel, inc in entry["include"].items():
            if "_rough_" in rel:
                continue  # replaced by the ARM map below
            src = cached(cache, os.path.join("polyhaven", asset, rel), inc["url"], inc["md5"])
            with open(src, "rb") as f, open(os.path.join(out, rel), "wb") as g:
                g.write(f.read())
        arm = files["arm"][res]["jpg"]
        arm_rel = f"textures/{asset}_arm_{res}.jpg"
        src = cached(cache, os.path.join("polyhaven", asset, arm_rel), arm["url"], arm["md5"])
        with open(src, "rb") as f, open(os.path.join(out, arm_rel), "wb") as g:
            g.write(f.read())

        # Point the roughness image at the ARM map (some glTFs already do) and use it for occlusion too.
        orm_images = [i for i, img in enumerate(gltf["images"]) if "_rough_" in img["uri"] or "_arm_" in img["uri"]]
        for i in orm_images:
            gltf["images"][i] = {"mimeType": "image/jpeg", "name": f"{asset}_arm", "uri": arm_rel}
        for material in gltf.get("materials", []):
            mr = material.get("pbrMetallicRoughness", {}).get("metallicRoughnessTexture")
            if mr is not None and gltf["textures"][mr["index"]]["source"] in orm_images:
                material["occlusionTexture"] = {"index": mr["index"]}

        # Cut-out foliage: the glTF's diffuse is a JPG without alpha; merge the separate Alpha map into a PNG.
        if "Alpha" in files:
            alpha = files["Alpha"][res]["jpg"]
            alpha_src = cached(cache, os.path.join("polyhaven", asset, f"textures/{asset}_alpha_{res}.jpg"), alpha["url"], alpha["md5"])
            for i, img in enumerate(gltf["images"]):
                if "_diff_" not in img["uri"]:
                    continue
                diff_path = os.path.join(out, img["uri"])
                rgb = Image.open(diff_path).convert("RGB")
                a = Image.open(alpha_src).convert("L").resize(rgb.size, Image.LANCZOS)
                png_rel = img["uri"][:-4] + ".png"
                save_png(Image.merge("RGBA", (*rgb.split(), a)), os.path.join(out, png_rel))
                os.remove(diff_path)
                gltf["images"][i] = {"mimeType": "image/png", "name": f"{asset}_diff", "uri": png_rel}
        with open(os.path.join(out, f"{asset}_{res}.gltf"), "w", newline="\n") as f:
            json.dump(gltf, f, indent=1)
            f.write("\n")
        authors = ", ".join(info["authors"])
        rows.append((info["name"], asset, f"Content/Art/Props/{asset}/", f"https://polyhaven.com/a/{asset}", entry["url"], f"Poly Haven ({authors})"))
        print(f"prop {asset} {res}", file=sys.stderr)


def sky(cache, rows):
    info = get_json(f"https://api.polyhaven.com/info/{HDRI}")
    files = get_json(f"https://api.polyhaven.com/files/{HDRI}")
    tm = files["tonemapped"]
    src = cached(cache, os.path.join("polyhaven", HDRI, os.path.basename(tm["url"]).replace("%20", "_")), tm["url"], tm["md5"])
    img = Image.open(src).convert("RGB").resize(SKY_SIZE, Image.LANCZOS)
    save_jpg(img, os.path.join(ART, "Sky", f"{HDRI}_4k.jpg"), 90)
    authors = ", ".join(info["authors"])
    rows.append((info["name"] + " (tonemapped)", HDRI, "Content/Art/Sky/", f"https://polyhaven.com/a/{HDRI}", tm["url"], f"Poly Haven ({authors})"))
    print(f"sky {HDRI}", file=sys.stderr)


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
    parser.add_argument("--cache", default=os.path.join(tempfile.gettempdir(), "mainframe-forest-assets"))
    args = parser.parse_args()
    rows = []
    terrain(args.cache, rows)
    props(args.cache, rows)
    sky(args.cache, rows)
    write_metas()
    for name, asset, where, page, url, author in rows:
        print(f"| {name} | `{asset}` | `{where}` | [{page.split('//')[1]}]({page}) · [download]({url}) | {author} | CC0 1.0 |")


if __name__ == "__main__":
    main()
