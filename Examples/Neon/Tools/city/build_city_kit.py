"""Builds Neon's city kit: Examples/Neon/Assets/Art/City/{towers,props,rail,vehicles,signs}.gltf (+ .bin, glTF separate) and
the neon sign words atlas signs.png.

    just neon-city-art
    "$BLENDER" -b --factory-startup --python Examples/Neon/Tools/city/build_city_kit.py [-- --preview DIR] [-- --verify DIR]

Everything is generated here (seeded, so every run writes the same meshes). The game builds the city from these meshes
(World/NeonCity.cs) where World/CityLayout.cs places them; the facade, screen, sign and hologram shaders
(Assets/Shaders/*.slang) draw their surfaces, so the materials here only name the surfaces and give a flat colour.

Conventions (glTF space): metres, +Y up, every object's origin is its base centre (towers, pillars) or its centre
(vehicles, signs), and it faces +Z (vehicles drive towards -Z, the engine's forward). One mesh object per kit piece,
named as the game looks it up:

- towers.gltf: Tower_Slab, Tower_Setback, Tower_Twin, Tower_Needle, Tower_Block, Tower_Cross, Tower_Octagon,
  Tower_Arcology, each twice: the plain name is the far mesh, <name>_Near adds what only shows close (piers on the wall
  every BAY metres, risers, air conditioners). Footprints and roof heights are CityLayout.Archetypes' (change both
  together). Towers are stretched up per instance, so nothing that must keep its shape goes on a tower: roof clutter is in
  props.gltf. Materials: Facade (the walls; UV = metres along the face from a centred whole number of BAYs, metres up:
  the shader's window columns divide BAY, so they land between the piers), Roof (roofs, ledges, structure), Trim (the
  emissive accent lines: the facade shader colours them per tower), Beacon (aviation lights), Lamp (work lights). Ledges
  stand at whole multiples of LEDGE, which the facade shader divides into whole storeys (its ledge_every).
- props.gltf: placed unstretched by CityLayout's props, base centred on the origin, front along +Z. Roofs: Prop_Hvac,
  Prop_Tank, Prop_Dish, Prop_Antenna, Prop_Bulkhead, Prop_Cooling, Prop_Pipes, Prop_Solar, Prop_LightMast, Prop_Helipad,
  Prop_Lattice, Prop_Crane. Streets: Street_Lamp (its arm reaches +Z, over the road), Street_Kiosk, Street_Awning (2 m deep
  from the wall along +Z, 1 m wide along X: stretched along X), Street_Signal, Street_Lantern (a paper lantern, its Trim
  coloured by the instance), Street_Walker (a figure under an umbrella, walking +Z: crowd.slang).
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
  instanceCustom.x.

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
BAY = 6.0               # metres between a wall's piers (facade.slang's bay)
LEDGE = 17.5            # kit metres between ledges (facade.slang's ledge_every)

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
    "Canopy": ((0.2, 0.2, 0.22), 0.5, 0.0),
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
            if mat == "Facade":
                u = -0.5 * (w % BAY)  # a whole number of bays, centred on the face
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

    def oface(self, pts, out, mat):
        """A polygon whose winding is chosen so that it faces along `out`."""
        a, b, c = (Vector(p) for p in pts[:3])
        if (b - a).cross(c - a).dot(Vector(out)) < 0:
            pts = pts[::-1]
        self.face(pts, mat)

    def obox(self, o, ax, ay, az, mat):
        """A box at centre o with half axes ax, ay, az (three perpendicular vectors, any orientation)."""
        o, ax, ay, az = Vector(o), Vector(ax), Vector(ay), Vector(az)
        for (u, v, w) in ((ax, ay, az), (ay, az, ax), (az, ax, ay)):
            for sign in (-1, 1):
                c = o + w * sign
                self.oface([tuple(c - u - v), tuple(c + u - v), tuple(c + u + v), tuple(c - u + v)], tuple(w * sign), mat)

    def beam(self, p0, p1, r, mat):
        """A square bar of half width r from p0 to p1."""
        p0, p1 = Vector(p0), Vector(p1)
        axis = (p1 - p0) * 0.5
        side = axis.cross(Vector((0, 1, 0)))
        if side.length < 1e-4:
            side = axis.cross(Vector((1, 0, 0)))
        side = side.normalized() * r
        self.obox((p0 + p1) * 0.5, axis, side, axis.cross(side).normalized() * r, mat)

    def cylinder(self, x, z, y0, y1, r, mat, n=8, top=None, top_r=None):
        """An upright cylinder (or cone, with top_r) whose cap is `top` (default: the same material)."""
        self.prism(circle(r, n, x, z), y0, y1, mat, top=top or mat, top_poly=circle(top_r, n, x, z) if top_r is not None else None)

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


def edges(poly):
    """Each wall of a polygon: its start (x, z), length, direction and outward normal (2D vectors)."""
    n = len(poly)
    for i in range(n):
        a, b = Vector(poly[i]), Vector(poly[(i + 1) % n])
        length = (b - a).length
        t = (b - a) / length
        yield a, length, t, Vector((-t.y, t.x))


def on_wall(b, a, t, n, u, y, d, half_u, half_y, half_d, mat):
    """A box on a wall: u metres along it, y up, d metres out from its surface."""
    b.obox((a.x + t.x * u + n.x * d, y, a.y + t.y * u + n.y * d), (t.x * half_u, 0, t.y * half_u), (0, half_y, 0),
           (n.x * half_d, 0, n.y * half_d), mat)


def cornice(b, poly, y, out=0.25, light=True):
    """Where a wall ends: a dark cornice with a thin neon line under it."""
    b.prism(grow(poly, out + 0.1), y, y + 0.4, "Roof", top="Roof", bottom="Roof")
    if light:
        b.prism(grow(poly, out), y - 0.22, y, "Trim", top=None, bottom="Roof")


def parapet(b, poly, y, h=1.0, t=0.35):
    """The wall round a roof: outside, inside and its coping."""
    outer, inner = grow(poly, 0.05), grow(poly, -t)
    n = len(poly)
    b.prism(outer, y, y + h, "Roof", top=None)
    for i in range(n):
        j = (i + 1) % n
        (ax, az), (bx, bz) = inner[i], inner[j]
        b.oface([(ax, y, az), (bx, y, bz), (bx, y + h, bz), (ax, y + h, az)], (-(bz - az), 0, bx - ax), "Roof")
        (cx, cz), (dx, dz) = outer[i], outer[j]
        b.oface([(ax, y + h, az), (bx, y + h, bz), (dx, y + h, dz), (cx, y + h, cz)], (0, 1, 0), "Roof")


def ledges(b, poly, y0, y1, every, out=0.35):
    """A slab edge every `every` multiples of LEDGE between y0 and y1 (where the shader's floor lines fall)."""
    k = math.ceil((y0 + 0.5) / (LEDGE * every))
    while k * LEDGE * every < y1 - 1.0:
        y = k * LEDGE * every
        b.prism(grow(poly, out), y - 0.32, y, "Roof", top="Roof", bottom="Roof")
        k += 1


def corners(b, poly, y0, y1, w=0.55):
    """A corner column at every vertex: the facade's window grid never meets at a bare edge."""
    for i, (x, z) in enumerate(poly):
        p = Vector((x, z))
        a, c = Vector(poly[i - 1]), Vector(poly[(i + 1) % len(poly)])
        e1, e2 = (p - a).normalized(), (c - p).normalized()
        if e1.dot(e2) > 0.97:
            continue
        b.prism(rect(w * 2, w * 2, x, z), y0, y1, "Roof", top="Roof")


def piers(b, poly, y0, y1, out=0.42, half=0.16, every=1):
    """Piers proud of the wall every BAY, centred on the face as the facade's UV is (near only)."""
    for a, length, t, n in edges(poly):
        if length < BAY:
            continue
        count = int(length // BAY)
        start = (length - count * BAY) * 0.5
        for i in range(0, count + 1, every):
            on_wall(b, a, t, n, start + i * BAY, (y0 + y1) * 0.5, out * 0.5, half, (y1 - y0) * 0.5, out * 0.5, "Roof")


def risers(b, rng, poly, y0, y1, count):
    """Service pipes up the walls (near only)."""
    walls = list(edges(poly))
    for _ in range(count):
        a, length, t, n = rng.choice(walls)
        u = rng.uniform(1.0, max(1.5, length - 1.0))
        r = rng.choice((0.12, 0.18, 0.26))
        on_wall(b, a, t, n, u, (y0 + y1) * 0.5, r + 0.05, r, (y1 - y0) * 0.5, r, "Roof")
        y = y0 + 6.0
        while y < y1:
            on_wall(b, a, t, n, u, y, r + 0.05, r + 0.08, 0.1, r + 0.08, "Roof")
            y += 12.0


def units(b, rng, poly, y0, y1, count):
    """Air conditioners and service boxes on the walls (near only), on the storey lines."""
    walls = list(edges(poly))
    for _ in range(count):
        a, length, t, n = rng.choice(walls)
        y = y0 + rng.randrange(1, max(2, int((y1 - y0) / STOREY))) * STOREY
        w, h, d = rng.uniform(0.35, 0.6), rng.uniform(0.25, 0.4), rng.uniform(0.25, 0.45)
        on_wall(b, a, t, n, rng.uniform(0.8, max(1.0, length - 0.8)), y, d, w, h, d, "Roof")


def core(b, a, t, n, u, half, out, y0, y1):
    """A service core on a wall, running past the roof, with a lit slit up it."""
    on_wall(b, a, t, n, u, (y0 + y1) * 0.5, out * 0.5, half, (y1 - y0) * 0.5, out * 0.5, "Roof")
    on_wall(b, a, t, n, u, (y0 + y1) * 0.5, out + 0.03, 0.14, (y1 - y0) * 0.46, 0.04, "Lamp")


def storeys(b, poly, y0, y1, near, rng, trim=True, ledge_every=1):
    """A block of the tower: its wall, slab edges, corners, cornice and, near, its piers, pipes and boxes."""
    b.prism(poly, y0, y1, "Facade", top=None)
    ledges(b, poly, y0, y1, ledge_every if near else ledge_every * 2)
    corners(b, poly, y0, y1)
    cornice(b, poly, y1, light=trim)
    if near:
        piers(b, poly, y0, y1)
        risers(b, rng, poly, y0, y1, 3)
        units(b, rng, poly, y0, y1, int((y1 - y0) * 0.9))


def roof_slab(b, poly, y):
    b.prism(grow(poly, -0.1), y, y + 0.02, "Roof", top="Roof")
    parapet(b, poly, y)


def tower_slab(near, rng):
    b = Builder("Tower_Slab_Near" if near else "Tower_Slab")
    p = rect(30, 18)
    storeys(b, p, 0, 105, near, rng)
    # A plant floor set back on the roofline.
    top = rect(24, 13)
    b.prism(top, 105, 110, "Facade", top=None)
    cornice(b, top, 110, light=False)
    roof_slab(b, top, 110)
    roof_slab(b, p, 105)
    walls = list(edges(p))
    core(b, walls[0][0], walls[0][2], walls[0][3], 4.0, 2.2, 0.55, 0, 114)
    b.box((-4, 110, -3), (4, 115, 3), "Roof")
    mast(b, 9, 3, 110, 14)
    return b.finish()


def tower_setback(near, rng):
    b = Builder("Tower_Setback_Near" if near else "Tower_Setback")
    for (w, y0, y1) in ((34, 0, 70), (26, 70, 115.5), (18, 115.5, 150)):
        p = chamfered(w, w, 2.0)
        storeys(b, p, y0, y1, near, rng)
        roof_slab(b, p, y1)
    b.prism(octagon(5), 150, 158, "Roof", top="Roof", top_poly=octagon(3.5))
    b.prism(octagon(3), 158, 176, "Roof", top="Roof", top_poly=octagon(0.6))
    b.box((-0.5, 176, -0.5), (0.5, 177, 0.5), "Beacon")
    return b.finish()


def tower_twin(near, rng):
    b = Builder("Tower_Twin_Near" if near else "Tower_Twin")
    podium = rect(40, 30)
    storeys(b, podium, 0, 21, near, rng)
    roof_slab(b, podium, 21)
    for cx, h in ((-11, 105), (11, 87.5)):
        p = chamfered(14, 14, 1.2, cx)
        storeys(b, p, 21, h, near, rng)
        roof_slab(b, p, h)
    # A sky bridge between the two, its glass lit.
    for y in (70.0,):
        b.box((-4.2, y - 3.5, -2.2), (4.2, y, 2.2), "Roof")
        b.box((-4.2, y - 2.9, -2.26), (4.2, y - 0.8, 2.26), "Lamp")
    mast(b, -11, 0, 105, 12)
    return b.finish()


def tower_needle(near, rng):
    b = Builder("Tower_Needle_Near" if near else "Tower_Needle")
    p = chamfered(18, 18, 3)
    storeys(b, p, 0, 190, near, rng, ledge_every=2)
    for y in (122.5, 175.0):
        b.prism(grow(p, 0.5), y - 0.5, y + 0.5, "Roof", top="Roof", bottom="Roof")
        b.prism(grow(p, 0.4), y - 0.72, y - 0.5, "Trim", top=None, bottom="Roof")
    parapet(b, p, 190)
    b.prism(octagon(8), 190, 196, "Roof", top="Roof", top_poly=octagon(6))
    b.prism(octagon(3.2), 196, 206, "Roof", top="Roof", top_poly=octagon(1.4))
    mast(b, 0, 0, 206, 14, 0.8)
    return b.finish()


def tower_block(near, rng):
    b = Builder("Tower_Block_Near" if near else "Tower_Block")
    p = rect(44, 36)
    storeys(b, p, 0, 42, near, rng)
    roof_slab(b, p, 42)
    return b.finish()


def tower_cross(near, rng):
    b = Builder("Tower_Cross_Near" if near else "Tower_Cross")
    p = cross_poly(30, 12)
    storeys(b, p, 0, 70, near, rng)
    roof_slab(b, p, 70)
    # The arms stop short of the core, which carries on.
    q = cross_poly(22, 12)
    storeys(b, q, 70, 85, near, rng)
    roof_slab(b, q, 85)
    mast(b, 0, 0, 85, 10)
    return b.finish()


def tower_octagon(near, rng):
    b = Builder("Tower_Octagon_Near" if near else "Tower_Octagon")
    p = octagon(26)
    storeys(b, p, 0, 120, near, rng, trim=False)
    b.prism(grow(p, 0.5), 120, 121, "Trim", top=None, bottom="Roof")
    b.prism(grow(p, 0.3), 121, 124, "Roof", top=None)
    b.prism(p, 124, 130, "Facade", top=None)
    cornice(b, p, 130)
    parapet(b, p, 130)
    b.prism(octagon(10), 130, 136, "Roof", top="Roof", top_poly=octagon(7))
    mast(b, 0, 0, 136, 10)
    return b.finish()


def tower_arcology(near, rng):
    b = Builder("Tower_Arcology_Near" if near else "Tower_Arcology")
    podium = rect(50, 50)
    storeys(b, podium, 0, 17.5, near, rng)
    roof_slab(b, podium, 17.5)
    # Terraces: the block steps in as it rises, a planted edge round each.
    steps = ((42, 8, 17.5, 52.5), (38, 7, 52.5, 77), (32, 6, 77, 95))
    for w, c, y0, y1 in steps:
        body = chamfered(w, w, c)
        storeys(b, body, y0, y1, near, rng)
        parapet(b, body, y1)
    b.prism(chamfered(30, 30, 6), 95, 103, "Roof", top="Roof", top_poly=chamfered(16, 16, 3))
    mast(b, 4, -2, 103, 12)
    return b.finish()


TOWERS = (tower_slab, tower_setback, tower_twin, tower_needle, tower_block, tower_cross, tower_octagon, tower_arcology)


def mast(b, x, z, y0, height, width=0.5):
    b.prism(rect(width * 1.6, width * 1.6, x, z), y0, y0 + height * 0.35, "Roof", top="Roof")
    b.prism(rect(width, width, x, z), y0 + height * 0.35, y0 + height, "Roof", top="Roof", top_poly=rect(width * 0.4, width * 0.4, x, z))
    b.box((x - 0.4, y0 + height, z - 0.4), (x + 0.4, y0 + height + 0.8, z + 0.4), "Beacon")


# ---------------------------------------------------------------------------------------------------------------------
# Props: roof clutter and street furniture, unstretched


def prop_hvac():
    """A rooftop air handler, 5 x 2.4 m: a louvred casing and two fans in cowls."""
    b = Builder("Prop_Hvac")
    b.box((-2.5, 0.25, -1.2), (2.5, 1.7, 1.2), "Roof")
    for x in (-2.3, 2.1):
        b.box((x, 0, -1.1), (x + 0.2, 0.25, 1.1), "Roof")
    for y in (0.5, 0.8, 1.1, 1.4):
        b.box((-2.45, y, 1.2), (2.45, y + 0.12, 1.26), "Roof")
    for x in (-1.2, 1.2):
        b.cylinder(x, 0, 1.7, 2.15, 0.95, "Roof", n=12, top="Roof")
        b.beam((x - 0.8, 2.18, 0), (x + 0.8, 2.18, 0), 0.04, "Roof")
        b.beam((x, 2.18, -0.8), (x, 2.18, 0.8), 0.04, "Roof")
    return b.finish()


def prop_tank():
    """A water tank on legs, 2 m across."""
    b = Builder("Prop_Tank")
    for x, z in ((-1.1, -1.1), (1.1, -1.1), (1.1, 1.1), (-1.1, 1.1)):
        b.beam((x, 0, z), (x * 0.7, 2.5, z * 0.7), 0.07, "Roof")
    b.beam((-1, 1.2, -1), (1, 1.2, -1), 0.04, "Roof")
    b.beam((-1, 1.2, 1), (1, 1.2, 1), 0.04, "Roof")
    b.cylinder(0, 0, 2.5, 5.0, 1.4, "Roof", n=12)
    b.prism(circle(1.4, 12), 5.0, 5.7, "Roof", top="Roof", top_poly=circle(0.15, 12))
    return b.finish()


def prop_dish():
    """A satellite dish on a post, tilted to the south."""
    b = Builder("Prop_Dish")
    b.cylinder(0, 0, 0, 1.6, 0.14, "Roof", n=6)
    tilt = Vector((0, 0.7, 0.71)).normalized()
    side = Vector((1, 0, 0))
    up = tilt.cross(side).normalized()
    centre = Vector((0, 1.7, 0))
    n = 12
    rim = [centre + tilt * 0.45 + (side * math.cos(i * math.tau / n) + up * math.sin(i * math.tau / n)) * 1.1 for i in range(n)]
    for i in range(n):
        b.oface([tuple(centre), tuple(rim[i]), tuple(rim[(i + 1) % n])], tuple(tilt), "Roof")
        b.oface([tuple(centre), tuple(rim[i]), tuple(rim[(i + 1) % n])], tuple(-tilt), "Roof")
    b.beam(tuple(centre), tuple(centre + tilt * 1.2), 0.03, "Roof")
    return b.finish()


def prop_antenna():
    """A guyed lattice mast, 26 m tall, with arrays on it and a beacon."""
    b = Builder("Prop_Antenna")
    h = 26.0
    w = 0.5
    legs = [(-w, -w), (w, -w), (w, w), (-w, w)]
    for x, z in legs:
        b.beam((x, 0, z), (x * 0.3, h, z * 0.3), 0.05, "Roof")
    for k in range(9):
        y0, y1 = k * h / 9, (k + 1) * h / 9
        s0, s1 = 1 - 0.7 * y0 / h, 1 - 0.7 * y1 / h
        for i in range(4):
            (ax, az), (cx, cz) = legs[i], legs[(i + 1) % 4]
            b.beam((ax * s0, y0, az * s0), (cx * s1, y1, cz * s1), 0.018, "Roof")
    for y, l in ((14.0, 1.6), (18.0, 1.2), (22.0, 0.9)):
        b.beam((-l, y, 0), (l, y, 0), 0.03, "Roof")
        for x in (-l, l):
            b.beam((x, y - 0.8, 0), (x, y + 0.8, 0), 0.025, "Roof")
    for i in range(3):
        a = i * math.tau / 3 + 0.5
        b.beam((0, 16, 0), (math.cos(a) * 7, 0, math.sin(a) * 7), 0.012, "Roof")
    b.box((-0.2, h, -0.2), (0.2, h + 0.4, 0.2), "Beacon")
    return b.finish()


def prop_bulkhead():
    """A stair and lift housing, 4 x 3.5 m and 3.5 m tall, a lamp over its door."""
    b = Builder("Prop_Bulkhead")
    b.box((-2, 0, -1.75), (2, 3.5, 1.75), "Roof")
    b.box((-2.1, 3.5, -1.85), (2.1, 3.7, 1.85), "Roof")
    b.box((-0.5, 0, 1.75), (0.5, 2.2, 1.78), "Roof")
    b.box((-0.25, 2.4, 1.75), (0.25, 2.5, 1.9), "Lamp")
    b.box((1.0, 3.7, -1.0), (1.8, 4.3, 0.4), "Roof")
    return b.finish()


def prop_cooling():
    """A cooling tower, 5 m square: slatted sides, a fan stack on top."""
    b = Builder("Prop_Cooling")
    for x, z in ((-2.3, -2.3), (2.3, -2.3), (2.3, 2.3), (-2.3, 2.3)):
        b.box((x - 0.12, 0, z - 0.12), (x + 0.12, 3.2, z + 0.12), "Roof")
    for k in range(6):
        y = 0.4 + k * 0.45
        b.box((-2.3, y, -2.3), (2.3, y + 0.2, 2.3), "Roof")
    b.box((-2.5, 3.2, -2.5), (2.5, 3.5, 2.5), "Roof")
    b.prism(circle(2.1, 16), 3.5, 4.8, "Roof", top="Roof", top_poly=circle(1.7, 16))
    return b.finish()


def prop_pipes():
    """A run of pipes on sleepers, 12 m long along X."""
    b = Builder("Prop_Pipes")
    for x in (-5.5, -2, 2, 5.5):
        b.box((x - 0.1, 0, -0.9), (x + 0.1, 0.5, 0.9), "Roof")
    for z, r in ((-0.55, 0.14), (-0.1, 0.22), (0.5, 0.18)):
        b.beam((-6, 0.5 + r, z), (6, 0.5 + r, z), r, "Roof")
    b.beam((6, 0.72, -0.1), (6, 2.2, -0.1), 0.22, "Roof")
    return b.finish()


def prop_solar():
    """Rows of tilted panels, 8 x 6 m."""
    b = Builder("Prop_Solar")
    for k in range(4):
        z = -2.6 + k * 1.75
        a, c = (-4, 0.25, z + 0.6), (4, 0.25, z + 0.6)
        d, e = (4, 1.0, z - 0.6), (-4, 1.0, z - 0.6)
        b.oface([a, c, d, e], (0, 0.8, 0.6), "Roof")
        b.oface([a, c, d, e], (0, -0.8, -0.6), "Roof")
        for x in (-3.8, 0, 3.8):
            b.beam((x, 0, z + 0.6), (x, 0.25, z + 0.6), 0.04, "Roof")
            b.beam((x, 0, z - 0.6), (x, 1.0, z - 0.6), 0.04, "Roof")
    return b.finish()


def prop_lightmast():
    """A work light on a pole."""
    b = Builder("Prop_LightMast")
    b.cylinder(0, 0, 0, 4.5, 0.07, "Roof", n=6)
    b.beam((-0.5, 4.5, 0), (0.5, 4.5, 0), 0.05, "Roof")
    for x in (-0.45, 0.45):
        b.box((x - 0.14, 4.55, -0.14), (x + 0.14, 4.75, 0.14), "Lamp")
    return b.finish()


def prop_helipad():
    """A landing deck 14 m across on stilts, ringed with lamps."""
    b = Builder("Prop_Helipad")
    r = 7.0
    n = 16
    for i in range(0, n, 2):
        a = i * math.tau / n
        b.beam((math.cos(a) * r * 0.9, 1.3, math.sin(a) * r * 0.9), (math.cos(a) * r * 0.45, 0, math.sin(a) * r * 0.45), 0.09, "Roof")
    b.prism(circle(r, n), 1.3, 1.6, "Roof", top="Roof", bottom="Roof")
    for i in range(n):
        a = (i + 0.5) * math.tau / n
        x, z = math.cos(a) * (r - 0.2), math.sin(a) * (r - 0.2)
        b.box((x - 0.14, 1.6, z - 0.14), (x + 0.14, 1.7, z + 0.14), "Lamp")
    for x0, z0, x1, z1 in ((-2.3, -3, -1.5, 3), (1.5, -3, 2.3, 3), (-1.5, -0.4, 1.5, 0.4)):
        b.box((x0, 1.6, z0), (x1, 1.62, z1), "Trim")
    return b.finish()


def prop_lattice():
    """An open steel crown, 12 m square and 14 m tall, stiffened by X braces: stretched to a roof's width."""
    b = Builder("Prop_Lattice")
    s, h, levels = 6.0, 14.0, 4
    pts = [(-s, -s), (s, -s), (s, s), (-s, s)]
    for k in range(levels):
        y0, y1 = k * h / levels, (k + 1) * h / levels
        for i in range(4):
            (ax, az), (cx, cz) = pts[i], pts[(i + 1) % 4]
            b.beam((ax, y0, az), (ax, y1, az), 0.14, "Roof")
            b.beam((ax, y1, az), (cx, y1, cz), 0.1, "Roof")
            b.beam((ax, y0, az), (cx, y1, cz), 0.05, "Roof")
            b.beam((cx, y0, cz), (ax, y1, az), 0.05, "Roof")
    for x, z in pts:
        b.box((x - 0.2, h, z - 0.2), (x + 0.2, h + 0.3, z + 0.2), "Beacon")
    b.prism(rect(12.4, 12.4), h - 0.1, h, "Trim", top=None, bottom=None)
    return b.finish()


def prop_crane():
    """A tower crane, 34 m tall with a 40 m jib along +X: the city is never finished."""
    b = Builder("Prop_Crane")
    w, h = 0.8, 30.0
    legs = [(-w, -w), (w, -w), (w, w), (-w, w)]
    for x, z in legs:
        b.beam((x, 0, z), (x, h, z), 0.08, "Roof")
    for k in range(12):
        y0, y1 = k * h / 12, (k + 1) * h / 12
        for i in range(4):
            (ax, az), (cx, cz) = legs[i], legs[(i + 1) % 4]
            b.beam((ax, y0, az), (cx, y1, cz), 0.03, "Roof")
    b.box((-1.3, h, -1.3), (1.3, h + 1.8, 1.3), "Roof")
    b.box((1.3, h + 0.6, -0.55), (1.34, h + 1.5, 0.55), "Lamp")
    top = h + 6.0
    b.beam((0, h + 1.8, 0), (0, top, 0), 0.12, "Roof")
    b.beam((0, h + 2.2, 0.5), (34, h + 2.2, 0.5), 0.09, "Roof")
    b.beam((0, h + 2.2, -0.5), (34, h + 2.2, -0.5), 0.09, "Roof")
    b.beam((0, h + 3.2, 0), (34, h + 2.3, 0), 0.08, "Roof")
    for k in range(12):
        x = k * 34 / 12
        b.beam((x, h + 2.2, 0.5), (x + 34 / 24, h + 3.15, 0), 0.03, "Roof")
        b.beam((x, h + 2.2, -0.5), (x + 34 / 24, h + 3.15, 0), 0.03, "Roof")
    b.beam((-10, h + 2.2, 0), (0, h + 2.2, 0), 0.25, "Roof")
    b.box((-10.5, h + 0.8, -0.8), (-8, h + 2.2, 0.8), "Roof")
    b.beam((0, top, 0), (26, h + 3.0, 0), 0.03, "Roof")
    b.beam((0, top, 0), (-9, h + 2.4, 0), 0.03, "Roof")
    b.beam((22, h + 2.1, 0), (22, h - 12, 0), 0.02, "Roof")
    b.box((21.6, h - 12.8, -0.4), (22.4, h - 12, 0.4), "Roof")
    b.box((-0.2, top, -0.2), (0.2, top + 0.4, 0.2), "Beacon")
    b.box((33.8, h + 2.4, -0.15), (34.1, h + 2.7, 0.15), "Beacon")
    return b.finish()


def street_lamp():
    """A street lamp, 8.5 m tall: its arm reaches +Z over the road."""
    b = Builder("Street_Lamp")
    b.cylinder(0, 0, 0, 0.6, 0.16, "Roof", n=6)
    b.cylinder(0, 0, 0.6, 8.2, 0.08, "Roof", n=6, top_r=0.05)
    b.beam((0, 8.2, 0), (0, 8.5, 2.2), 0.04, "Roof")
    b.box((-0.12, 8.32, 1.5), (0.12, 8.44, 2.4), "Roof")
    b.box((-0.1, 8.29, 1.55), (0.1, 8.32, 2.35), "Lamp")
    return b.finish()


def street_signal():
    """A traffic signal on a mast arm reaching +Z."""
    b = Builder("Street_Signal")
    b.cylinder(0, 0, 0, 6.2, 0.1, "Roof", n=6)
    b.beam((0, 6.0, 0), (0, 6.0, 5.0), 0.05, "Roof")
    for z in (2.6, 4.8):
        b.box((-0.16, 5.3, z - 0.16), (0.16, 6.0, z + 0.16), "Roof")
        b.box((-0.17, 5.4, z - 0.09), (-0.16, 5.55, z + 0.09), "Beacon")
    return b.finish()


def street_kiosk():
    """A food stall, 3.2 x 2.2 m: a lit counter under a striped awning, its sign on the roof."""
    b = Builder("Street_Kiosk")
    b.box((-1.6, 0, -1.1), (1.6, 2.4, 1.1), "Roof")
    b.box((-1.5, 0.95, 1.1), (1.5, 2.1, 1.12), "Lamp")
    b.box((-1.65, 0.85, 1.1), (1.65, 0.95, 1.5), "Roof")
    for k in range(7):
        x = -1.75 + k * 0.5
        a, c, d, e = (x, 2.6, 1.0), (x + 0.5, 2.6, 1.0), (x + 0.5, 2.15, 2.1), (x, 2.15, 2.1)
        b.oface([a, c, d, e], (0, 0.9, 0.4), "Roof")
        b.oface([a, c, d, e], (0, -0.9, -0.4), "Roof")
    b.box((-1.72, 2.1, 2.06), (1.72, 2.16, 2.12), "Trim")
    b.box((-1.4, 2.4, -0.2), (1.4, 2.95, -0.05), "Lamp")
    b.box((-1.5, 2.4, -1.1), (1.5, 2.55, 1.0), "Roof")
    return b.finish()


def street_awning():
    """An awning over a shop: 1 m of width along X (stretched), 2 m deep along +Z, lit from under its edge."""
    b = Builder("Street_Awning")
    b.oface([(-0.5, 0.45, 0), (0.5, 0.45, 0), (0.5, 0, 2), (-0.5, 0, 2)], (0, 1, 0.2), "Roof")
    b.oface([(-0.5, 0.45, 0), (0.5, 0.45, 0), (0.5, 0, 2), (-0.5, 0, 2)], (0, -1, -0.2), "Roof")
    b.box((-0.5, -0.32, 1.97), (0.5, 0, 2.03), "Trim")
    b.box((-0.5, -0.04, 0.8), (0.5, 0.2, 0.86), "Lamp")
    return b.finish()


def street_lantern():
    """A paper lantern hanging from y = 0, its body coloured by the instance."""
    b = Builder("Street_Lantern")
    b.prism(circle(0.22, 6), -0.5, -0.1, "Trim", top="Trim", bottom="Trim")
    return b.finish()


def street_walker():
    """Someone walking +Z under an umbrella, feet at y = 0: the body in Body, the umbrella in Canopy (crowd.slang)."""
    b = Builder("Street_Walker")
    b.prism(circle(0.17, 6), 0, 1.35, "Body", top="Body", top_poly=circle(0.2, 6))
    b.prism(circle(0.12, 6), 1.35, 1.65, "Body", top="Body", top_poly=circle(0.09, 6))
    n = 8
    rim = [(0.5 * math.cos(i * math.tau / n), 1.86, 0.5 * math.sin(i * math.tau / n)) for i in range(n)]
    for i in range(n):
        b.oface([(0, 2.1, 0), rim[i], rim[(i + 1) % n]], (0, 1, 0), "Canopy")
    b.beam((0, 1.3, 0.1), (0, 2.08, 0), 0.015, "Body")
    return b.finish()


PROPS = (prop_hvac, prop_tank, prop_dish, prop_antenna, prop_bulkhead, prop_cooling, prop_pipes, prop_solar, prop_lightmast,
         prop_helipad, prop_lattice, prop_crane, street_lamp, street_signal, street_kiosk, street_awning, street_lantern,
         street_walker)

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
    # The lights' pools on the wet road (drawn additively by beam.slang): UV u across, v from the car (0) outwards (1).
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
    """A `.mfmeta` sidecar (the AssetDatabase's UID) for every file in OUT without one; existing UIDs are kept."""
    prefixes = {".png": "tex", ".gltf": "mdl"}
    for name in sorted(os.listdir(OUT)):
        if name.endswith(".mfmeta") or name.startswith("."):
            continue
        meta = os.path.join(OUT, name + ".mfmeta")
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
    for name in ("towers", "props", "rail", "vehicles", "signs"):
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

    towers = [make(near, rng) for make in TOWERS for near in (False, True)]
    props = [make() for make in PROPS]
    rail = [rail_track(), rail_pillar(), train_car()]
    vehicles = [car_ground(), car_hover(), airship()]
    signs = [billboard(), sign_blade(), sign_board(), holo_koi(), holo_logo()]

    for name, objs in (("towers", towers), ("props", props), ("rail", rail), ("vehicles", vehicles), ("signs", signs)):
        export(name, objs)
        print("KIT", name, {o.name: tri_count(o) for o in objs})
    if "--no-atlas" not in argv:
        render_atlas(os.path.join(OUT, "signs.png"))
    write_metas()

    if preview_dir:
        previews(preview_dir, towers, props + rail + vehicles + signs)
    if verify_dir:
        verify(verify_dir)


main()
