"""Builds the woman on the loft's bed: Examples/Neon/Assets/Art/Apartment/resident.gltf (+ .bin, Textures/resident-*.png).

    just neon-art
    "$BLENDER" -b --factory-startup --python Examples/Neon/Tools/apartment/build_resident.py [-- --preview DIR [--no-export]]

She is an adult woman of about 1.73 m in black lingerie and stockings, reclining on her left side, propped on her left
elbow with her head on her hand, right knee drawn up. The character comes from MPFB (the MakeHuman Plugin for Blender) and
its CC0 asset packs, which fetch_mpfb_packs.py installs in this Blender: body, skin, eyes, brows, lashes, hair and the
stockings' mesh are theirs (NOTICE.md lists each), shaped here by macro details and face targets. The lingerie and the
stockings' texture are ours, baked by this script: the lingerie is a shell of her own torso, a few millimetres off the
skin so it has her shape and her weights, cut and patterned by lingerie() in her UV space from shapes measured on her
standing mesh.

The pose is set from the rig's A-pose bone by bone (J, as she lies) and then applied as the rest pose, so the model's
bounds are her real ones and the idle only moves her a little.

Conventions: glTF space, metres, +Y up, the origin on the mattress under her pelvis, her feet towards +X, her front
towards -Z. One skinned mesh, Resident, with surfaces by material: Skin, Lingerie, Stocking, Hair, Eye, Brow, Lash. One
clip, idle-loop, 8 s, closing on itself: NeonResident plays it.
"""

import addon_utils
import bmesh
import bpy
import json
import math
import os
import shutil
import sys
import tempfile
from mathutils import Matrix, Quaternion, Vector

import numpy as np

addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)

from bl_ext.blender_org.mpfb.services.humanservice import HumanService  # noqa: E402
from bl_ext.blender_org.mpfb.services.locationservice import LocationService  # noqa: E402
from bl_ext.blender_org.mpfb.services.targetservice import TargetService  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
NEON = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(NEON, "Assets", "Art", "Apartment")
FPS = 24
LOOP_SECONDS = 8

SKIN = "darthfurby_caucasian_female"
HAIR = "toigo_blunt_bob"
BROWS = "mindfront_eyebrows_03"
LASHES = "mindfront_eyelashes_02"
STOCKING = "v0rt3x_stockings_black_fishnet_medium"
SKIN_SIZE = 2048        # the skin and the lingerie share her UV layout
DETAIL_SIZE = 1024
SHELL = 0.0028          # lingerie off the skin, metres
STOCKING_OFFSET = 0.004 # stockings off the skin, so bent knees and toes stay inside them
STOCKING_BAND = 0.08    # the lace at the top of the thigh

MACRO = {"gender": 1.0, "age": 0.55, "muscle": 0.45, "weight": 0.42, "proportions": 0.8, "height": 0.52, "cupsize": 0.55, "firmness": 0.6,
         "race": {"asian": 0.15, "caucasian": 0.75, "african": 0.1}}

# Target files (under MPFB's data/targets) and weights: a softer, narrower face than the macro's average.
TARGETS = {
    "head/head-oval": 0.55, "head/head-invertedtriangular": 0.3, "head/head-scale-horiz-decr": 0.25,
    "chin/chin-width-decr": 0.6, "chin/chin-bones-decr": 0.55, "chin/chin-triangle": 0.3,
    "nose/nose-scale-horiz-decr": 0.4, "nose/nose-point-width-decr": 0.45, "nose/nose-volume-decr": 0.3, "nose/nose-point-up": 0.25,
    "mouth/mouth-upperlip-volume-incr": 0.45, "mouth/mouth-lowerlip-volume-incr": 0.4, "mouth/mouth-cupidsbow-incr": 0.4,
    "mouth/mouth-scale-horiz-decr": 0.12, "mouth/mouth-angles-up": 0.2,
    "neck/neck-scale-horiz-decr": 0.35, "neck/neck-scale-depth-decr": 0.3,
    "torso/measure-waist-circ-decr": 0.4, "torso/measure-shoulder-dist-decr": 0.35,
}
SIDED_TARGETS = {"cheek/{}-cheek-bones-incr": 0.35, "cheek/{}-cheek-volume-decr": 0.35, "eyes/{}-eye-scale-incr": 0.3,
                 "eyes/{}-eye-corner2-up": 0.25, "eyes/{}-eye-bag-decr": 0.5, "eyes/{}-eye-height2-incr": 0.35}

ELBOW_HEIGHT = 0.045       # the elbow's joint above the origin
CHEEK_WRIST = (0.0, 0.03, -0.17)        # her wrist from the middle of her head (Blender axes): under her jaw
CHEEK_FINGERS = (-0.02, 0.075, -0.03)   # the root of her fingers: along her cheek
FOREARM_SHARE = 0.5                     # how much of the turn to the palm her forearm takes; her wrist takes the rest
FINGER_CURL = 1.0

