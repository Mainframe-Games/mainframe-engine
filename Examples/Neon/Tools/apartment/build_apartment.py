"""Builds Neon's loft apartment as a graybox: Examples/Neon/Assets/Art/Apartment/apartment.gltf + apartment.bin (glTF
separate, flat-coloured materials, no textures yet: the art comes in a later phase).

    just neon-art
    "$BLENDER" -b --factory-startup --python Examples/Neon/Tools/apartment/build_apartment.py [-- --preview DIR] [-- --verify DIR]

Everything is generated here (seeded, so every run writes the same file). Like the Forest's cabin script
(Examples/Forest/Tools/cabin/build_cabin.py), the game walks the exported nodes by name (World/NeonApartment.cs).

Conventions (glTF space): metres, +Y up, the window wall and the balcony face +Z (south), the front door is in the
north wall (-Z). The ground floor's top is y = 0, the mezzanine's y = MZ, the ceiling's underside y = H. The interior
spans x in [X0, X1] and z in [Z0, Z1]; the balcony z in [Z1, BZ].

Meshes (the name's prefix says what the game does with them):

- Door_Bath, Door_Computer, Door_Bed2: hinged doors. The origin is on the hinge axis at the floor; the leaf runs along
  local -X and opens towards local +Z with a POSITIVE turn about +Y (0 = shut).
- Door_Balcony: the glass slider. Origin at the leaf's -X edge on the floor; the leaf slides along local -X.
- Door_Front: the locked front door (origin on its hinge, like the others; it never opens).
- Furn_*: furniture, one box collider each (its bounds). Stairs: the steps (no collision: Collider_Stairs is the ramp
  the player walks on). Rug_*, LED_*, Plate_*, Screen_*, Decor_*: no collision.
- Floor, Tile_Bath, Balcony, Mezzanine: the walkable slabs; their collision is boxes (Collider_<mesh>_<n>), since a
  capsule sliding over triangles catches on their internal edges.
- Everything else is the shell: triangle collision. The glass is solid: the player cannot leave.
- Footstep surfaces follow the name: Tile_* tile, Mezzanine, Stairs and Railing_* metal, the rest concrete.
- Materials LED_<Room> are the light fixtures' glow: Switch_<Room> dims them with the room's lights.

Empties exported as mesh-less nodes:

- Light_<Room>_<n>: a light; the scale is its range (uniform). Switch_<Room>: a light switch on a wall.
- Collider_<name>[_<n>]: a box: location = centre, rotation, scale = half extents (the walkable slabs, the stairs'
  ramp, the balcony's invisible walls). The footstep surface is <name>'s.
- Zone_<Room>: location = the room's centre on its floor; scale = (half width x, height, half depth z).
- Spawn, Seat: where the player stands or sits; the node's -Z is the view (yaw only).
- Shower, Tap_<Room>, Tv, Stereo, Blinds_<Room>, Monitor_<n>: places for later phases' interactables.

--preview DIR renders previews (a floor plan per level, the living room, the mezzanine, the balcony) and --verify DIR
re-imports the exported apartment.gltf into an empty scene, lists its nodes and renders.
"""

import bmesh
import bpy
import json
import math
import os
import random
import shutil
import sys
import tempfile
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
NEON = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(NEON, "Assets", "Art", "Apartment")

# ---------------------------------------------------------------------------------------------------------------------
# Dimensions (glTF space)

X0, X1 = -7.0, 7.0             # interior, west to east (14 m)
Z0, Z1 = -5.5, 5.5             # interior, north (front door) to the window wall (11 m)
T = 0.2                        # outer wall thickness
H = 6.0                        # ceiling (double height)
MZ = 3.0                       # mezzanine floor top
SLAB = 0.25                    # mezzanine slab thickness
BZ = 8.0                       # balcony edge (2.5 m deep)
PX = -1.5                      # ground-floor partition's east face (bathroom, computer room); the mezzanine's edge
PT = 0.12                      # partition thickness
LX = -0.4                      # the landing's east edge (the stairs run between PX and LX)
GLASS_Z = (5.50, 5.54)         # the window wall's glass
SLIDER_X = (2.0, 3.4)          # the balcony slider's opening
SLIDER_H = 2.4
FRONT_X = (4.6, 5.6)           # front door opening
DOOR_W, DOOR_H = 0.9, 2.1      # inner doors
BATH_DOOR_Z = (-3.6, -2.7)
COMPUTER_DOOR_Z = (4.0, 4.9)
BED2_X = -3.0                  # bedroom 2's east wall (west face); its door opening
BED2_DOOR_Z = (-4.0, -3.1)
RISE, TREAD, STEPS = 0.2, 0.3, 14
STAIR_NOSE = 3.5               # the first step's nosing (z); the steps climb north
RAIL_H = 1.05

# The stairs' walking line through the nosings: y = (RAMP_FOOT - z) * RISE / TREAD.
RAMP_FOOT = STAIR_NOSE + TREAD * 1                      # where the line meets the floor
RAMP_TOP = RAMP_FOOT - MZ * TREAD / RISE                # where it meets the mezzanine (the landing's south edge)

