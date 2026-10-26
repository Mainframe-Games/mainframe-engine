#!/usr/bin/env python3
"""Fetches and imports the Forest's CC0 art into Examples/Forest/Assets/Art (see docs/design/forest.md#assets).

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
- Debris (ambientCG 1K JPG sets of scattered leaves, ADR 0175): Color and Opacity merged into an RGBA PNG, NormalGL,
  and an ORM PNG (R = 255, G = Roughness, B = 0), for the ground-hugging leaf-litter cards.
- Sky (Poly Haven HDRI): the tonemapped JPG, resized to 4096 × 2048 (the engine's panorama path is LDR).
- Cabin (tiling PBR sets for the log cabin, Tools/cabin/build_cabin.py): ambientCG 2K JPG sets and Poly Haven 2K
  textures (diff, nor_gl, arm) → Assets/Art/Cabin/Textures/<Id>_Color.jpg, <Id>_NormalGL.jpg and <Id>_ORM.png
  (R = AO or 255, G = roughness, B = metallic or 0) at 1024².

    python3 Examples/Forest/Tools/fetch_assets.py --only props,debris --assets stone_01,ScatteredLeaves004

fetches only some kinds (terrain, props, debris, sky, cabin) or assets; the NOTICE rows are printed for what it fetched.
`--only metas` fetches nothing and only writes the missing `.meta` sidecars (after `just forest-cabin`).
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
ART = os.path.normpath(os.path.join(HERE, "..", "Assets", "Art"))

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
    ("sand", "Ground054"),    # trodden beach sand (the lake's shore; splat layer 8, the third weight map)
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
    # G8e.7 (ADR 0175): forest-floor scans for the clutter, the understorey and the props near the shots.
    ("root_cluster_02", "1k"),
    ("root_cluster_01", "1k"),
    ("single_root", "1k"),
    ("bark_debris_01", "1k"),
    ("moss_01", "1k"),
    ("stone_01", "1k"),
    ("grass_medium_01", "1k"),
    ("dead_quiver_branch_01", "1k"),
    ("nettle_plant", "1k"),
    # The map's edge (cliffs) and the lake shore: rock faces at the cliffs' feet.
    ("rock_face_01", "1k"),
    ("rock_face_02", "1k"),
    ("mountainside", "1k"),
    # The log cabin's furniture and set dressing, placed at the cabin glTF's Place_<id> empties.
    ("old_bed_frame", "1k"),
    ("wooden_table_02", "1k"),
    ("painted_wooden_chair_02", "1k"),
    ("Rockingchair_01", "1k"),
    ("Shelf_01", "1k"),
    ("wooden_lantern_01", "1k"),
    ("wooden_bucket_01", "1k"),
    ("pot_enamel_01", "1k"),
    ("wooden_axe", "1k"),
]

# (ambientCG id, size): scattered-leaf debris with opacity, laid on the ground as cards (ADR 0175).
DEBRIS = [
    ("ScatteredLeaves004", 1024),
]

# (material, source, id): the log cabin's tiling sets; the material names are build_cabin.py's.
CABIN = [
    ("LogWall", "polyhaven", "rough_wood"),        # weathered grey-brown wood with checks: the debarked logs
    ("LogEnd", "ambientcg", "TreeEnd002"),         # a sawn log end (rings; the bark ring falls outside the caps' UVs)
    ("Floor", "polyhaven", "old_wood_floor"),      # dark worn floorboards
    ("Roof", "ambientcg", "WoodSiding012"),        # weathered, slightly mossy wood shingles
    ("Stone", "polyhaven", "stone_wall"),          # fieldstone with mortar: footing, hearth, chimney
    ("DoorWood", "polyhaven", "weathered_planks"),  # dark weathered planks: door, frames, shelves
    ("Fabric", "polyhaven", "wool_boucle"),        # checked wool: blanket and rug
    ("Linen", "ambientcg", "Fabric045"),           # rough off-white canvas: mattress, pillow
    ("Leather", "polyhaven", "fabric_leather_02"),  # aged leather: book covers
]
CABIN_SIZE = 1024

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


def terrain(cache, rows, wanted=None):
    for tag, asset in TERRAIN:
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
        rows.append((found["displayName"], asset, f"Assets/Art/Terrain/{tag.capitalize()}/", found["shortLink"], url, "ambientCG (no author listed)"))
        print(f"terrain {tag:8} {asset}", file=sys.stderr)


def debris(cache, rows, wanted=None):
    for asset, size in DEBRIS:
        if wanted and asset not in wanted:
            continue
        meta = get_json(f"https://ambientcg.com/api/v2/full_json?id={asset}&include=downloadData,displayData")
        found = meta["foundAssets"][0]
        name = f"{asset}_1K-JPG.zip"
        url = f"https://ambientcg.com/get?file={name}"
        path = cached(cache, os.path.join("ambientcg", name), url)
        with zipfile.ZipFile(path) as zf:
            def read(suffix, mode):
                entry = next((n for n in zf.namelist() if n.endswith(f"_{suffix}.jpg")), None)
                if entry is None:
                    return None
                img = Image.open(io.BytesIO(zf.read(entry))).convert(mode)
                return img.resize((size, size), Image.LANCZOS) if img.size != (size, size) else img

            out = os.path.join(ART, "Debris", asset)
            color = read("Color", "RGB")
            alpha = read("Opacity", "L")
            save_png(Image.merge("RGBA", (*color.split(), alpha)), os.path.join(out, f"{asset}_Color.png"))
            save_jpg(read("NormalGL", "RGB"), os.path.join(out, f"{asset}_NormalGL.jpg"), 95, subsampling=0)
            rough = read("Roughness", "L")
            save_png(Image.merge("RGB", (Image.new("L", (size, size), 255), rough, Image.new("L", (size, size), 0))), os.path.join(out, f"{asset}_ORM.png"))
        rows.append((found["displayName"], asset, f"Assets/Art/Debris/{asset}/", found["shortLink"], url, "ambientCG (no author listed)"))
        print(f"debris {asset}", file=sys.stderr)


def props(cache, rows, wanted=None):
    for asset, res in PROPS:
        if wanted and asset not in wanted:
            continue
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
        rows.append((info["name"], asset, f"Assets/Art/Props/{asset}/", f"https://polyhaven.com/a/{asset}", entry["url"], f"Poly Haven ({authors})"))
        print(f"prop {asset} {res}", file=sys.stderr)


def cabin(cache, rows, wanted=None):
    out = os.path.join(ART, "Cabin", "Textures")
    size = (CABIN_SIZE, CABIN_SIZE)

    def fit(img):
        return img.resize(size, Image.LANCZOS) if img.size != size else img

    for material, source, asset in CABIN:
        if wanted and asset not in wanted:
            continue
        if source == "ambientcg":
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
                rough = read("Roughness", "L")
                ao = read("AmbientOcclusion", "L") or Image.new("L", size, 255)
                metal = read("Metalness", "L") or Image.new("L", size, 0)
            orm = Image.merge("RGB", (ao, rough, metal))
            rows.append((found["displayName"], asset, "Assets/Art/Cabin/Textures/", found["shortLink"], url, "ambientCG (no author listed)"))
        else:
            info = get_json(f"https://api.polyhaven.com/info/{asset}")
            files = get_json(f"https://api.polyhaven.com/files/{asset}")

            def read(key, mode):
                f = files[key]["2k"]["jpg"]
                return fit(Image.open(cached(cache, os.path.join("polyhaven", asset, os.path.basename(f["url"])), f["url"], f["md5"])).convert(mode))

            color = read("Diffuse", "RGB")
            normal = read("nor_gl", "RGB")
            orm = read("arm", "RGB")  # Poly Haven's ARM is already R = AO, G = roughness, B = metallic
            url = files["Diffuse"]["2k"]["jpg"]["url"]
            authors = ", ".join(info["authors"])
            rows.append((info["name"], asset, "Assets/Art/Cabin/Textures/", f"https://polyhaven.com/a/{asset}", url, f"Poly Haven ({authors})"))
        save_jpg(color, os.path.join(out, f"{asset}_Color.jpg"), 90)
        save_jpg(normal, os.path.join(out, f"{asset}_NormalGL.jpg"), 95, subsampling=0)
        save_png(orm, os.path.join(out, f"{asset}_ORM.png"))
        print(f"cabin {material:8} {asset}", file=sys.stderr)


def sky(cache, rows):
    info = get_json(f"https://api.polyhaven.com/info/{HDRI}")
    files = get_json(f"https://api.polyhaven.com/files/{HDRI}")
    tm = files["tonemapped"]
    src = cached(cache, os.path.join("polyhaven", HDRI, os.path.basename(tm["url"]).replace("%20", "_")), tm["url"], tm["md5"])
    img = Image.open(src).convert("RGB").resize(SKY_SIZE, Image.LANCZOS)
    save_jpg(img, os.path.join(ART, "Sky", f"{HDRI}_4k.jpg"), 90)
    authors = ", ".join(info["authors"])
    rows.append((info["name"] + " (tonemapped)", HDRI, "Assets/Art/Sky/", f"https://polyhaven.com/a/{HDRI}", tm["url"], f"Poly Haven ({authors})"))
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
    parser.add_argument("--only", default="terrain,props,debris,sky,cabin",
                        help="comma-separated kinds: terrain, props, debris, sky, cabin (metas alone: only write .meta sidecars)")
    parser.add_argument("--assets", default="", help="comma-separated asset ids (terrain, props, debris and cabin); default: all")
    args = parser.parse_args()
    kinds = set(args.only.split(","))
    wanted = set(a for a in args.assets.split(",") if a) or None
    rows = []
    if "terrain" in kinds:
        terrain(args.cache, rows, wanted)
    if "props" in kinds:
        props(args.cache, rows, wanted)
    if "debris" in kinds:
        debris(args.cache, rows, wanted)
    if "sky" in kinds:
        sky(args.cache, rows)
    if "cabin" in kinds:
        cabin(args.cache, rows, wanted)
    write_metas()
    for name, asset, where, page, url, author in rows:
        print(f"| {name} | `{asset}` | `{where}` | [{page.split('//')[1]}]({page}) · [download]({url}) | {author} | CC0 1.0 |")


if __name__ == "__main__":
    main()
