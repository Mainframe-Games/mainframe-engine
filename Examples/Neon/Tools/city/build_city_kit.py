"""Builds Neon's city kit: Examples/Neon/Assets/Art/City/{towers,rail,vehicles,signs}.gltf (+ .bin, glTF separate) and the
neon sign words atlas signs.png.

    just neon-city-art
    "$BLENDER" -b --factory-startup --python Examples/Neon/Tools/city/build_city_kit.py [-- --preview DIR] [-- --verify DIR]

Everything is generated here (seeded, so every run writes the same meshes). The game builds the city from these meshes
(World/NeonCity.cs) where World/CityLayout.cs places them; the facade, screen, sign and hologram shaders
(Assets/Shaders/*.gdshader) draw their surfaces, so the materials here only name the surfaces and give a flat colour.

Conventions (glTF space): metres, +Y up, every object's origin is its base centre (towers, pillars) or its centre
(vehicles, signs), and it faces +Z (vehicles drive towards -Z, the engine's forward). One mesh object per kit piece,
named as the game looks it up:

- towers.gltf: Tower_Slab, Tower_Setback, Tower_Twin, Tower_Needle, Tower_Block, Tower_Cross, Tower_Octagon,
  Tower_Arcology. Footprints and roof heights are CityLayout.Archetypes' (change both together). Materials: Facade (the
  walls; UV = metres along the facade, metres up), Roof (roofs, ledges, clutter, masts), Trim (the emissive accent bands:
  the facade shader colours them per tower), Beacon (aviation lights).
- rail.gltf: Rail_Track (an 8 m deck along Z, its underside at y = 0), Rail_Pillar (a T pillar, base at y = 0, its top at
  RAIL_HEIGHT - DECK), Train_Car (a 20 m car along Z, wheels at y = 0). Materials: Concrete, Steel, Trim, Body, Windows,
  Lamp.
- vehicles.gltf: Car_Ground (4.6 m), Car_Hover (5.6 m), Airship (64 m; Screen surfaces with UV 0..1 on both flanks).
  Materials: Body, Glass, Lamp_Head, Lamp_Tail, Glow, Screen, Steel; Beam_Head and Beam_Tail are the ground car's light
  pools on the road (flat quads at y = 0.03, UV v = 0 at the car).
- signs.gltf: Billboard (a unit screen facing +Z: Screen with UV 0..1, Frame round and behind it), Sign_Blade (a unit-high
  blade whose two lit Panel faces look along ±Z: UV 0..1, its bracket towards -X), Sign_Board (a unit panel facing +Z),
  Holo_Koi (a koi 1 m long along Z), Holo_Logo (a turning emblem 1 m across).
- signs.png: the words of the neon signs (white on black, 2048 x 1024): eight horizontal words in 512 x 256 cells (four
  per row, the top half), eight vertical words in 256 x 512 cells (the bottom half). Made-up names and plain words in
  Noto Sans JP Bold (SIL OFL 1.1, fetched into a cache folder; credited in NOTICE.md). The sign shader picks a cell by
  INSTANCE_CUSTOM.x.

--preview DIR renders the kit lined up (towers, vehicles, signs) and --verify DIR re-imports the glTFs, lists their
objects and triangle counts and renders the towers.
"""

import bmesh
import bpy
import json
import math
import os
import random
import secrets
import shutil
import subprocess
import sys
import tempfile
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
NEON = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(NEON, "Assets", "Art", "City")
CACHE = os.path.join(tempfile.gettempdir(), "mainframe-neon-assets")
FONT_URL = "https://github.com/notofonts/noto-cjk/raw/main/Sans/SubsetOTF/JP/NotoSansJP-Bold.otf"

RAIL_HEIGHT = 22.0      # CityLayout.RailHeight: the deck's top above the street
DECK = 1.0              # deck thickness (the track's origin is the deck's underside)
STOREY = 3.5            # CityLayout.StoreyHeight

# Words on the signs (made-up names and plain words; no brands). CitySign.VerticalWords / HorizontalWords.
HORIZONTAL_WORDS = ["KIRAMI", "ゼンカ", "NOVA-9", "電脳街", "HOSHIRO", "ヨルノ", "SOLKA", "麺 BAR"]
VERTICAL_WORDS = ["夜市", "電光", "ヒカリ", "麺", "酒場", "ゼンカ", "電脳", "薬"]

COLORS = {
    # name: (base colour (linear), roughness, metallic)
    "Facade": ((0.03, 0.032, 0.04), 0.5, 0.2),
    "Roof": ((0.025, 0.025, 0.028), 0.85, 0.0),
    "Concrete": ((0.06, 0.06, 0.065), 0.85, 0.0),
    "Steel": ((0.05, 0.05, 0.055), 0.4, 0.8),
    "Body": ((0.08, 0.08, 0.09), 0.3, 0.6),
    "Glass": ((0.01, 0.012, 0.016), 0.05, 0.0),
    "Frame": ((0.03, 0.03, 0.035), 0.5, 0.6),
}
EMISSIVE = {
    # name: (emission colour (linear), strength)
    "Trim": ((1.0, 0.2, 0.7), 4.0),
    "Beacon": ((1.0, 0.05, 0.02), 6.0),
    "Windows": ((1.0, 0.75, 0.45), 3.0),
    "Lamp": ((1.0, 0.95, 0.85), 8.0),
    "Lamp_Head": ((1.0, 0.95, 0.85), 10.0),
    "Lamp_Tail": ((1.0, 0.04, 0.03), 6.0),
    "Glow": ((0.1, 0.8, 1.0), 6.0),
    "Screen": ((0.6, 0.2, 1.0), 3.0),
    "Panel": ((1.0, 0.3, 0.8), 3.0),
    "Holo": ((0.2, 0.9, 1.0), 3.0),
    "Beam_Head": ((1.0, 0.9, 0.75), 2.0),
    "Beam_Tail": ((1.0, 0.05, 0.03), 2.0),
}