BREATH = 0.007
DOUBLE_SIDED = ("Hair", "Stocking", "Brow", "Lash")

# name: (roughness, cutout)
MATERIALS = {"Skin": (0.5, False), "Lingerie": (0.52, True), "Stocking": (0.6, True), "Hair": (0.62, True), "Eye": (0.1, True),
             "Brow": (0.6, False), "Lash": (0.5, False)}


def G(p):
    """glTF (x, y up, z front) -> Blender (x, -z, y)."""
    return Vector((p[0], -p[2], p[1]))


# Where her joints lie (glTF points). Only the directions between them are used: her bones keep their own lengths.
J = {
    "pelvis": (0.0, 0.175, 0.0), "waist": (-0.2, 0.195, 0.0), "ribs": (-0.36, 0.255, -0.005), "chest": (-0.49, 0.335, -0.01),
    "neck": (-0.6, 0.415, -0.015), "skull": (-0.655, 0.44, -0.03), "head": (-0.74, 0.475, -0.05),
    "shoulder_r": (-0.45, 0.478, 0.0), "elbow_r": (-0.2, 0.43, -0.15), "wrist_r": (0.02, 0.365, -0.14), "fingers_r": (0.16, 0.33, -0.12),
    "shoulder_l": (-0.635, 0.245, -0.01), "elbow_l": (-0.72, 0.03, -0.13), "wrist_l": (-0.76, 0.26, -0.14), "fingers_l": (-0.74, 0.415, -0.115),
    "hip_r": (0.03, 0.265, 0.0), "knee_r": (0.3, 0.27, -0.3), "ankle_r": (0.62, 0.12, -0.1), "toes_r": (0.79, 0.07, -0.17),
    "hip_l": (0.03, 0.085, 0.0), "knee_l": (0.4, 0.095, -0.07), "ankle_l": (0.74, 0.06, 0.04), "toes_l": (0.91, 0.05, -0.03),
}

# Bone: from joint, to joint, twist about the bone (degrees). Parents before children.
POSE = [
    ("spine_01", "pelvis", "waist", 0), ("spine_02", "waist", "ribs", 0), ("spine_03", "ribs", "chest", 0), ("neck_01", "chest", "skull", 0),
    ("head", "skull", "head", 0),
    ("upperarm_r", "shoulder_r", "elbow_r", 0), ("lowerarm_r", "elbow_r", "wrist_r", 0), ("hand_r", "wrist_r", "fingers_r", 0),
    ("upperarm_l", "shoulder_l", "elbow_l", 0), ("lowerarm_l", "elbow_l", "wrist_l", 0), ("hand_l", "wrist_l", "fingers_l", 0),
    ("thigh_r", "hip_r", "knee_r", 0), ("calf_r", "knee_r", "ankle_r", 0), ("foot_r", "ankle_r", "toes_r", 0),
    ("thigh_l", "hip_l", "knee_l", 0), ("calf_l", "knee_l", "ankle_l", 0), ("foot_l", "ankle_l", "toes_l", 0),
]