COLORS = {
    # name: (base colour (linear), roughness)
    "Concrete": ((0.20, 0.20, 0.21), 0.85),
    "Wall": ((0.33, 0.33, 0.34), 0.9),
    "Ceiling": ((0.16, 0.16, 0.17), 0.9),
    "Steel": ((0.035, 0.037, 0.04), 0.45),
    "Grate": ((0.06, 0.06, 0.065), 0.6),
    "Wood": ((0.16, 0.08, 0.04), 0.6),
    "Door": ((0.05, 0.05, 0.06), 0.5),
    "Leather": ((0.12, 0.02, 0.025), 0.45),
    "Fabric": ((0.07, 0.07, 0.09), 0.9),
    "Tile": ((0.45, 0.47, 0.5), 0.3),
    "Rug": ((0.06, 0.03, 0.09), 0.95),
    "Screen": ((0.004, 0.004, 0.006), 0.15),
    "Porcelain": ((0.7, 0.7, 0.72), 0.2),
}
# The rooms' fixtures: emission colour (linear) and strength. Switch_<Room> dims LED_<Room>.
LEDS = {
    "Living": ((1.0, 0.62, 0.38), 6.0),
    "Bath": ((0.85, 0.92, 1.0), 6.0),
    "Computer": ((0.55, 0.18, 1.0), 8.0),
    "Loft": ((1.0, 0.22, 0.55), 6.0),
    "Bed2": ((0.15, 0.85, 1.0), 6.0),
    "Balcony": ((1.0, 0.45, 0.12), 6.0),
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


def flat_material(name, color, roughness):
    mat = bpy.data.materials.new(name)
    bsdf = principled(mat)
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0.0
    mat.diffuse_color = (*color, 1.0)
    return mat


def led_material(name, color, strength):
    mat = flat_material(name, (0.02, 0.02, 0.02), 0.4)
    bsdf = principled(mat)
    emission = bsdf.inputs.get("Emission Color") or bsdf.inputs["Emission"]
    emission.default_value = (*color, 1.0)
    bsdf.inputs["Emission Strength"].default_value = strength
    return mat


def glass_material():
    mat = bpy.data.materials.new("Glass")
    bsdf = principled(mat)
    bsdf.inputs["Base Color"].default_value = (0.30, 0.36, 0.40, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.05
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Alpha"].default_value = 0.2
    try:
        mat.surface_render_method = "BLENDED"
    except Exception:
        mat.blend_method = "BLEND"
    mat.use_backface_culling = False
    return mat


def make_materials():
    for name, (color, roughness) in COLORS.items():
        MATERIALS[name] = flat_material(name, color, roughness)
    for room, (color, strength) in LEDS.items():
        MATERIALS[f"LED_{room}"] = led_material(f"LED_{room}", color, strength)
    MATERIALS["Glass"] = glass_material()


# ---------------------------------------------------------------------------------------------------------------------
# Mesh building


class Builder:
    """One object: a BMesh with a UV layer and a material list. Points are glTF space, relative to the origin."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.mats = []

    def mat(self, name):
        if name not in self.mats:
            self.mats.append(name)
        return self.mats.index(name)

    def hexa(self, c, mat):
        """A convex six-sided solid from its 8 corners c[(i, j, k)] (glTF points; i along x, j along y, k along z)."""
        centre = sum((Vector(p) for p in c.values()), Vector()) / 8
        verts = {key: self.bm.verts.new(G(*p)) for key, p in c.items()}
        mi = self.mat(mat)
        for axis in range(3):
            for side in (0, 1):
                a, b = [i for i in range(3) if i != axis]
                keys = []
                for p, q in ((0, 0), (1, 0), (1, 1), (0, 1)):
                    key = [0, 0, 0]
                    key[axis], key[a], key[b] = side, p, q
                    keys.append(tuple(key))
                pts = [Vector(c[k]) for k in keys]
                normal = (pts[1] - pts[0]).cross(pts[2] - pts[0])
                if normal.length < 1e-9:
                    normal = (pts[2] - pts[0]).cross(pts[3] - pts[0])
                if normal.dot(sum(pts, Vector()) / 4 - centre) < 0:
                    keys.reverse()
                    pts.reverse()
                    normal = -normal
                f = self.bm.faces.new([verts[k] for k in keys])
                f.material_index = mi
                # UVs: metres in the face's plane (the two axes other than its normal's largest).
                n = [abs(normal.x), abs(normal.y), abs(normal.z)]
                d = n.index(max(n))
                u_axis, v_axis = [(2, 1), (0, 2), (0, 1)][d]
                for loop, p in zip(f.loops, pts):
                    loop[self.uv].uv = (p[u_axis], p[v_axis])

    def box(self, lo, hi, mat):
        """An axis-aligned box from its glTF min and max corners."""
        c = {}
        for i in (0, 1):
            for j in (0, 1):
                for k in (0, 1):
                    c[(i, j, k)] = ((lo[0], hi[0])[i], (lo[1], hi[1])[j], (lo[2], hi[2])[k])
        self.hexa(c, mat)

    def finish(self, location=(0, 0, 0), yaw=0.0):
        """The object, its origin at glTF <location> turned <yaw> degrees about +Y (points were given in that frame)."""
        mesh = bpy.data.meshes.new(self.name)
        self.bm.to_mesh(mesh)
        self.bm.free()
        obj = bpy.data.objects.new(self.name, mesh)
        for m in self.mats:
            mesh.materials.append(MATERIALS[m])
        obj.matrix_world = Matrix.Translation(G(*location)) @ Matrix.Rotation(math.radians(yaw), 4, "Z")
        bpy.context.scene.collection.objects.link(obj)
        return obj


def single_box(name, lo, hi, mat):
    b = Builder(name)
    b.box(lo, hi, mat)
    return b.finish()


def wall(b, lo, hi, axis, holes, mat):
    """An axis-aligned wall slab from lo to hi with rectangular holes along <axis> (0 = x, 2 = z): holes are
    (along0, along1, y0, y1). The pieces: full-height columns between holes, and the parts above and below each hole."""
    a0, a1 = lo[axis], hi[axis]
    cuts = sorted(holes)

    def piece(s0, s1, y0, y1):
        if s1 - s0 < 1e-6 or y1 - y0 < 1e-6:
            return
        p, q = list(lo), list(hi)
        p[axis], q[axis] = s0, s1
        p[1], q[1] = y0, y1
        b.box(p, q, mat)

    at = a0
    for h0, h1, y0, y1 in cuts:
        piece(at, h0, lo[1], hi[1])
        piece(h0, h1, lo[1], y0)
        piece(h0, h1, y1, hi[1])
        at = h1
    piece(at, a1, lo[1], hi[1])


def sloped(b, z0, z1, x0, x1, bottom, top, mat):
    """A solid between z0 and z1 (x0..x1) whose bottom and top follow lines: bottom(z), top(z) give y."""
    c = {}
    for i, x in enumerate((x0, x1)):
        for k, z in enumerate((z0, z1)):
            c[(i, 0, k)] = (x, bottom(z), z)
            c[(i, 1, k)] = (x, top(z), z)
    b.hexa(c, mat)


# The walkable slabs: mesh name -> boxes (min, max, material). Their collision is these boxes, not triangles: a capsule
# sliding over a triangle mesh catches on its internal edges. The bathroom's tiles, the main floor round them, the
# balcony; the mezzanine (the loft and bedroom 2 over the west rooms, and the landing at the top of the stairs).
WALKABLE = {
    "Floor": [((PX - PT, -0.3, Z0 - T), (X1 + T, 0.0, Z1), "Concrete"),
              ((X0 - T, -0.3, -1.56), (PX - PT, 0.0, Z1), "Concrete")],
    "Tile_Bath": [((X0 - T, -0.3, Z0 - T), (PX - PT, 0.0, -1.56), "Tile")],
    "Balcony": [((X0 - T, -0.3, Z1), (X1 + T, 0.0, BZ), "Concrete")],
    "Mezzanine": [((X0, MZ - SLAB, Z0), (PX, MZ, Z1), "Grate"),
                  ((PX, MZ - SLAB, Z0), (LX, MZ, RAMP_TOP), "Grate")],
}


def ramp_y(z):
    return (RAMP_FOOT - z) * RISE / TREAD


# ---------------------------------------------------------------------------------------------------------------------
# The apartment


def shell():
    objs = []
    # The floors the player walks on: slabs whose collision is boxes (Collider_<mesh>_<n>), flush at y = 0 and y = MZ.
    for name, boxes in WALKABLE.items():
        b = Builder(name)
        for lo, hi, mat in boxes:
            b.box(lo, hi, mat)
        objs.append(b.finish())
    # The ceiling, over the balcony too (the floor above's balcony): nobody climbs out.
    objs.append(single_box("Ceiling", (X0 - T, H, Z0 - T), (X1 + T, H + 0.3, BZ + 0.1), "Ceiling"))

    walls = Builder("Walls")
    wall(walls, (X0 - T, 0, Z0 - T), (X1 + T, H, Z0), 0, [(FRONT_X[0], FRONT_X[1], 0.0, 2.2)], "Wall")
    walls.box((X0 - T, 0, Z0), (X0, H, Z1), "Wall")
    walls.box((X1, 0, Z0), (X1 + T, H, Z1), "Wall")
    # The ground floor's partition (bathroom and computer room doors) and the wall between those rooms.
    wall(walls, (PX - PT, 0, Z0), (PX, MZ - SLAB, Z1), 2,
         [(BATH_DOOR_Z[0], BATH_DOOR_Z[1], 0.0, DOOR_H), (COMPUTER_DOOR_Z[0], COMPUTER_DOOR_Z[1], 0.0, DOOR_H)], "Wall")
    walls.box((X0, 0, -1.56), (PX - PT, MZ - SLAB, -1.44), "Wall")
    # Bedroom 2 upstairs: its east wall (door) and its south wall onto the loft.
    wall(walls, (BED2_X, MZ, Z0), (BED2_X + PT, H, -1.44), 2, [(BED2_DOOR_Z[0], BED2_DOOR_Z[1], MZ, MZ + DOOR_H)], "Wall")
    walls.box((X0, MZ, -1.56), (BED2_X, H, -1.44), "Wall")
    # Door frames (steel jambs) round the inner doors' openings.
    for z0, z1 in (BATH_DOOR_Z, COMPUTER_DOOR_Z):
        for z in (z0, z1):
            walls.box((PX - PT - 0.01, 0, z - 0.03), (PX + 0.01, DOOR_H, z + 0.03), "Steel")
        walls.box((PX - PT - 0.01, DOOR_H, z0 - 0.03), (PX + 0.01, DOOR_H + 0.05, z1 + 0.03), "Steel")
    objs.append(walls.finish())

    # The window wall: glass from floor to ceiling but the slider's opening, steel mullions and transoms.
    glass = Builder("Glass_South")
    glass.box((X0, 0, GLASS_Z[0]), (SLIDER_X[0], H, GLASS_Z[1]), "Glass")
    glass.box((SLIDER_X[1], 0, GLASS_Z[0]), (X1, H, GLASS_Z[1]), "Glass")
    glass.box((SLIDER_X[0], SLIDER_H, GLASS_Z[0]), (SLIDER_X[1], H, GLASS_Z[1]), "Glass")
    objs.append(glass.finish())
    frame = Builder("Frame_South")
    for x in (X0, -5.0, -3.0, PX, 0.5, SLIDER_X[0], SLIDER_X[1], 5.2, X1):
        frame.box((x - 0.04, 0, 5.48), (x + 0.04, H, 5.58), "Steel")
    frame.box((X0, MZ - SLAB, 5.48), (PX, MZ, 5.58), "Steel")
    frame.box((SLIDER_X[0], SLIDER_H, 5.48), (SLIDER_X[1], SLIDER_H + 0.06, 5.58), "Steel")
    frame.box((X0, H - 0.08, 5.48), (X1, H, 5.58), "Steel")
    objs.append(frame.finish())

    # Railings: glass panels with a steel top rail, solid (the loft's edge, the landing, the stairs, the balcony).
    rail = Builder("Railing_Mezzanine")
    rail.box((PX - 0.03, MZ, RAMP_TOP), (PX + 0.03, MZ + RAIL_H, Z1), "Glass")
    rail.box((PX - 0.04, MZ + RAIL_H, RAMP_TOP), (PX + 0.04, MZ + RAIL_H + 0.05, Z1), "Steel")
    rail.box((LX - 0.03, MZ, Z0), (LX + 0.03, MZ + RAIL_H, RAMP_TOP), "Glass")
    rail.box((LX - 0.04, MZ + RAIL_H, Z0), (LX + 0.04, MZ + RAIL_H + 0.05, RAMP_TOP), "Steel")
    sloped(rail, RAMP_TOP, RAMP_FOOT, LX - 0.03, LX + 0.03, ramp_y, lambda z: ramp_y(z) + RAIL_H, "Glass")
    sloped(rail, RAMP_TOP, RAMP_FOOT, LX - 0.04, LX + 0.04, lambda z: ramp_y(z) + RAIL_H,
           lambda z: ramp_y(z) + RAIL_H + 0.05, "Steel")
    objs.append(rail.finish())
    brail = Builder("Railing_Balcony")
    brail.box((X0 - T, 0, BZ - 0.08), (X1 + T, 1.1, BZ - 0.02), "Glass")
    brail.box((X0 - T, 1.1, BZ - 0.1), (X1 + T, 1.16, BZ), "Steel")
    for x0, x1 in ((X0 - T, X0 - T + 0.06), (X1 + T - 0.06, X1 + T)):
        brail.box((x0, 0, Z1 + 0.08), (x1, 1.1, BZ - 0.08), "Glass")
        brail.box((x0 - 0.01, 1.1, Z1 + 0.08), (x1 + 0.01, 1.16, BZ - 0.08), "Steel")
    objs.append(brail.finish())
    return objs


def stairs():
    """Steel stairs climbing north between the partition and the landing's edge; collision is Collider_Stairs."""
    b = Builder("Stairs")
    for i in range(1, STEPS + 1):
        z_front = STAIR_NOSE - TREAD * (i - 1)
        b.box((PX + 0.02, RISE * i - 0.05, z_front - TREAD), (LX - 0.02, RISE * i, z_front + 0.02), "Grate")
    for x0, x1 in ((PX, PX + 0.06), (LX - 0.06, LX)):
        sloped(b, RAMP_TOP, RAMP_FOOT, x0, x1, lambda z: ramp_y(z) - 0.3, lambda z: ramp_y(z) + 0.02, "Steel")
    return b.finish()


def door_leaf(name, hinge, yaw, width, height, mat="Door", thickness=0.04):
    """A hinged leaf along local -X from the hinge, a handle near its free edge on both faces."""
    b = Builder(name)
    b.box((-width, 0.0, -thickness / 2), (0.0, height, thickness / 2), mat)
    for s in (-1, 1):
        b.box((-width + 0.06, 1.0, s * thickness / 2), (-width + 0.18, 1.04, s * (thickness / 2 + 0.05)), "Steel")
    return b.finish(hinge, yaw)


def doors():
    gap = 0.01
    objs = [
        # Hinges at the openings' north/south ends, so each leaf opens into its room (-X, yaw -90).
        door_leaf("Door_Bath", (PX - PT / 2, 0, BATH_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Computer", (PX - PT / 2, 0, COMPUTER_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Bed2", (BED2_X + PT / 2, MZ, BED2_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Front", (FRONT_X[1], 0, Z0 - T / 2), 0, FRONT_X[1] - FRONT_X[0], 2.2, thickness=0.08),
    ]
    # The balcony slider: a glass leaf in a steel frame, inside the window wall's glass.
    w = SLIDER_X[1] - SLIDER_X[0]
    b = Builder("Door_Balcony")
    b.box((0.04, 0.04, -0.01), (w - 0.04, SLIDER_H - 0.04, 0.01), "Glass")
    for lo, hi in (((0, 0, -0.025), (0.04, SLIDER_H, 0.025)), ((w - 0.04, 0, -0.025), (w, SLIDER_H, 0.025)),
                   ((0, 0, -0.025), (w, 0.04, 0.025)), ((0, SLIDER_H - 0.04, -0.025), (w, SLIDER_H, 0.025))):
        b.box(lo, hi, "Steel")
    b.box((w - 0.16, 0.9, -0.07), (w - 0.12, 1.3, -0.025), "Steel")
    objs.append(b.finish((SLIDER_X[0], 0, GLASS_Z[0] - 0.05)))
    return objs


def furniture(rng):
    F = []

    def furn(name, lo, hi, mat):
        F.append(single_box(name, lo, hi, mat))

    # Kitchen along the north wall, the island, the fridge.
    furn("Furn_Counter", (-0.2, 0, Z0), (3.6, 0.92, Z0 + 0.62), "Wood")
    furn("Furn_UpperCabinets", (-0.2, 1.55, Z0), (3.6, 2.3, Z0 + 0.35), "Wood")
    furn("Furn_Fridge", (3.6, 0, Z0), (4.3, 2.1, Z0 + 0.7), "Steel")
    furn("Furn_Island", (0.6, 0, -3.5), (2.8, 0.92, -2.6), "Wood")
    # Living room: the sectional, the coffee table, the TV on the east wall, the full-height bookshelf.
    furn("Furn_SofaA", (2.4, 0, 0.4), (3.3, 0.8, 3.6), "Leather")
    furn("Furn_SofaB", (3.3, 0, 0.4), (5.0, 0.8, 1.3), "Leather")
    furn("Furn_CoffeeTable", (3.8, 0, 1.9), (5.0, 0.4, 3.0), "Glass")
    furn("Furn_TvStand", (6.5, 0, 1.0), (X1, 0.5, 3.0), "Wood")
    furn("Furn_Bookshelf", (6.6, 0, -3.2), (X1, 5.4, -0.2), "Wood")
    furn("Furn_Console", (6.5, 0, -5.3), (X1, 0.9, -4.3), "Wood")
    F.append(single_box("Rug_Living", (3.0, 0, 0.0), (6.2, 0.012, 4.0), "Rug"))
    F.append(single_box("Screen_Tv", (6.9, 0.9, 0.8), (6.96, 2.2, 3.2), "Screen"))
    # Bathroom: the walk-in shower's glass, the vanity and its mirror, the toilet.
    sh = Builder("Glass_Shower")
    sh.box((-5.44, 0, Z0), (-5.4, 2.2, -4.0), "Glass")
    sh.box((X0, 0, -4.04), (-6.1, 2.2, -4.0), "Glass")
    F.append(sh.finish())
    furn("Furn_Vanity", (-3.3, 0, Z0), (-2.1, 0.85, Z0 + 0.5), "Porcelain")
    furn("Furn_Toilet", (-4.4, 0, Z0), (-3.8, 0.45, Z0 + 0.7), "Porcelain")
    F.append(single_box("Decor_Mirror", (-3.3, 1.1, Z0), (-2.1, 2.0, Z0 + 0.02), "Glass"))
    # Computer room: the desk, two racks, six monitors in an arc, the chair.
    furn("Furn_Desk", (-6.9, 0, 0.3), (-5.9, 0.75, 3.7), "Steel")
    furn("Furn_Rack1", (-2.4, 0, -1.3), (-1.75, 2.0, -0.6), "Steel")
    furn("Furn_Rack2", (-3.3, 0, -1.3), (-2.65, 2.0, -0.6), "Steel")
    furn("Furn_Chair", (-5.5, 0, 1.7), (-4.9, 1.2, 2.3), "Fabric")
    # The monitors: three columns of two in an arc round the chair, each facing it (the screen's face is local +X).
    monitors = []
    for i in range(6):
        a = math.radians(35 * (i % 3 - 1))
        x, z = -5.2 - 1.1 * math.cos(a), 2.0 + 1.1 * math.sin(a)
        y = 1.0 + 0.4 * (i // 3)
        m = Builder(f"Screen_Monitor_{i + 1}")
        m.box((-0.03, -0.18, -0.3), (0.03, 0.18, 0.3), "Screen")
        F.append(m.finish((x, y, z), math.degrees(a)))
        # The socket's +Z faces the chair.
        monitors.append(((x + 0.04 * math.cos(a), y, z - 0.04 * math.sin(a)), math.degrees(a) + 90))
    # Loft and bedroom 2.
    furn("Furn_Bed", (-6.9, MZ, 1.4), (-4.9, MZ + 0.55, 3.6), "Fabric")
    furn("Furn_Wardrobe", (X0, MZ, -1.3), (-6.4, MZ + 2.2, 0.6), "Wood")
    furn("Furn_Bunk", (-6.9, MZ, Z0 + 0.1), (-5.9, MZ + 1.9, -3.4), "Steel")
    furn("Furn_Bed2Desk", (-5.5, MZ, -2.2), (-4.0, MZ + 0.75, -1.56), "Wood")
    F.append(single_box("Rug_Loft", (-6.2, MZ, -0.8), (-3.0, MZ + 0.012, 1.2), "Rug"))
    # Balcony: an AC unit.
    furn("Furn_AcUnit", (5.6, 0, 6.0), (6.6, 0.7, 6.7), "Steel")
    # A few books on the shelf (seeded heights), as one decor mesh.
    books = Builder("Decor_Books")
    for shelf in range(5):
        y = 0.4 + shelf * 1.0
        z = -3.1
        while z < -0.4:
            w = rng.uniform(0.03, 0.07)
            h = rng.uniform(0.2, 0.32)
            books.box((6.7, y, z), (6.95, y + h, z + w), rng.choice(["Leather", "Fabric", "Rug", "Wood"]))
            z += w + rng.uniform(0.0, 0.02)
    F.append(books.finish())
    return F, monitors


def fixtures():
    """The lights' glowing fixtures (LED_<Room>_<n>, material LED_<Room>) and the switches' plates."""
    objs = []
    for name, lo, hi in (
            ("LED_Living_1", (-0.5, H - 0.25, -2.5), (1.5, H - 0.2, -2.45)),
            ("LED_Living_2", (3.5, H - 0.25, 1.0), (5.5, H - 0.2, 1.05)),
            ("LED_Living_3", (0.2, 2.42, -5.2), (3.4, 2.45, -5.1)),
            ("LED_Living_4", (5.0, 2.95, -4.7), (5.8, 3.0, -4.5)),
            ("LED_Bath_1", (-5.0, MZ - SLAB - 0.03, -3.6), (-3.6, MZ - SLAB, -3.4)),
            ("LED_Computer_1", (-6.8, MZ - SLAB - 0.03, 0.0), (-1.8, MZ - SLAB, 0.04)),
            ("LED_Computer_2", (-6.8, MZ - SLAB - 0.03, 4.0), (-1.8, MZ - SLAB, 4.04)),
            ("LED_Loft_1", (-6.8, H - 0.03, 2.4), (-1.8, H, 2.44)),
            ("LED_Bed2_1", (-6.0, H - 0.03, -3.6), (-4.0, H, -3.4)),
            ("LED_Balcony_1", (-1.0, H - 0.03, 7.2), (1.0, H, 7.3)),
    ):
        room = name.split("_")[1]
        objs.append(single_box(name, lo, hi, f"LED_{room}"))
    return objs


SWITCHES = {
    # room: (position on the wall's face, the wall's normal axis: 0 = x, 2 = z, and its sign)
    "Living": ((5.85, 1.2, Z0), 2, 1),
    "Bath": ((PX, 1.2, -2.45), 0, 1),
    "Computer": ((PX, 1.2, 5.15), 0, 1),
    "Loft": ((BED2_X + PT, MZ + 1.2, -1.9), 0, 1),
    "Bed2": ((BED2_X, MZ + 1.2, -2.85), 0, -1),
    "Balcony": ((X1, 1.2, 5.0), 0, -1),
}

LIGHTS = {
    # name: (position, range)
    "Light_Living_1": ((0.5, 5.4, -1.0), 10.0),
    "Light_Living_2": ((4.5, 5.4, 2.0), 10.0),
    "Light_Living_3": ((1.7, 2.3, -4.2), 5.0),
    "Light_Living_4": ((5.4, 2.6, -4.6), 4.0),
    "Light_Bath_1": ((-4.3, 2.4, -3.5), 5.0),
    "Light_Computer_1": ((-4.3, 2.4, 2.0), 6.5),
    "Light_Loft_1": ((-4.3, 5.4, 2.0), 7.0),
    "Light_Bed2_1": ((-5.0, 5.4, -3.5), 5.0),
    "Light_Balcony_1": ((0.0, 5.5, 6.8), 9.0),
}

ZONES = {
    # room: (centre on the floor, (half x, height, half z))
    "Living": ((2.75, 0, 0.0), (4.25, H, 5.5)),
    "Kitchen": ((1.7, 0, -4.0), (2.2, MZ, 1.5)),
    "Entry": ((5.65, 0, -4.5), (1.35, MZ, 1.0)),
    "Bath": ((-4.31, 0, -3.53), (2.69, MZ - SLAB, 1.97)),
    "Computer": ((-4.31, 0, 2.03), (2.69, MZ - SLAB, 3.47)),
    "Loft": ((-4.25, MZ, 2.0), (2.75, H - MZ, 3.5)),
    "Bed2": ((-5.0, MZ, -3.5), (2.0, H - MZ, 2.0)),
    "Balcony": ((0.0, 0, (Z1 + BZ) / 2), (X1, 3.0, (BZ - Z1) / 2)),
}


def plates():
    objs = []
    for room, (p, axis, sign) in SWITCHES.items():
        half = [0.04, 0.06, 0.04]
        half[axis] = 0.008
        c = list(p)
        c[axis] += sign * half[axis]
        objs.append(single_box(f"Plate_{room}", [c[i] - half[i] for i in range(3)], [c[i] + half[i] for i in range(3)],
                               "Steel"))
    return objs


# ---------------------------------------------------------------------------------------------------------------------
# Empties


def empty(name, loc, size=0.25, scale=None, yaw=0.0, pitch=0.0):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = "PLAIN_AXES"
    e.empty_display_size = size
    bpy.context.scene.collection.objects.link(e)
    e.location = G(*loc)
    e.rotation_euler = (math.radians(pitch), 0.0, math.radians(yaw))
    if scale is not None:
        e.scale = (scale[0], scale[2], scale[1])  # glTF (x, y, z) -> Blender (x, z, y)
    return e


def empties(monitors):
    E = []
    for name, (p, rng) in LIGHTS.items():
        E.append(empty(name, p, size=0.2, scale=(rng, rng, rng)))
    for room, (p, axis, sign) in SWITCHES.items():
        c = list(p)
        c[axis] += sign * 0.008
        E.append(empty(f"Switch_{room}", c, size=0.1))
    for room, (centre, half) in ZONES.items():
        E.append(empty(f"Zone_{room}", centre, size=0.5, scale=half))
    # Colliders: the walkable slabs' boxes,
    for name, boxes in WALKABLE.items():
        for i, (lo, hi, _) in enumerate(boxes):
            centre = tuple((lo[k] + hi[k]) / 2 for k in range(3))
            E.append(empty(f"Collider_{name}_{i + 1}", centre, scale=tuple((hi[k] - lo[k]) / 2 for k in range(3))))
    # the stairs' ramp (its top face on the walking line) and the balcony's invisible walls (3 m).
    length = math.hypot(RAMP_FOOT - RAMP_TOP, MZ)
    angle = math.atan2(MZ, RAMP_FOOT - RAMP_TOP)
    thick = 0.1
    mid_z, mid_y = (RAMP_FOOT + RAMP_TOP) / 2, MZ / 2
    normal = Vector((0.0, math.cos(angle), math.sin(angle)))  # glTF (x, y, z): up and towards +z
    centre = Vector(((PX + LX) / 2, mid_y, mid_z)) - normal * (thick / 2)
    E.append(empty("Collider_Stairs", tuple(centre), scale=((LX - PX) / 2, thick / 2, length / 2),
                   pitch=math.degrees(angle)))
    E.append(empty("Collider_BalconySouth", (0.0, 1.5, BZ + 0.05), scale=(X1 + T + 0.1, 1.5, 0.05)))
    for x in (X0 - T - 0.05, X1 + T + 0.05):
        E.append(empty(f"Collider_Balcony{'West' if x < 0 else 'East'}", (x, 1.5, (Z1 + BZ) / 2),
                       scale=(0.05, 1.5, (BZ - Z1) / 2 + 0.1)))
    # Places for later phases.
    E.append(empty("Shower", (-6.25, 2.3, -4.75)))
    E.append(empty("Tap_Kitchen", (1.7, 0.95, Z0 + 0.2)))
    E.append(empty("Tap_Bath", (-2.7, 0.95, Z0 + 0.15)))
    E.append(empty("Tv", (6.93, 1.55, 2.0), yaw=90))
    E.append(empty("Stereo", (6.75, 0.55, 1.4)))
    E.append(empty("Blinds_Living", (4.3, H, 5.42)))
    E.append(empty("Blinds_Computer", (-4.3, MZ - SLAB, 5.42)))
    E.append(empty("Blinds_Loft", (-4.3, H, 5.42)))
    E.append(empty("Seat", (-5.2, 0.0, 2.0), yaw=90))
    for i, (p, yaw) in enumerate(monitors):
        E.append(empty(f"Monitor_{i + 1}", p, size=0.1, yaw=yaw))
    E.append(empty("Spawn", (5.1, 0.0, -4.0), yaw=180))
    return E


# ---------------------------------------------------------------------------------------------------------------------
# Export


def export(objs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    tmp = tempfile.mkdtemp(prefix="apartment-gltf-")
    path = os.path.join(tmp, "apartment.gltf")
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
        sys.exit("the graybox has no textures, but the exporter wrote images")
    gltf.get("asset", {})["generator"] = "Examples/Neon/Tools/apartment/build_apartment.py (Blender glTF exporter)"
    os.makedirs(OUT, exist_ok=True)
    shutil.copyfile(os.path.join(tmp, "apartment.bin"), os.path.join(OUT, "apartment.bin"))
    with open(os.path.join(OUT, "apartment.gltf"), "w", newline="\n") as f:
        json.dump(gltf, f, indent=1)
        f.write("\n")
    shutil.rmtree(tmp)
    return gltf


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons) if obj.type == "MESH" else 0


# ---------------------------------------------------------------------------------------------------------------------
# Previews (not part of the asset)


def look_matrix(eye_g, target_g):
    """Blender camera matrix looking from eye to target (glTF points)."""
    eye, tgt = G(*eye_g), G(*target_g)
    f = (tgt - eye).normalized()
    right = f.cross(Vector((0, 0, 1)))
    if right.length < 1e-6:
        right = Vector((1, 0, 0))
    right.normalize()
    up = right.cross(f).normalized()
    m = Matrix((right, up, -f)).transposed().to_4x4()
    m.translation = eye
    return m


def setup_render(res=(1280, 720), samples=48):
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
    sc.cycles.samples = samples
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
    bg.inputs["Color"].default_value = (0.05, 0.03, 0.09, 1)
    bg.inputs["Strength"].default_value = 1.0
    for name, (p, rng) in LIGHTS.items():
        light = bpy.data.lights.new("Preview" + name, "POINT")
        light.energy = rng * 25
        light.shadow_soft_size = 0.3
        lo = bpy.data.objects.new("Preview" + name, light)
        lo.location = G(*p)
        sc.collection.objects.link(lo)


def render(path, eye, target, lens=20, ortho=None, exposure=0.0, hide=()):
    sc = bpy.context.scene
    cam = bpy.data.cameras.new("PreviewCam")
    co = bpy.data.objects.new("PreviewCam", cam)
    sc.collection.objects.link(co)
    if ortho:
        cam.type = "ORTHO"
        cam.ortho_scale = ortho
    cam.lens = lens
    cam.clip_start = 0.05
    co.matrix_world = look_matrix(eye, target)
    sc.camera = co
    sc.view_settings.exposure = exposure
    sc.render.filepath = path
    hidden = [o for o in sc.objects if any(o.name.startswith(h) for h in hide)]
    for o in hidden:
        o.hide_render = True
    bpy.ops.render.render(write_still=True)
    for o in hidden:
        o.hide_render = False
    bpy.data.objects.remove(co, do_unlink=True)
    print("render", path)


def previews(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    setup_render()
    render(os.path.join(out_dir, "living.png"), (5.5, 1.6, -4.2), (0.0, 2.2, 3.0), lens=16, exposure=1.0)
    render(os.path.join(out_dir, "from_loft.png"), (-2.2, MZ + 1.6, 4.5), (4.0, 1.0, -2.0), lens=16, exposure=1.0)
    render(os.path.join(out_dir, "stairs.png"), (2.5, 1.6, 4.8), (-1.0, 1.6, -0.5), lens=18, exposure=1.0)
    render(os.path.join(out_dir, "balcony.png"), (-5.5, 1.6, 6.0), (4.0, 0.8, 7.5), lens=18, exposure=1.0)
    render(os.path.join(out_dir, "computer.png"), (-2.3, 1.6, 4.4), (-6.5, 1.0, 1.5), lens=18, exposure=1.0)
    sc = bpy.context.scene
    sc.render.resolution_x, sc.render.resolution_y = 1200, 1100
    render(os.path.join(out_dir, "plan_ground.png"), (0.0, 20.0, 1.25), (0.0, 0.0, 1.2499), ortho=16.5,
           hide=("Ceiling", "Mezzanine", "Railing_Mezz", "Furn_Bed", "Furn_Wardrobe", "Furn_Bunk", "Rug_Loft",
                 "LED_Loft", "LED_Bed2", "Door_Bed2", "Walls", "LED_Living_1", "LED_Living_2", "LED_Balcony"))
    render(os.path.join(out_dir, "plan_upper.png"), (0.0, 20.0, 1.25), (0.0, 0.0, 1.2499), ortho=16.5,
           hide=("Ceiling", "LED_Loft", "LED_Bed2", "LED_Living_1", "LED_Living_2", "LED_Balcony"))
    sc.render.resolution_x, sc.render.resolution_y = 1280, 720


def verify(out_dir):
    """Re-imports the exported apartment into an empty scene and renders it."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, "apartment.gltf"))
    report = {o.name: o.type for o in bpy.data.objects}
    print("VERIFY objects", json.dumps(report, sort_keys=True))
    print("VERIFY materials", sorted(m.name for m in bpy.data.materials))
    setup_render()
    os.makedirs(out_dir, exist_ok=True)
    render(os.path.join(out_dir, "verify_living.png"), (5.5, 1.6, -4.2), (0.0, 2.2, 3.0), lens=16, exposure=1.0)


# ---------------------------------------------------------------------------------------------------------------------


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview_dir = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    verify_dir = argv[argv.index("--verify") + 1] if "--verify" in argv else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    make_materials()
    rng = random.Random(2077)

    meshes = shell() + [stairs()] + doors()
    furn, monitors = furniture(rng)
    meshes += furn + fixtures() + plates()
    sockets = empties(monitors)

    gltf = export(meshes + sockets)
    print("APARTMENT triangles", sum(tri_count(o) for o in meshes), "objects", len(meshes), "empties", len(sockets))
    print("APARTMENT nodes", [n["name"] for n in gltf["nodes"] if "mesh" not in n])

    if preview_dir:
        previews(preview_dir)
    if verify_dir:
        verify(verify_dir)


main()