def G(x, y, z):
    """glTF (x, y up, z front) -> Blender (x, -z, y)."""
    return Vector((x, -z, y))


# ---------------------------------------------------------------------------------------------------------------------
# Materials

MATERIALS = {}


def principled(mat):
    try:
        mat.use_nodes = True
    except Exception:
        pass
    return next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")


def make_materials():
    for name, (color, roughness, metallic) in COLORS.items():
        mat = bpy.data.materials.new(name)
        bsdf = principled(mat)
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        mat.diffuse_color = (*color, 1.0)
        MATERIALS[name] = mat
    for name, (color, strength) in EMISSIVE.items():
        mat = bpy.data.materials.new(name)
        bsdf = principled(mat)
        bsdf.inputs["Base Color"].default_value = (0.02, 0.02, 0.02, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.4
        emission = bsdf.inputs.get("Emission Color") or bsdf.inputs["Emission"]
        emission.default_value = (*color, 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
        mat.diffuse_color = (*color, 1.0)
        MATERIALS[name] = mat


# ---------------------------------------------------------------------------------------------------------------------
# Mesh building


class Builder:
    """One object: a BMesh with a UV layer and a material list. Points are glTF space, relative to the object's origin."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.mats = []

    def mat(self, name):
        if name not in self.mats:
            self.mats.append(name)
        return self.mats.index(name)

    def face(self, pts, mat, uvs=None):
        """A polygon from glTF points (counter-clockwise seen from its front); UVs given, or metres in its plane."""
        verts = [self.bm.verts.new(G(*p)) for p in pts]
        f = self.bm.faces.new(verts)
        f.material_index = self.mat(mat)
        if uvs is None:
            a, b, c = (Vector(p) for p in pts[:3])
            n = (b - a).cross(c - a)
            n = [abs(n.x), abs(n.y), abs(n.z)]
            d = n.index(max(n))
            u_axis, v_axis = [(2, 1), (0, 2), (0, 1)][d]
            uvs = [(p[u_axis], p[v_axis]) for p in pts]
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        return f

    def prism(self, poly, y0, y1, mat, top="Roof", bottom=None, top_poly=None):
        """
        A vertical prism over the polygon poly [(x, z), ...] (counter-clockwise seen from above, i.e. from +Y), from y0 to
        y1. The sides get UV u = metres along the perimeter, v = metres up (the facade shader's window grid). top_poly
        tapers it (same vertex count). Caps: top/bottom materials (None = open).
        """
        upper = top_poly or poly
        n = len(poly)
        u = 0.0
        for i in range(n):
            (ax, az), (bx, bz) = poly[i], poly[(i + 1) % n]
            (cx, cz), (dx, dz) = upper[(i + 1) % n], upper[i]
            w = math.hypot(bx - ax, bz - az)
            # Outward faces: counter-clockwise from outside = a(bottom) -> b(bottom) -> b(top) -> a(top) when poly is
            # counter-clockwise seen from above... seen from outside the side, the order a -> b runs left to right.
            self.face([(ax, y0, az), (bx, y0, bz), (cx, y1, cz), (dx, y1, dz)], mat,
                      [(u, y0), (u + w, y0), (u + w, y1), (u, y1)])
            u += w
        if top:
            self.face([(x, y1, z) for x, z in upper], top)
        if bottom:
            self.face([(x, y0, z) for x, z in reversed(poly)], bottom)

    def box(self, lo, hi, mat):
        """An axis-aligned box from its glTF min and max corners (all six faces)."""
        poly = rect_xz(lo[0], hi[0], lo[2], hi[2])
        self.prism(poly, lo[1], hi[1], mat, top=mat, bottom=mat)

    def finish(self):
        mesh = bpy.data.meshes.new(self.name)
        self.bm.to_mesh(mesh)
        self.bm.free()
        obj = bpy.data.objects.new(self.name, mesh)
        for m in self.mats:
            mesh.materials.append(MATERIALS[m])
        bpy.context.scene.collection.objects.link(obj)
        return obj


def rect_xz(x0, x1, z0, z1):
    """A rectangle's corners, counter-clockwise seen from above (+Y): x0z1 -> x1z1 -> x1z0 -> x0z0."""
    return [(x0, z1), (x1, z1), (x1, z0), (x0, z0)]


def rect(w, d, cx=0.0, cz=0.0):
    return rect_xz(cx - w / 2, cx + w / 2, cz - d / 2, cz + d / 2)


def chamfered(w, d, c, cx=0.0, cz=0.0):
    """A rectangle with its corners cut by c (an octagon), counter-clockwise seen from above."""
    x0, x1, z0, z1 = cx - w / 2, cx + w / 2, cz - d / 2, cz + d / 2
    return [(x0 + c, z1), (x1 - c, z1), (x1, z1 - c), (x1, z0 + c), (x1 - c, z0), (x0 + c, z0), (x0, z0 + c), (x0, z1 - c)]


def octagon(across, cx=0.0, cz=0.0):
    side = across / (1 + math.sqrt(2))
    return chamfered(across, across, (across - side) / 2, cx, cz)


def circle(r, n, cx=0.0, cz=0.0):
    # counter-clockwise seen from above: angle increasing from +X towards -Z (x = cos, z = -sin)
    return [(cx + r * math.cos(2 * math.pi * i / n), cz - r * math.sin(2 * math.pi * i / n)) for i in range(n)]


def cross_poly(span, arm):
    s, a = span / 2, arm / 2
    pts = [(-a, s), (a, s), (a, a), (s, a), (s, -a), (a, -a), (a, -s), (-a, -s), (-a, -a), (-s, -a), (-s, a), (-a, a)]
    return pts


def grow(poly, d):
    """The polygon pushed out by d (for trims proud of the facade): each vertex moved along its corner's bisector."""
    n = len(poly)
    out = []
    for i in range(n):
        p = Vector((poly[i][0], poly[i][1]))
        a = Vector((poly[i - 1][0], poly[i - 1][1]))
        b = Vector((poly[(i + 1) % n][0], poly[(i + 1) % n][1]))
        e1 = (p - a).normalized()
        e2 = (b - p).normalized()
        # outward normals of the edges (counter-clockwise seen from above with z down: right-hand normal is (z, -x)... )
        n1 = Vector((-e1.y, e1.x))
        n2 = Vector((-e2.y, e2.x))
        bis = (n1 + n2)
        if bis.length < 1e-6:
            bis = n1
        bis.normalize()
        k = d / max(0.2, bis.dot(n1))
        q = p + bis * k
        out.append((q.x, q.y))
    return out


def trim(b, poly, y, h=0.8, out=0.25):
    b.prism(grow(poly, out), y - h, y, "Trim", top="Roof", bottom="Roof")


def clutter(b, rng, poly_bounds, y, count):
    """Rooftop clutter: AC units, a water tank, a lift housing, vents."""
    x0, x1, z0, z1 = poly_bounds
    for _ in range(count):
        w, d, h = rng.uniform(1.5, 4.5), rng.uniform(1.5, 4.5), rng.uniform(1.0, 3.0)
        cx = rng.uniform(x0 + w / 2 + 1, x1 - w / 2 - 1)
        cz = rng.uniform(z0 + d / 2 + 1, z1 - d / 2 - 1)
        if rng.random() < 0.25:
            b.prism(circle(min(w, d) / 2, 8, cx, cz), y, y + h + 2.0, "Roof", top="Roof")
        else:
            b.box((cx - w / 2, y, cz - d / 2), (cx + w / 2, y + h, cz + d / 2), "Roof")


def mast(b, x, z, y0, height, width=0.5):
    b.box((x - width / 2, y0, z - width / 2), (x + width / 2, y0 + height, z + width / 2), "Roof")
    b.box((x - 0.5, y0 + height, z - 0.5), (x + 0.5, y0 + height + 1.0, z + 0.5), "Beacon")


# ---------------------------------------------------------------------------------------------------------------------
# Towers (footprints and heights: CityLayout.Archetypes)


def tower_slab(rng):
    b = Builder("Tower_Slab")
    p = rect(30, 18)
    b.prism(p, 0, 110, "Facade")
    trim(b, p, 110, 1.0)
    trim(b, p, 4.5, 0.5)
    clutter(b, rng, (-15, 15, -9, 9), 110, 6)
    b.box((-4, 110, -3), (4, 115, 3), "Roof")
    mast(b, 9, 3, 110, 14)
    return b.finish()


def tower_setback(rng):
    b = Builder("Tower_Setback")
    for (w, y0, y1) in ((34, 0, 70), (26, 70, 115), (18, 115, 150)):
        p = rect(w, w)
        b.prism(p, y0, y1, "Facade")
        trim(b, p, y1, 0.9)
        clutter(b, rng, (-w / 2, w / 2, -w / 2, w / 2), y1, 3)
    b.prism(octagon(4), 150, 176, "Roof", top="Roof", top_poly=octagon(0.6))
    b.box((-0.5, 176, -0.5), (0.5, 177, 0.5), "Beacon")
    return b.finish()


def tower_twin(rng):
    b = Builder("Tower_Twin")
    podium = rect(40, 30)
    b.prism(podium, 0, 22, "Facade")
    trim(b, podium, 22, 0.8)
    for cx, h in ((-11, 105), (11, 85)):
        p = rect(14, 14, cx)
        b.prism(p, 22, h, "Facade")
        trim(b, p, h, 0.9)
        clutter(b, rng, (cx - 7, cx + 7, -7, 7), h, 2)
    mast(b, -11, 0, 105, 12)
    clutter(b, rng, (-20, 20, 8, 15), 22, 3)
    return b.finish()


def tower_needle(rng):
    b = Builder("Tower_Needle")
    p = chamfered(18, 18, 3)
    b.prism(p, 0, 190, "Facade")
    for y in (172, 181, 190):
        trim(b, p, y, 0.7, 0.3)
    b.prism(octagon(6), 190, 196, "Roof", top="Roof")
    mast(b, 0, 0, 196, 24, 0.8)
    return b.finish()


def tower_block(rng):
    b = Builder("Tower_Block")
    p = rect(44, 36)
    b.prism(p, 0, 42, "Facade")
    trim(b, p, 42, 1.0)
    trim(b, p, 5, 0.4)
    clutter(b, rng, (-22, 22, -18, 18), 42, 10)
    b.box((-21, 42, -17.5), (21, 43.2, -17.0), "Roof")  # a parapet along the back
    return b.finish()


def tower_cross(rng):
    b = Builder("Tower_Cross")
    p = cross_poly(30, 12)
    b.prism(p, 0, 85, "Facade")
    trim(b, p, 85, 0.9)
    trim(b, p, 44, 0.5)
    clutter(b, rng, (-6, 6, -6, 6), 85, 3)
    mast(b, 0, 0, 85, 10)
    return b.finish()


def tower_octagon(rng):
    b = Builder("Tower_Octagon")
    p = octagon(26)
    b.prism(p, 0, 120, "Facade")
    b.prism(grow(p, 0.6), 120, 124, "Trim", top=None, bottom="Roof")
    b.prism(p, 124, 130, "Facade")
    trim(b, p, 130, 0.6)
    b.prism(octagon(10), 130, 136, "Roof", top="Roof")
    mast(b, 0, 0, 136, 10)
    return b.finish()


def tower_arcology(rng):
    b = Builder("Tower_Arcology")
    podium = rect(50, 50)
    b.prism(podium, 0, 18, "Facade")
    trim(b, podium, 18, 0.8)
    body = chamfered(42, 42, 8)
    b.prism(body, 18, 95, "Facade")
    trim(b, body, 60, 0.6)
    trim(b, body, 95, 1.0)
    b.prism(chamfered(30, 30, 6), 95, 103, "Roof", top="Roof", top_poly=chamfered(16, 16, 3))
    clutter(b, rng, (-24, 24, 13, 24), 18, 4)
    mast(b, 4, -2, 103, 12)
    return b.finish()


# ---------------------------------------------------------------------------------------------------------------------
# Rail and vehicles


def rail_track():
    b = Builder("Rail_Track")
    b.box((-3.5, 0, -4), (3.5, DECK, 4), "Concrete")
    for x in (-3.5, 3.3):
        b.box((x, DECK, -4), (x + 0.2, DECK + 1.0, 4), "Concrete")
        b.box((x - 0.01, DECK + 1.0, -4), (x + 0.21, DECK + 1.08, 4), "Trim")  # a neon line along each parapet
    for x in (-1.6, -0.9, 0.9, 1.6):
        b.box((x - 0.06, DECK, -4), (x + 0.06, DECK + 0.18, 4), "Steel")
    # guide lamps between the tracks, every 2 m
    for z in (-3.0, -1.0, 1.0, 3.0):
        b.box((-0.15, DECK, z - 0.3), (0.15, DECK + 0.06, z + 0.3), "Trim")
    # emissive strips under the deck's edges: the track draws a line through the night
    for x in (-3.5, 3.4):
        b.box((x, 0.1, -4), (x + 0.1, 0.25, 4), "Trim")
    return b.finish()


def rail_pillar():
    b = Builder("Rail_Pillar")
    top = RAIL_HEIGHT - DECK
    b.prism(octagon(1.8), 0, top - 1.2, "Concrete", top=None)
    b.box((-3.2, top - 1.2, -1.0), (3.2, top, 1.0), "Concrete")
    return b.finish()


def train_car():
    b = Builder("Train_Car")
    L, W = 20.0, 3.0
    # body: a chamfered box on a skirt, a window band (emissive), a roof
    b.box((-W / 2, 0.4, -L / 2), (W / 2, 1.3, L / 2), "Body")
    b.prism(chamfered(W, L, 0.5), 1.3, 2.6, "Windows", top=None)
    b.prism(chamfered(W, L, 0.5), 2.6, 3.3, "Body", top="Body", top_poly=chamfered(W - 0.6, L - 0.6, 0.3))
    b.box((-W / 2 + 0.2, 0.0, -L / 2 + 1.5), (W / 2 - 0.2, 0.4, -L / 2 + 4.0), "Steel")
    b.box((-W / 2 + 0.2, 0.0, L / 2 - 4.0), (W / 2 - 0.2, 0.4, L / 2 - 1.5), "Steel")
    b.box((-1.0, 0.8, -L / 2 - 0.05), (1.0, 1.1, -L / 2), "Lamp")
    return b.finish()


def car_ground():
    b = Builder("Car_Ground")
    L, W = 4.6, 1.8
    b.box((-W / 2, 0.25, -L / 2), (W / 2, 0.85, L / 2), "Body")
    b.prism(chamfered(W - 0.1, 2.4, 0.3, 0, 0.3), 0.85, 1.35, "Glass", top="Body")
    for x in (-0.65, 0.65):
        b.box((x - 0.18, 0.55, -L / 2 - 0.02), (x + 0.18, 0.7, -L / 2 + 0.01), "Lamp_Head")
        b.box((x - 0.2, 0.6, L / 2 - 0.01), (x + 0.2, 0.72, L / 2 + 0.02), "Lamp_Tail")
    for x in (-W / 2, W / 2 - 0.25):
        for z in (-1.4, 1.4):
            b.box((x, 0.0, z - 0.33), (x + 0.25, 0.5, z + 0.33), "Steel")
    # The lights' pools on the wet road (drawn additively by beam.gdshader): UV u across, v from the car (0) outwards (1).
    b.face([(-1.1, 0.03, -L / 2 - 0.2), (1.1, 0.03, -L / 2 - 0.2), (1.6, 0.03, -L / 2 - 9.0), (-1.6, 0.03, -L / 2 - 9.0)][::-1],
           "Beam_Head", [(0, 1), (1, 1), (1, 0), (0, 0)][::-1])
    b.face([(-1.0, 0.03, L / 2 + 0.1), (-1.2, 0.03, L / 2 + 2.6), (1.2, 0.03, L / 2 + 2.6), (1.0, 0.03, L / 2 + 0.1)],
           "Beam_Tail", [(0, 1), (0, 0), (1, 0), (1, 1)])
    return b.finish()


def car_hover():
    b = Builder("Car_Hover")
    L, W = 5.6, 2.2
    b.prism(chamfered(W, L, 0.6), 0.3, 1.0, "Body", top=None, bottom="Body", top_poly=chamfered(W - 0.2, L - 0.4, 0.5))
    b.prism(chamfered(W - 0.4, 2.8, 0.5, 0, 0.2), 1.0, 1.55, "Glass", top="Body", top_poly=chamfered(W - 0.8, 1.8, 0.3, 0, 0.4))
    for x in (-1.25, 1.25):
        for z in (-1.9, 1.9):
            b.prism(octagon(0.9, x, z), 0.15, 0.6, "Steel", top="Steel", bottom="Glow")
    b.box((-0.8, 0.55, -L / 2 - 0.03), (0.8, 0.7, -L / 2 + 0.3), "Lamp_Head")
    b.box((-0.9, 0.6, L / 2 - 0.3), (0.9, 0.75, L / 2 + 0.03), "Lamp_Tail")
    return b.finish()


def airship():
    """A 64 m blimp: a lofted hull, a gondola, four fins, a screen on each flank and running lights."""
    b = Builder("Airship")
    L, R = 64.0, 9.0
    rings, seg = 14, 12
    profile = []
    for i in range(rings + 1):
        t = i / rings
        z = -L / 2 + t * L
        r = max(R * math.sin(math.pi * t) ** 0.6, 0.8)
        profile.append((z, r))
    verts = []
    for z, r in profile:
        ring = []
        for k in range(seg):
            a = 2 * math.pi * k / seg
            ring.append(b.bm.verts.new(G(r * math.cos(a), R + r * math.sin(a), z)))
        verts.append(ring)
    mi = b.mat("Body")
    hull = []
    for i in range(rings):
        for k in range(seg):
            k2 = (k + 1) % seg
            f = b.bm.faces.new([verts[i][k], verts[i][k2], verts[i + 1][k2], verts[i + 1][k]])
            f.material_index = mi
            hull.append(f)
            for loop, uv in zip(f.loops, [(k / seg, i / rings), (k2 / seg, i / rings), (k2 / seg, (i + 1) / rings), (k / seg, (i + 1) / rings)]):
                loop[b.uv].uv = uv
    for ring in (verts[0], verts[-1]):
        f = b.bm.faces.new(ring)
        f.material_index = mi
        hull.append(f)
    bmesh.ops.recalc_face_normals(b.bm, faces=hull)
    # gondola under the hull, its windows lit
    b.box((-2.2, -0.2, -9), (2.2, 1.6, 9), "Body")
    b.box((-2.25, 0.3, -8), (2.25, 1.1, 8), "Windows")
    # fins
    for (x0, x1, y0, y1) in ((-0.3, 0.3, R + 5, R + 13), (-0.3, 0.3, R - 13, R - 5), (5, 13, R - 0.3, R + 0.3), (-13, -5, R - 0.3, R + 0.3)):
        b.box((x0, y0, L / 2 - 12), (x1, y1, L / 2 - 2), "Body")
    # screens on both flanks (UV 0..1 across the screen, facing out), slightly proud of the hull
    sw, sh = 30.0, 9.0
    for side in (-1, 1):
        x = side * (R + 0.4)
        pts = [(x, R - sh / 2, sw / 2), (x, R - sh / 2, -sw / 2), (x, R + sh / 2, -sw / 2), (x, R + sh / 2, sw / 2)]
        uvs = [(0, 0), (1, 0), (1, 1), (0, 1)]
        if side < 0:
            pts = [pts[1], pts[0], pts[3], pts[2]]
        b.face(pts, "Screen", uvs)
        # the frame behind the screen
        fx = side * (R - 0.6)
        b.box((min(x, fx) - 0.01, R - sh / 2 - 0.4, -sw / 2 - 0.4), (max(x, fx) - 0.02, R + sh / 2 + 0.4, sw / 2 + 0.4), "Steel")
    # running lights: red port, green starboard, white tail
    b.box((-R - 0.4, R - 0.4, -2), (-R + 0.2, R + 0.4, -1), "Lamp_Tail")
    b.box((R - 0.2, R - 0.4, -2), (R + 0.4, R + 0.4, -1), "Glow")
    b.box((-0.4, R - 0.4, L / 2 - 0.2), (0.4, R + 0.4, L / 2 + 0.6), "Lamp_Head")
    return b.finish()


# ---------------------------------------------------------------------------------------------------------------------
# Signs and holograms


def billboard():
    b = Builder("Billboard")
    # a unit screen facing +Z centred on the origin, a frame round it and a box behind it
    b.face([(-0.5, -0.5, 0.0), (0.5, -0.5, 0.0), (0.5, 0.5, 0.0), (-0.5, 0.5, 0.0)], "Screen",
           [(0, 0), (1, 0), (1, 1), (0, 1)])
    f = 0.03
    b.box((-0.5 - f, -0.5 - f, -0.4), (0.5 + f, -0.5, 0.04), "Frame")
    b.box((-0.5 - f, 0.5, -0.4), (0.5 + f, 0.5 + f, 0.04), "Frame")
    b.box((-0.5 - f, -0.5, -0.4), (-0.5, 0.5, 0.04), "Frame")
    b.box((0.5, -0.5, -0.4), (0.5 + f, 0.5, 0.04), "Frame")
    b.box((-0.5, -0.5, -0.4), (0.5, 0.5, -0.02), "Frame")
    return b.finish()


def sign_blade():
    """A unit-high blade: 1.6 m out from x = -0.8 to 0.8 (its bracket reaches -X to the wall), panels facing ±Z."""
    b = Builder("Sign_Blade")
    w, t = 1.6, 0.16
    b.box((-w / 2 - 0.05, -0.5, -t + 0.01), (w / 2 + 0.05, 0.5, t - 0.01), "Frame")
    b.face([(-w / 2, -0.48, t), (w / 2, -0.48, t), (w / 2, 0.48, t), (-w / 2, 0.48, t)], "Panel", [(0, 0), (1, 0), (1, 1), (0, 1)])
    b.face([(w / 2, -0.48, -t), (-w / 2, -0.48, -t), (-w / 2, 0.48, -t), (w / 2, 0.48, -t)], "Panel", [(0, 0), (1, 0), (1, 1), (0, 1)])
    for y in (-0.35, 0.35):
        b.box((-1.9, y - 0.02, -0.05), (-w / 2 - 0.05, y + 0.02, 0.05), "Frame")
    return b.finish()


def sign_board():
    b = Builder("Sign_Board")
    b.face([(-0.5, -0.5, 0.0), (0.5, -0.5, 0.0), (0.5, 0.5, 0.0), (-0.5, 0.5, 0.0)], "Panel", [(0, 0), (1, 0), (1, 1), (0, 1)])
    b.box((-0.52, -0.56, -0.3), (0.52, 0.56, -0.01), "Frame")
    return b.finish()


def holo_koi():
    """A koi 1 m long along Z (head at -Z): a lofted body, a split tail fin, pectoral and dorsal fins."""
    b = Builder("Holo_Koi")
    rings, seg = 12, 10
    verts = []
    for i in range(rings + 1):
        t = i / rings
        z = -0.5 + t * 0.78
        # a fish profile: round head, full body, thin peduncle
        r = 0.11 * (math.sin(math.pi * min(1.0, t * 1.15)) ** 0.7) + 0.012
        ring = []
        for k in range(seg):
            a = 2 * math.pi * k / seg
            ring.append(b.bm.verts.new(G(r * 0.8 * math.cos(a), r * math.sin(a), z)))
        verts.append(ring)
    mi = b.mat("Holo")
    for i in range(rings):
        for k in range(seg):
            k2 = (k + 1) % seg
            f = b.bm.faces.new([verts[i][k], verts[i][k2], verts[i + 1][k2], verts[i + 1][k]])
            f.material_index = mi
            for loop, uv in zip(f.loops, [(k / seg, i / rings), (k2 / seg, i / rings), (k2 / seg, (i + 1) / rings), (k / seg, (i + 1) / rings)]):
                loop[b.uv].uv = uv
    for ring in (verts[0], verts[-1]):
        f = b.bm.faces.new(ring)
        f.material_index = mi
    # tail fin (two lobes, flat in the vertical plane) and fins (flat, double-sided in the shader)
    b.face([(0, 0, 0.26), (0, 0.16, 0.5), (0, 0.04, 0.42), (0, 0, 0.46), (0, -0.04, 0.42), (0, -0.16, 0.5)], "Holo",
           [(0.5, 0.0), (0.0, 1.0), (0.4, 0.8), (0.5, 0.9), (0.6, 0.8), (1.0, 1.0)])
    b.face([(0, 0.1, -0.15), (0, 0.17, 0.0), (0, 0.13, 0.12), (0, 0.08, 0.1)], "Holo", [(0, 0), (0.3, 1), (0.8, 1), (1, 0)])
    for side in (-1, 1):
        b.face([(side * 0.07, -0.05, -0.28), (side * 0.2, -0.1, -0.18), (side * 0.16, -0.08, -0.12), (side * 0.07, -0.05, -0.2)], "Holo",
               [(0, 0), (1, 0.3), (1, 1), (0, 1)])
    return b.finish()


def holo_logo():
    """An emblem 1 m across: a ring and a three-bladed star inside it (turned about +Y by the shader)."""
    b = Builder("Holo_Logo")
    n = 32
    outer, inner, t = 0.5, 0.43, 0.04
    for i in range(n):
        a0, a1 = 2 * math.pi * i / n, 2 * math.pi * (i + 1) / n
        p = lambda r, a: (r * math.cos(a), r * math.sin(a))
        o0, o1, i0, i1 = p(outer, a0), p(outer, a1), p(inner, a0), p(inner, a1)
        for z, flip in ((t, False), (-t, True)):
            q = [(o0[0], o0[1], z), (o1[0], o1[1], z), (i1[0], i1[1], z), (i0[0], i0[1], z)]
            b.face(list(reversed(q)) if flip else q, "Holo", [(0, 0), (1, 0), (1, 1), (0, 1)])
        b.face([(o0[0], o0[1], -t), (o1[0], o1[1], -t), (o1[0], o1[1], t), (o0[0], o0[1], t)], "Holo", [(0, 0), (1, 0), (1, 1), (0, 1)])
    for k in range(3):
        a = 2 * math.pi * k / 3 + math.pi / 2
        tip = (0.38 * math.cos(a), 0.38 * math.sin(a))
        l = (0.09 * math.cos(a + 2.2), 0.09 * math.sin(a + 2.2))
        r = (0.09 * math.cos(a - 2.2), 0.09 * math.sin(a - 2.2))
        for z, flip in ((t, False), (-t, True)):
            q = [(0, 0, z), (r[0], r[1], z), (tip[0], tip[1], z), (l[0], l[1], z)]
            b.face(list(reversed(q)) if flip else q, "Holo", [(0.5, 0.5), (1, 0.5), (0.5, 0), (0, 0.5)])
    return b.finish()


# ---------------------------------------------------------------------------------------------------------------------
# The sign words atlas


def fetch_font():
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, "NotoSansJP-Bold.otf")
    if not os.path.exists(path):
        print("fetch", FONT_URL)
        subprocess.check_call(["curl", "-sS", "-L", "--fail", "-m", "600", "-o", path + ".part", FONT_URL])
        os.replace(path + ".part", path)
    return path


def render_atlas(path):
    """signs.png: the words in white on black, rendered orthographically with Cycles."""
    font = bpy.data.fonts.load(fetch_font())
    scene = bpy.data.scenes.new("Atlas")
    window_scene = bpy.context.window.scene if bpy.context.window else None
    mat = bpy.data.materials.new("AtlasText")
    bsdf = principled(mat)
    bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
    emission = bsdf.inputs.get("Emission Color") or bsdf.inputs["Emission"]
    emission.default_value = (1, 1, 1, 1)
    bsdf.inputs["Emission Strength"].default_value = 1.0

    def place(text, cx, cy, cw, ch, vertical):
        curve = bpy.data.curves.new("Word", "FONT")
        curve.font = font
        curve.body = "\n".join(text) if vertical else text
        curve.align_x = "CENTER"
        curve.align_y = "CENTER"
        curve.space_line = 0.9
        curve.resolution_u = 6
        obj = bpy.data.objects.new("Word", curve)
        obj.data.materials.append(mat)
        scene.collection.objects.link(obj)
        # Fit: measure at size 1, then scale into the cell with a margin.
        dg = bpy.context.evaluated_depsgraph_get()
        dg.update()
        bb = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
        w = max(v.x for v in bb) - min(v.x for v in bb)
        h = max(v.y for v in bb) - min(v.y for v in bb)
        s = min(cw * 0.84 / max(w, 1e-3), ch * 0.8 / max(h, 1e-3))
        mx = (max(v.x for v in bb) + min(v.x for v in bb)) / 2
        my = (max(v.y for v in bb) + min(v.y for v in bb)) / 2
        obj.scale = (s, s, s)
        obj.location = (cx - mx * s, cy - my * s, 0)

    W, H = 2048, 1024
    # Blender's y is up: the image's top row is y = H. Horizontal words: 4 x 2 cells of 512 x 256 in the top half.
    for i, word in enumerate(HORIZONTAL_WORDS):
        col, row = i % 4, i // 4
        place(word, col * 512 + 256, H - (row * 256 + 128), 512, 256, False)
    for i, word in enumerate(VERTICAL_WORDS):
        place(word, i * 256 + 128, H - (512 + 256), 256, 512, True)

    cam = bpy.data.cameras.new("AtlasCam")
    cam.type = "ORTHO"
    cam.ortho_scale = W
    co = bpy.data.objects.new("AtlasCam", cam)
    co.location = (W / 2, H / 2, 10)
    scene.collection.objects.link(co)
    scene.camera = co
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 16
    scene.cycles.use_denoising = False
    scene.render.resolution_x, scene.render.resolution_y = W, H
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("AtlasWorld")
    scene.world = world
    try:
        world.use_nodes = True
    except Exception:
        pass
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0, 0, 0, 1)
    scene.render.filepath = path
    if bpy.context.window:
        bpy.context.window.scene = scene
    bpy.ops.render.render(write_still=True, scene=scene.name)
    if window_scene and bpy.context.window:
        bpy.context.window.scene = window_scene
    print("ATLAS", path)


# ---------------------------------------------------------------------------------------------------------------------
# Export


def export(name, objs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    tmp = tempfile.mkdtemp(prefix="citykit-gltf-")
    path = os.path.join(tmp, name + ".gltf")
    kw = dict(filepath=path, export_format="GLTF_SEPARATE", use_selection=True, export_yup=True, export_apply=True,
              export_texcoords=True, export_normals=True, export_tangents=False, export_materials="EXPORT",
              export_cameras=False, export_lights=False, export_animations=False, export_skins=False,
              export_morph=False, export_extras=False)
    try:
        bpy.ops.export_scene.gltf(**kw, export_vertex_color="NONE")
    except TypeError:
        bpy.ops.export_scene.gltf(**kw)
    with open(path) as f:
        gltf = json.load(f)
    if gltf.get("images"):
        sys.exit("the city kit has no textures, but the exporter wrote images")
    gltf.get("asset", {})["generator"] = "Examples/Neon/Tools/city/build_city_kit.py (Blender glTF exporter)"
    os.makedirs(OUT, exist_ok=True)
    shutil.copyfile(os.path.join(tmp, name + ".bin"), os.path.join(OUT, name + ".bin"))
    with open(os.path.join(OUT, name + ".gltf"), "w", newline="\n") as f:
        json.dump(gltf, f, indent=1)
        f.write("\n")
    shutil.rmtree(tmp)
    return gltf


def write_metas():
    """A `.meta` sidecar (the AssetDatabase's UID) for every file in OUT without one; existing UIDs are kept."""
    prefixes = {".png": "tex", ".gltf": "mdl"}
    for name in sorted(os.listdir(OUT)):
        if name.endswith(".meta") or name.startswith("."):
            continue
        meta = os.path.join(OUT, name + ".meta")
        if os.path.exists(meta):
            continue
        prefix = prefixes.get(os.path.splitext(name)[1].lower(), "ast")
        with open(meta, "w", newline="\n") as f:
            f.write(json.dumps({"uid": f"{prefix}_{secrets.token_hex(6)}"}, indent=2) + "\n")


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons) if obj.type == "MESH" else 0


# ---------------------------------------------------------------------------------------------------------------------
# Previews (not part of the asset)


def setup_preview(res=(1600, 800)):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.compute_device_type = "METAL"
        prefs.get_devices()
        for d in prefs.devices:
            d.use = True
        sc.cycles.device = "GPU"
    except Exception:
        pass
    sc.cycles.samples = 32
    sc.cycles.use_denoising = True
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.image_settings.file_format = "PNG"
    world = bpy.data.worlds.new("PreviewWorld") if sc.world is None else sc.world
    sc.world = world
    try:
        world.use_nodes = True
    except Exception:
        pass
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.35, 0.3, 0.45, 1)
    bg.inputs["Strength"].default_value = 1.0