def activate(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def build_human():
    data = LocationService.get_user_data()
    targets = LocationService.get_mpfb_data("targets")
    macro = TargetService.get_default_macro_info_dict()
    macro.update(MACRO)
    base = HumanService.create_human(macro_detail_dict=macro)
    weights = dict(TARGETS)
    for path, weight in SIDED_TARGETS.items():
        weights[path.format("l")] = weight
        weights[path.format("r")] = weight
    for path, weight in weights.items():
        TargetService.load_target(base, os.path.join(targets, path + ".target.gz"), weight=weight)
    arm = HumanService.add_builtin_rig(base, "game_engine")
    HumanService.set_character_skin(os.path.join(data, "skins", SKIN, SKIN + ".mhmat"), base, skin_type="GAMEENGINE")

    def asset(sub, name, kind):
        return HumanService.add_mhclo_asset(os.path.join(data, sub, name + ".mhclo"), base, asset_type=kind, subdiv_levels=0, material_type="MAKESKIN")

    parts = {
        "Eye": asset("eyes/high-poly", "high-poly", "Eyes"),
        "Brow": asset(f"eyebrows/{BROWS}", BROWS, "Eyebrows"),
        "Lash": asset(f"eyelashes/{LASHES}", LASHES, "Eyelashes"),
        "Hair": asset(f"hair/{HAIR}", HAIR, "Hair"),
        "Stocking": asset(f"clothes/{STOCKING}", STOCKING, "Clothes"),
    }
    return base, arm, parts


def image_of(material):
    """The albedo image of an imported material: its first image node that is not a normal or bump map."""
    for node in material.node_tree.nodes:
        if node.type == "TEX_IMAGE" and node.image and not any(k in node.image.name.lower() for k in ("nrm", "norm", "bump", "spec")):
            return node.image
    return None


def own_image(source, name, size, tint=None):
    """A resized copy of an image under its export name (new, so nothing of the source's path follows it); tint multiplies
    its colour, not its alpha."""
    scaled = source.copy()
    scaled.scale(size, size)
    px = np.empty(size * size * 4, dtype=np.float32)
    scaled.pixels.foreach_get(px)
    bpy.data.images.remove(scaled)
    if tint is not None:
        px = px.reshape(-1, 4)
        px[:, :3] *= np.array(tint, dtype=np.float32)
    image = bpy.data.images.new(name, size, size, alpha=True)
    image.pixels.foreach_set(px.ravel())
    image.pack()
    return image


def set_material(obj, name, image=None, colour=(0.02, 0.015, 0.012)):
    """Replaces the object's materials by one plain PBR material the glTF exporter can follow."""
    roughness, cutout = MATERIALS[name]
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()
    out = tree.nodes.new("ShaderNodeOutputMaterial")
    bsdf = tree.nodes.new("ShaderNodeBsdfPrincipled")
    tree.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0
    bsdf.inputs["Base Color"].default_value = (*colour, 1)
    if image is not None:
        tex = tree.nodes.new("ShaderNodeTexImage")
        tex.image = image
        tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        if cutout:
            tree.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    return mat


def apply_modifiers(obj, kinds):
    activate(obj)
    if obj.data.shape_keys:
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    for modifier in list(obj.modifiers):
        if modifier.type in kinds:
            bpy.ops.object.modifier_apply(modifier=modifier.name)


def bind_to_head(obj):
    """Her hair moves with her head alone: fitted to the standing body, its ends would follow her shoulders and arms."""
    obj.vertex_groups.clear()
    group = obj.vertex_groups.new(name="head")
    group.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")


def bake_body(base):
    """Bakes the targets and drops the helpers; returns the vertex indices the hair's Delete group marks (her scalp)."""
    TargetService.bake_targets(base)
    activate(base)
    scalp_group = None
    for modifier in list(base.modifiers):
        if modifier.type == "MASK" and modifier.vertex_group == "body":
            bpy.ops.object.modifier_apply(modifier=modifier.name)
        elif modifier.type == "MASK":
            scalp_group = modifier.vertex_group  # keep her scalp: the hair is cut out, so it must not show through her
            base.modifiers.remove(modifier)
    if scalp_group is None or scalp_group not in base.vertex_groups:
        return set()
    index = base.vertex_groups[scalp_group].index
    return {v.index for v in base.data.vertices if any(g.group == index for g in v.groups)}


def triangles(obj):
    """The UVs (T, 3, 2) and points (T, 3, 3) of a triangulated mesh, and the source vertex indices (T, 3)."""
    mesh = obj.data
    mesh.calc_loop_triangles()
    uv_layer = mesh.uv_layers.active.data
    uvs = np.array([[uv_layer[i].uv[:] for i in t.loops] for t in mesh.loop_triangles], dtype=np.float32)
    indices = np.array([t.vertices[:] for t in mesh.loop_triangles], dtype=np.int32)
    co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", co)
    return uvs, co.reshape(-1, 3)[indices], indices


def rasterize(uvs, points, size):
    """Her surface in texture space: (size, size, 3) points, NaN where no triangle covers the texel."""
    out = np.full((size, size, 3), np.nan, dtype=np.float32)
    corners = uvs * size
    for tri, tri_points in zip(corners, points):
        lo = np.maximum(np.floor(tri.min(axis=0)).astype(int), 0)
        hi = np.minimum(np.ceil(tri.max(axis=0)).astype(int) + 1, size)
        if lo[0] >= hi[0] or lo[1] >= hi[1]:
            continue
        xs, ys = np.meshgrid(np.arange(lo[0], hi[0]) + 0.5, np.arange(lo[1], hi[1]) + 0.5)
        a, b, c = tri
        det = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(det) < 1e-9:
            continue
        w0 = ((b[1] - c[1]) * (xs - c[0]) + (c[0] - b[0]) * (ys - c[1])) / det
        w1 = ((c[1] - a[1]) * (xs - c[0]) + (a[0] - c[0]) * (ys - c[1])) / det
        w2 = 1 - w0 - w1
        inside = (w0 >= -0.02) & (w1 >= -0.02) & (w2 >= -0.02)
        if inside.any():
            p = w0[..., None] * tri_points[0] + w1[..., None] * tri_points[1] + w2[..., None] * tri_points[2]
            out[lo[1]:hi[1], lo[0]:hi[0]][inside] = p[inside]
    return out


def dilate(rgba, passes):
    """Spreads the opaque texels into their neighbours' colour (not their alpha), so mips and seams stay dark."""
    colour = rgba[..., :3].copy()
    known = rgba[..., 3] > 0
    for _ in range(passes):
        for axis, step in ((0, 1), (0, -1), (1, 1), (1, -1)):
            near = np.roll(known, step, axis=axis)
            fill = near & ~known
            colour[fill] = np.roll(colour, step, axis=axis)[fill]
            known = known | fill
    rgba[..., :3] = colour
    return rgba


def lace_pattern(size):
    """Lace in texture space (about 13 mm flowers in a fine net): True where there is thread."""
    v, u = np.mgrid[0:size, 0:size].astype(np.float32)
    cell = 13.0 * size / 2048
    row = np.floor(v / (cell * 0.866))
    shift = 0.5 * (row % 2)
    cx = (np.floor(u / cell + shift) + 0.5 - shift) * cell
    cy = (row + 0.5) * cell * 0.866
    dx, dy = u - cx, v - cy
    r = np.hypot(dx, dy) / cell
    theta = np.arctan2(dy, dx)
    petals = (r < 0.34 + 0.07 * np.cos(6 * theta)) & (r > 0.09)
    wave = 2 * np.pi / (4.0 * size / 2048)
    net = np.abs(np.sin(u * wave) * np.sin(v * wave)) < 0.32
    return petals | net


def lingerie(P, size):
    """The set, as (size, size, 4) linear RGBA over her surface P (standing, A-pose): a satin bra with a lace top edge and
    full briefs with lace at the waist and legs, both trimmed in magenta. Every shape is measured in metres on her."""
    x, y, z = P[..., 0], P[..., 1], P[..., 2]
    valid = ~np.isnan(x)
    ax = np.abs(x)
    with np.errstate(invalid="ignore"):
        side = np.arctan2(y, x) * 0.16          # roughly metres around her, for the scallops
        front = y < 0.012

        # The bust: an ellipse on her front around each apex, opening into the band below it.
        u, v = ax - LANDMARKS["bust_x"], z - LANDMARKS["bust_z"]
        top = 0.092 - 0.05 * np.clip(u / 0.09, 0, 1)
        cup = front & ((u / 0.1) ** 2 + (np.clip(v, 0, None) / top) ** 2 < 1) & (v > -0.1)
        band = (z > LANDMARKS["bust_z"] - 0.1) & (z < LANDMARKS["bust_z"] - 0.07 + 0.015 * np.cos(np.arctan2(y, x) * 2))
        gore = front & (ax < LANDMARKS["bust_x"]) & (v < 0.0) & (v > -0.09)
        lace_top = cup & (v > 0.035)

        # Straps: the tops of the cups over her shoulders to the band behind.
        shoulder_x = LANDMARKS["bust_x"] + 0.018
        strap = (np.abs(ax - (shoulder_x + 0.012 * np.clip((z - LANDMARKS["bust_z"]) / 0.2, 0, 1))) < 0.0075) & (z > LANDMARKS["bust_z"] + 0.05)
        strap &= z < LANDMARKS["shoulder_z"] + 0.1

        # The briefs: from a high waist to the leg lines, cut fuller at the back.
        waist = LANDMARKS["crotch_z"] + 0.15 + 0.008 * np.cos(side * 2 * np.pi / 0.2)
        back = np.clip((y + 0.02) / 0.05, 0, 1)
        leg_front = LANDMARKS["crotch_z"] - 0.012 + 0.13 * np.clip(ax / 0.17, 0, 1) ** 1.35
        leg_back = LANDMARKS["crotch_z"] - 0.03 + 0.07 * np.clip(ax / 0.17, 0, 1) ** 2.2
        leg = leg_front * (1 - back) + leg_back * back
        briefs = (z < waist) & (z > leg)
        scallop = 0.0035 * np.abs(np.sin(side * np.pi / 0.021))
        edge = np.minimum(waist - z, z - leg)
        briefs &= edge > -scallop
        briefs_lace = briefs & (edge < 0.03)

    lace = lace_pattern(size)
    solid = (cup & ~lace_top) | band | gore | strap | (briefs & ~briefs_lace)
    threaded = (lace_top | briefs_lace) & lace
    trim = (briefs & (edge < 0.0055) & (edge > 0.0012)) | (band & (np.abs(z - (LANDMARKS["bust_z"] - 0.1)) < 0.005)) | (strap & (front | ~front))
    trim |= cup & ((u / 0.1) ** 2 + (np.clip(v, 0, None) / top) ** 2 > 0.93) & (v > 0)
    satin = np.array([0.008, 0.007, 0.01], dtype=np.float32)
    thread = np.array([0.075, 0.009, 0.042], dtype=np.float32)
    magenta = np.array([0.42, 0.012, 0.16], dtype=np.float32)
    rgba = np.zeros((size, size, 4), dtype=np.float32)
    on = valid & (solid | threaded)
    rgba[on & threaded, :3] = thread
    rgba[on & solid, :3] = satin
    rgba[on & trim, :3] = magenta
    rgba[on, 3] = 1
    return dilate(rgba, 3)


LANDMARKS = {}


def measure(base):
    """Fills LANDMARKS from her standing mesh."""
    co = np.empty(len(base.data.vertices) * 3, dtype=np.float32)
    base.data.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    height = co[:, 2].max()
    chest = co[(co[:, 0] > 0.02) & (co[:, 0] < 0.17) & (co[:, 2] > 0.62 * height) & (co[:, 2] < 0.8 * height)]
    apex = chest[np.argmin(chest[:, 1])]
    middle = co[np.abs(co[:, 0]) < 0.006]
    torso = middle[middle[:, 2] > 0.3 * height]
    LANDMARKS.update(height=float(height), bust_x=float(apex[0]), bust_z=float(apex[2]), bust_y=float(apex[1]),
                     crotch_z=float(torso[:, 2].min()), shoulder_z=float(0.83 * height))


def make_lingerie(base):
    """The shell: a copy of her torso pushed out along its normals, cut by the baked texture."""
    uvs, points, indices = triangles(base)
    z = points[..., 2].mean(axis=1)
    near = (z > LANDMARKS["crotch_z"] - 0.06) & (z < LANDMARKS["shoulder_z"] + 0.11) & (np.abs(points[..., 0]).min(axis=1) < 0.24)
    P = rasterize(uvs[near], points[near], SKIN_SIZE)
    rgba = lingerie(P, SKIN_SIZE)
    image = bpy.data.images.new("resident-lingerie", SKIN_SIZE, SKIN_SIZE, alpha=True)
    image.pixels.foreach_set(rgba.ravel())
    image.pack()

    # Keep the faces the texture uses (any texel of theirs is opaque): the rest of the copy goes.
    used = set()
    size = SKIN_SIZE
    px = np.clip((uvs * size).astype(int), 0, size - 1)
    alpha = rgba[..., 3] > 0
    for t, tri in zip(indices[near], px[near]):
        lo, hi = tri.min(axis=0), tri.max(axis=0) + 1
        if alpha[lo[1]:hi[1], lo[0]:hi[0]].any():
            used.update(t.tolist())

    shell = base.copy()
    shell.data = base.data.copy()
    shell.name = "Lingerie"
    bpy.context.scene.collection.objects.link(shell)
    if shell.data.shape_keys:
        activate(shell)
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    bm = bmesh.new()
    bm.from_mesh(shell.data)
    bm.verts.ensure_lookup_table()
    bm.normal_update()
    normals = {v.index: v.normal.copy() for v in bm.verts}
    for v in bm.verts:
        v.co += normals[v.index] * SHELL
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not any(v.index in used for v in f.verts)], context="FACES_ONLY")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.to_mesh(shell.data)
    bm.free()
    set_material(shell, "Lingerie", image)
    return shell


def stocking_image(obj):
    """The stockings' texture, ours, over the stocking mesh's UVs: a fine diamond net with a lace band at the top of the
    thigh. The threads cover more than half the surface, so far away, in the mips, it stays a dark sheer stocking."""
    uvs, points, _ = triangles(obj)
    size = SKIN_SIZE
    P = rasterize(uvs, points, size)
    z = P[..., 2]
    valid = ~np.isnan(z)
    top = float(np.nanmax(z))
    v, u = np.mgrid[0:size, 0:size].astype(np.float32)
    cell = 11.0 * size / 2048
    net = (np.abs(np.sin(np.pi * (u + v) / cell)) < 0.62) | (np.abs(np.sin(np.pi * (u - v) / cell)) < 0.62)
    with np.errstate(invalid="ignore"):
        band = z > top - STOCKING_BAND
        welt = z > top - 0.014
    threaded = (net & ~band) | (band & lace_pattern(size)) | welt
    rgba = np.zeros((size, size, 4), dtype=np.float32)
    on = valid & threaded
    rgba[on, :3] = np.array([0.009, 0.008, 0.011], dtype=np.float32)
    rgba[on & band, :3] = np.array([0.03, 0.009, 0.02], dtype=np.float32)
    rgba[on, 3] = 1
    rgba = dilate(rgba, 3)
    image = bpy.data.images.new("resident-stocking", size, size, alpha=True)
    image.pixels.foreach_set(rgba.ravel())
    image.pack()
    return image