def render(path, eye, target, lens=35):
    sc = bpy.context.scene
    cam = bpy.data.cameras.new("PreviewCam")
    co = bpy.data.objects.new("PreviewCam", cam)
    sc.collection.objects.link(co)
    cam.lens = lens
    cam.clip_end = 5000
    e, t = G(*eye), G(*target)
    f = (t - e).normalized()
    right = f.cross(Vector((0, 0, 1))).normalized()
    up = right.cross(f).normalized()
    m = Matrix((right, up, -f)).transposed().to_4x4()
    m.translation = e
    co.matrix_world = m
    sc.camera = co
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(co, do_unlink=True)
    print("render", path)


def lineup(groups):
    """Moves the objects apart along +X (each group on its own row) for a preview."""
    z = 0.0
    for objs, gap in groups:
        x = 0.0
        for o in objs:
            dims = o.dimensions
            o.location = G(x + dims.x / 2, 0, z)
            x += dims.x + gap
        z += 80


def previews(out_dir, towers, rest):
    os.makedirs(out_dir, exist_ok=True)
    setup_preview()
    lineup([(towers, 12)])
    render(os.path.join(out_dir, "towers.png"), (210, 120, 360), (210, 80, 0), lens=30)
    for o in towers:
        o.hide_render = True
    lineup([(rest, 6)])
    render(os.path.join(out_dir, "kit.png"), (60, 40, 130), (60, 5, 0), lens=35)


def verify(out_dir):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for name in ("towers", "rail", "vehicles", "signs"):
        bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, name + ".gltf"))
    report = {o.name: tri_count(o) for o in bpy.data.objects if o.type == "MESH"}
    print("VERIFY objects", json.dumps(report, sort_keys=True))
    print("VERIFY materials", sorted(m.name for m in bpy.data.materials))
    setup_preview()
    towers = sorted((o for o in bpy.data.objects if o.name.startswith("Tower_")), key=lambda o: o.name)
    lineup([(towers, 12)])
    os.makedirs(out_dir, exist_ok=True)
    render(os.path.join(out_dir, "verify_towers.png"), (210, 120, 360), (210, 80, 0), lens=30)


# ---------------------------------------------------------------------------------------------------------------------


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview_dir = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    verify_dir = argv[argv.index("--verify") + 1] if "--verify" in argv else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    make_materials()
    rng = random.Random(2077)

    towers = [tower_slab(rng), tower_setback(rng), tower_twin(rng), tower_needle(rng), tower_block(rng),
              tower_cross(rng), tower_octagon(rng), tower_arcology(rng)]
    rail = [rail_track(), rail_pillar(), train_car()]
    vehicles = [car_ground(), car_hover(), airship()]
    signs = [billboard(), sign_blade(), sign_board(), holo_koi(), holo_logo()]

    for name, objs in (("towers", towers), ("rail", rail), ("vehicles", vehicles), ("signs", signs)):
        export(name, objs)
        print("KIT", name, {o.name: tri_count(o) for o in objs})
    if "--no-atlas" not in argv:
        render_atlas(os.path.join(OUT, "signs.png"))
    write_metas()

    if preview_dir:
        previews(preview_dir, towers, rail + vehicles + signs)
    if verify_dir:
        verify(verify_dir)


main()