def inflate(obj, amount):
    """Pushes a mesh along its normals."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.normal_update()
    for v in bm.verts:
        v.co += v.normal * amount
    bm.to_mesh(obj.data)
    bm.free()


def darken_scalp(base, skin, scalp, hair_colour):
    """Paints her scalp the hair's colour in the skin texture, so the gaps between the hair's strands read as more hair."""
    if not scalp:
        return
    uvs, points, indices = triangles(base)
    keep = np.array([all(int(i) in scalp for i in t) for t in indices])
    size = skin.size[0]
    P = rasterize(uvs[keep], points[keep], size)
    mask = (~np.isnan(P[..., 0])).astype(np.float32)
    for _ in range(6):
        mask = (mask + np.roll(mask, 1, 0) + np.roll(mask, -1, 0) + np.roll(mask, 1, 1) + np.roll(mask, -1, 1)) / 5
    px = np.empty(size * size * 4, dtype=np.float32)
    skin.pixels.foreach_get(px)
    px = px.reshape(size, size, 4)
    px[..., :3] = px[..., :3] * (1 - mask[..., None]) + np.array(hair_colour, dtype=np.float32) * mask[..., None]
    skin.pixels.foreach_set(px.ravel())
    skin.pack()


def aim(arm, bone, direction, twist=0.0):
    pb = arm.pose.bones[bone]
    m = pb.matrix.copy()
    head = m.to_translation()
    axis = m.to_3x3().col[1].normalized()
    d = direction.normalized()
    q = axis.rotation_difference(d)
    if twist:
        q = Quaternion(d, math.radians(twist)) @ q
    n = (q.to_matrix() @ m.to_3x3()).to_4x4()
    n.translation = head
    pb.matrix = n
    bpy.context.view_layer.update()


def pose(arm):
    activate(arm)
    bpy.ops.object.mode_set(mode="POSE")
    # Up the body -> towards the headboard, her front -> Blender +Y, so her left side is down.
    body = Matrix(((0, 0, -1), (0, -1, 0), (-1, 0, 0)))
    pb = arm.pose.bones["pelvis"]
    m = pb.matrix.copy()
    n = (body @ m.to_3x3()).to_4x4()
    n.translation = G(J["pelvis"])
    pb.matrix = n
    bpy.context.view_layer.update()
    for bone, a, b, twist in POSE:
        aim(arm, bone, G(J[b]) - G(J[a]), twist)
    bpy.ops.object.mode_set(mode="OBJECT")


def lowest_point(meshes, east_of):
    """The lowest point of the meshes as posed, among the vertices east of a line (so her propped arm does not count)."""
    dg = bpy.context.evaluated_depsgraph_get()
    lowest = 1e9
    for obj in meshes:
        ev = obj.evaluated_get(dg)
        mesh = ev.to_mesh()
        co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
        mesh.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        lowest = min(lowest, float(co[co[:, 0] > east_of, 2].min()))
        ev.to_mesh_clear()
    return lowest


def settle(arm, meshes):
    """Rests her hip on the mattress: moves the pelvis so her lowest point is a little under the origin."""
    activate(arm)
    bpy.ops.object.mode_set(mode="POSE")
    pb = arm.pose.bones["pelvis"]
    m = pb.matrix.copy()
    m.translation.z -= lowest_point(meshes, -0.3) + 0.012
    pb.matrix = m
    bpy.context.view_layer.update()
    bpy.ops.object.mode_set(mode="OBJECT")


def prop_arm(arm):
    """Puts her left elbow on the mattress, her forearm up to her cheek and her hand along her jaw."""
    activate(arm)
    bpy.ops.object.mode_set(mode="POSE")
    upper = arm.pose.bones["upperarm_l"]
    shoulder = upper.matrix.to_translation()
    drop = shoulder.z - ELBOW_HEIGHT
    outward = G(J["elbow_l"]) - G(J["shoulder_l"])
    outward.z = 0
    outward.normalize()
    run = math.sqrt(max(upper.length ** 2 - drop ** 2, 0))
    aim(arm, "upperarm_l", outward * run + Vector((0, 0, -min(drop, upper.length))))

    head = arm.pose.bones["head"].matrix
    centre = head.to_translation() + head.to_3x3().col[1].normalized() * 0.09
    elbow = arm.pose.bones["lowerarm_l"].matrix.to_translation()
    wrist_at = centre + Vector(CHEEK_WRIST)
    fingers_at = centre + Vector(CHEEK_FINGERS)
    aim(arm, "lowerarm_l", wrist_at - elbow)
    hand = arm.pose.bones["hand_l"]
    aim(arm, "hand_l", fingers_at - hand.matrix.to_translation())

    def palm_turn():
        """How far her hand must turn about itself for the palm to face the middle of her head (degrees)."""
        along = hand.matrix.to_3x3().col[1].normalized()
        across = (arm.pose.bones["pinky_01_l"].matrix.to_translation() - arm.pose.bones["index_01_l"].matrix.to_translation()).normalized()
        palm = across.cross(along).normalized()     # down, in the rig's A-pose
        want = centre - hand.matrix.to_translation()
        want = (want - along * want.dot(along)).normalized()
        return math.degrees(math.atan2(along.dot(palm.cross(want)), palm.dot(want)))

    aim(arm, "lowerarm_l", wrist_at - elbow, palm_turn() * FOREARM_SHARE)
    for _ in range(3):      # each aim also moves the bone's own direction a little: settles in two
        aim(arm, "hand_l", fingers_at - hand.matrix.to_translation(), palm_turn())
    for side, amount in (("l", 1.0), ("r", 0.7)):
        for finger in ("index", "middle", "ring", "pinky"):
            for joint, angle in (("01", 16), ("02", 24), ("03", 14)):
                pb = arm.pose.bones[f"{finger}_{joint}_{side}"]
                pb.rotation_mode = "QUATERNION"
                pb.rotation_quaternion = Quaternion((1, 0, 0), math.radians(angle * amount * FINGER_CURL))
    bpy.context.view_layer.update()
    bpy.ops.object.mode_set(mode="OBJECT")


def apply_rest(arm, meshes):
    """Makes the pose the rest pose: the meshes are skinned once for good, then the armature takes the pose."""
    for obj in meshes:
        apply_modifiers(obj, {"ARMATURE"})
    activate(arm)
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    for obj in meshes:
        modifier = obj.modifiers.new("Armature", "ARMATURE")
        modifier.object = arm


def join(meshes):
    activate(meshes[0])
    for obj in meshes:
        obj.select_set(True)
    bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = "Resident"
    obj.data.name = "Resident"
    return obj


def idle(arm):
    """The loop: whole sine cycles over LOOP_SECONDS, so it closes on itself. Her ribs breathe (scaled, and undone on what
    hangs from them), her head settles against her hand, her raised knee sways about the line from hip to ankle (the foot
    stays put) and her feet flex."""
    activate(arm)
    bpy.ops.object.mode_set(mode="POSE")
    bones = arm.pose.bones
    action = bpy.data.actions.new("idle-loop")
    arm.animation_data_create()
    arm.animation_data.action = action

    def axis_of(bone, axis):
        return (bones[bone].bone.matrix_local.to_3x3().inverted() @ axis).normalized()

    knee_line = (arm.data.bones["calf_r"].tail_local - arm.data.bones["thigh_r"].head_local).normalized()
    sideways = Vector((0, 1, 0))
    channels = [
        # bone, its axis (armature space), degrees, cycles, phase
        ("spine_02", sideways, 0.5, 3, 0.0), ("spine_03", sideways, 0.45, 3, 0.25),
        ("head", arm.data.bones["head"].y_axis, 1.1, 1, 0.0), ("head", sideways, 0.6, 2, 1.0),
        ("thigh_r", knee_line, 2.2, 1, 0.5),
        ("foot_r", sideways, 4.0, 2, 0.0), ("foot_l", sideways, 2.5, 2, 2.0),
        ("hand_r", sideways, 2.0, 1, 1.5),
    ]
    for bone in {c[0] for c in channels} | {"neck_01", "clavicle_l", "clavicle_r"}:
        bones[bone].rotation_mode = "QUATERNION"
    frames = FPS * LOOP_SECONDS
    for frame in range(frames + 1):
        t = frame / frames
        turns = {}
        for bone, axis, degrees, cycles, phase in channels:
            q = Quaternion(axis_of(bone, axis), math.radians(degrees) * math.sin(2 * math.pi * cycles * t + phase))
            turns[bone] = turns.get(bone, Quaternion()) @ q
        for bone, q in turns.items():
            bones[bone].rotation_quaternion = q
            bones[bone].keyframe_insert("rotation_quaternion", frame=frame)
        breath = 1 + BREATH * math.sin(2 * math.pi * 3 * t)
        bones["spine_03"].scale = (breath, breath, breath)
        bones["spine_03"].keyframe_insert("scale", frame=frame)
        for bone in ("neck_01", "clavicle_l", "clavicle_r"):
            bones[bone].scale = (1 / breath, 1 / breath, 1 / breath)
            bones[bone].keyframe_insert("scale", frame=frame)
    bpy.ops.object.mode_set(mode="OBJECT")
    sc = bpy.context.scene
    sc.render.fps = FPS
    sc.frame_start, sc.frame_end = 0, frames


def export(arm, obj):
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = arm
    tmp = tempfile.mkdtemp(prefix="resident-gltf-")
    path = os.path.join(tmp, "resident.gltf")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLTF_SEPARATE", use_selection=True, export_yup=True, export_apply=False,
                              export_texcoords=True, export_normals=True, export_tangents=False, export_materials="EXPORT",
                              export_image_format="AUTO", export_texture_dir="Textures", export_cameras=False, export_lights=False,
                              export_animations=True, export_skins=True, export_morph=False, export_extras=False,
                              export_force_sampling=True, export_frame_step=1, export_optimize_animation_size=False)
    with open(path) as f:
        gltf = json.load(f)
    gltf.get("asset", {})["generator"] = "Examples/Neon/Tools/apartment/build_resident.py (MPFB, Blender glTF exporter)"
    for anim in gltf.get("animations", []):
        anim["extras"] = {"loop": True}
    for material in gltf.get("materials", []):
        cutout = MATERIALS[material["name"]][1]
        material.pop("alphaCutoff", None)
        material["alphaMode"] = "MASK" if cutout else "OPAQUE"
        if cutout:
            material["alphaCutoff"] = 0.5
        material["doubleSided"] = material["name"] in DOUBLE_SIDED
    os.makedirs(OUT, exist_ok=True)
    shutil.copyfile(os.path.join(tmp, "resident.bin"), os.path.join(OUT, "resident.bin"))
    os.makedirs(os.path.join(OUT, "Textures"), exist_ok=True)
    for image in gltf.get("images", []):
        shutil.copyfile(os.path.join(tmp, image["uri"]), os.path.join(OUT, image["uri"]))
    with open(os.path.join(OUT, "resident.gltf"), "w", newline="\n") as f:
        json.dump(gltf, f, indent=1)
        f.write("\n")
    shutil.rmtree(tmp)
    return gltf


def preview(out_dir, arm):
    os.makedirs(out_dir, exist_ok=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 48
    sc.cycles.use_denoising = True
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.compute_device_type = "METAL"
        prefs.get_devices()
        for d in prefs.devices:
            d.use = True
        sc.cycles.device = "GPU"
    except Exception:
        pass
    sc.render.resolution_x, sc.render.resolution_y = 1280, 720
    sc.render.image_settings.file_format = "PNG"
    world = bpy.data.worlds.new("Preview")
    sc.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.5, 0.47, 0.5, 1)
    bg.inputs["Strength"].default_value = 0.8
    bpy.ops.mesh.primitive_plane_add(size=6)
    sun = bpy.data.objects.new("Key", bpy.data.lights.new("Key", "SUN"))
    sun.data.energy = 3.0
    sun.rotation_euler = (math.radians(55), 0.2, 0.6)
    sc.collection.objects.link(sun)
    head = arm.data.bones["head"]
    face = head.head_local + head.y_axis * 0.09
    views = (("side", G((0.0, 0.75, -2.3)), G((-0.05, 0.2, 0)), 36), ("feet", G((1.9, 1.0, -0.8)), G((0, 0.2, 0)), 40),
             ("torso", G((-0.3, 0.9, -1.15)), G((-0.3, 0.27, 0)), 50), ("face", face + Vector((0.28, 0.72, 0.16)), face, 85))
    for name, e, t, lens in views:
        cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
        cam.data.lens = lens
        sc.collection.objects.link(cam)
        f = (t - e).normalized()
        right = f.cross(Vector((0, 0, 1))).normalized()
        up = right.cross(f).normalized()
        m = Matrix((right, up, -f)).transposed().to_4x4()
        m.translation = e
        cam.matrix_world = m
        sc.camera = cam
        sc.render.filepath = os.path.join(out_dir, f"resident-{name}.png")
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(cam, do_unlink=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview_dir = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)

    base, arm, parts = build_human()
    hair_colour = (0.018, 0.011, 0.008)
    images = {name: image_of(obj.active_material) for name, obj in parts.items()}
    skin_source = image_of(base.active_material)
    skin = own_image(skin_source, "resident-skin", SKIN_SIZE)
    scalp = bake_body(base)
    measure(base)
    darken_scalp(base, skin, scalp, hair_colour)
    set_material(base, "Skin", skin)
    set_material(parts["Eye"], "Eye", own_image(images["Eye"], "resident-eye", DETAIL_SIZE // 2) if images["Eye"] else None)
    set_material(parts["Brow"], "Brow", None, (0.025, 0.016, 0.011))
    set_material(parts["Lash"], "Lash", None, (0.006, 0.005, 0.005))
    set_material(parts["Hair"], "Hair", own_image(images["Hair"], "resident-hair", DETAIL_SIZE, tint=(0.07, 0.055, 0.06)))
    set_material(parts["Stocking"], "Stocking", stocking_image(parts["Stocking"]))
    inflate(parts["Stocking"], STOCKING_OFFSET)
    bind_to_head(parts["Hair"])
    shell = make_lingerie(base)
    meshes = [base, shell, *parts.values()]
    pose(arm)
    settle(arm, [base])
    prop_arm(arm)
    apply_rest(arm, meshes)
    obj = join(meshes)
    idle(arm)
    clips = []
    if "--no-export" not in argv:
        clips = [a["name"] for a in export(arm, obj).get("animations", [])]
    print("RESIDENT triangles", sum(len(p.vertices) - 2 for p in obj.data.polygons), "bones", len(arm.data.bones), "clips", clips)
    if preview_dir:
        preview(preview_dir, arm)


main()
