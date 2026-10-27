"""Builds Neon's loft apartment: Examples/Neon/Assets/Art/Apartment/apartment.gltf + apartment.bin, textured from the CC0
sets in Assets/Art/Apartment/Textures (Tools/fetch_assets.py) and the posters (Tools/apartment/make_posters.py).

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
  the player walks on). Rug_*, LED_*, Plate_*, Screen_*, Decor_*, Glow_*: no collision.
- Floor, Tile_Bath, Balcony, Mezzanine, Planks_Bed2: the walkable slabs; their collision is boxes (Collider_<mesh>_<n>),
  since a capsule sliding over triangles catches on their internal edges.
- Everything else is the shell: triangle collision. The glass is solid: the player cannot leave.
- Footstep surfaces follow the name: Tile_* tile, Planks_* wood, Mezzanine, Stairs and Railing_* metal, the rest
  concrete.
- Materials LED_<Room> are the light fixtures' glow: Switch_<Room> dims them with the room's lights. Glow_* materials
  (signs, the sofa's strip, the racks, the PC) always glow. Screen is the monitors' and the TV's glass (the game gives
  each screen its own copy); Display_Stereo the stereo's display.

Empties exported as mesh-less nodes:

- Light_<Room>_<n>: a light; the scale is its range (uniform). Switch_<Room>: a light switch on a wall.
- Collider_<name>[_<n>]: a box: location = centre, rotation, scale = half extents (the walkable slabs, the stairs'
  ramp, the balcony's invisible walls). The footstep surface is <name>'s.
- Zone_<Room>: location = the room's centre on its floor; scale = (half width x, height, half depth z).
- Spawn, Seat: where the player stands or sits; the node's -Z is the view (yaw only).
- Shower (the head), Valve_Shower (the mixer, on the wall outside the glass), Tap_<Room> (the spout's tip), Tv (the
  screen's centre, -Z into the room), Stereo, Monitor_<n> (+Z faces the chair).
- Blinds_<Room>_<n>: the top centre of a run of blinds; scale = (half width, drop, 1). Panel_Blinds_<Room>: the wall
  panel that opens and closes the room's blinds.

--preview DIR renders previews (the living room, the loft, the computer room, the bathroom, the balcony, plans) and
--verify DIR re-imports the exported apartment.gltf into an empty scene, lists its nodes and renders.
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
TEX = os.path.join(OUT, "Textures")

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
WALL_Z = -1.56                 # the wall between the bathroom and the computer room (and bedroom 2 and the loft)
RISE, TREAD, STEPS = 0.2, 0.3, 14
STAIR_NOSE = 3.5               # the first step's nosing (z); the steps climb north
RAIL_H = 1.05

# The stairs' walking line through the nosings: y = (RAMP_FOOT - z) * RISE / TREAD.
RAMP_FOOT = STAIR_NOSE + TREAD * 1                      # where the line meets the floor
RAMP_TOP = RAMP_FOOT - MZ * TREAD / RISE                # where it meets the mezzanine (the landing's south edge)

# Textured materials: name -> (ambientCG set, metres per tile, tint (linear, multiplied) or None, double-sided).
TEXTURED = {
    "Concrete": ("Concrete042A", 2.5, (0.62, 0.62, 0.66), False),
    "Wall": ("Concrete034", 2.5, (0.36, 0.35, 0.38), False),
    "Ceiling": ("Concrete034", 3.0, (0.16, 0.16, 0.18), False),
    "Accent": ("CorrugatedSteel005", 1.2, (0.09, 0.07, 0.12), False),
    "Grate": ("MetalWalkway010", 0.8, (0.30, 0.30, 0.33), False),
    "Steel": ("Metal009", 1.0, (0.20, 0.20, 0.22), False),
    "Wood": ("Wood026", 1.0, (0.40, 0.33, 0.30), False),
    "Planks": ("WoodFloor041", 2.0, (0.75, 0.70, 0.68), False),
    "Tile": ("Tiles140", 1.2, (1.0, 1.0, 1.04), False),
    "Leather": ("Leather037", 0.6, (0.13, 0.07, 0.08), False),
    "Fabric": ("Fabric028", 0.6, (0.30, 0.28, 0.34), False),
    "Rug": ("Carpet016", 1.5, (0.30, 0.15, 0.40), False),
    "Rubber": ("Rubber004", 0.6, (0.6, 0.6, 0.62), False),
    "RedPaint": ("PaintedMetal004", 0.6, (0.8, 0.7, 0.7), False),
}
# Flat materials: name -> (base colour (linear), roughness, metallic).
FLAT = {
    "Door": ((0.035, 0.035, 0.04), 0.45, 0.0),
    "Black": ((0.012, 0.012, 0.014), 0.4, 0.0),
    "Chrome": ((0.6, 0.6, 0.62), 0.12, 1.0),
    "Screen": ((0.004, 0.004, 0.006), 0.12, 0.0),
    "Mirror": ((0.03, 0.035, 0.04), 0.03, 1.0),
    "Porcelain": ((0.75, 0.75, 0.77), 0.18, 0.0),
    "Linen": ((0.55, 0.53, 0.56), 0.85, 0.0),
    "Blanket": ((0.16, 0.03, 0.06), 0.9, 0.0),
    "Leaf": ((0.03, 0.12, 0.05), 0.7, 0.0),
    "Soil": ((0.04, 0.03, 0.025), 0.95, 0.0),
}
# Emissive materials: name -> (emission colour (linear), strength). LED_<Room> follow the room's switch.
GLOWS = {
    "LED_Living": ((1.0, 0.62, 0.38), 6.0),
    "LED_Bath": ((0.85, 0.92, 1.0), 6.0),
    "LED_Computer": ((0.55, 0.18, 1.0), 8.0),
    "LED_Loft": ((1.0, 0.22, 0.55), 6.0),
    "LED_Bed2": ((0.15, 0.85, 1.0), 6.0),
    "LED_Balcony": ((1.0, 0.45, 0.12), 6.0),
    "Glow_Red": ((1.0, 0.05, 0.08), 7.0),
    "Glow_Rack": ((0.1, 1.0, 0.55), 5.0),
    "Glow_PC": ((0.7, 0.2, 1.0), 6.0),
    "Glow_Pink": ((1.0, 0.12, 0.6), 10.0),
    "Glow_Cyan": ((0.05, 0.9, 1.0), 10.0),
    "Glow_Lock": ((1.0, 0.06, 0.04), 5.0),
    "Display_Stereo": ((0.1, 0.9, 1.0), 4.0),
}
POSTERS = 8


def G(x, y, z):
    """glTF (x, y up, z front) -> Blender (x, -z, y)."""
    return Vector((x, -z, y))


# ---------------------------------------------------------------------------------------------------------------------
# Materials

MATERIALS = {}
TILE = {}  # material -> metres per texture repeat (the UVs are metres / this)


def principled(mat):
    try:
        mat.use_nodes = True
    except Exception:
        pass
    return next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")


def gltf_output_group():
    g = bpy.data.node_groups.get("glTF Material Output")
    if g is None:
        g = bpy.data.node_groups.new("glTF Material Output", "ShaderNodeTree")
        g.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
        g.interface.new_socket("Thickness", in_out="INPUT", socket_type="NodeSocketFloat")
    return g


def load_image(name, colorspace):
    path = os.path.join(TEX, name)
    if not os.path.exists(path):
        sys.exit(f"missing {path}: run `python3 Examples/Neon/Tools/fetch_assets.py` and make_posters.py (or git lfs pull)")
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = colorspace
    return img


def textured_material(name, tex, tint, double):
    mat = bpy.data.materials.new(name)
    bsdf = principled(mat)
    nt = mat.node_tree
    col = nt.nodes.new("ShaderNodeTexImage")
    col.image = load_image(f"{tex}_Color.jpg", "sRGB")
    if tint:
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs[0].default_value = 1.0
        nt.links.new(col.outputs["Color"], mix.inputs[6])
        mix.inputs[7].default_value = (*tint, 1.0)
        nt.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
    else:
        nt.links.new(col.outputs["Color"], bsdf.inputs["Base Color"])
    nor = nt.nodes.new("ShaderNodeTexImage")
    nor.image = load_image(f"{tex}_NormalGL.jpg", "Non-Color")
    nmap = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(nor.outputs["Color"], nmap.inputs["Color"])
    nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    orm = nt.nodes.new("ShaderNodeTexImage")
    orm.image = load_image(f"{tex}_ORM.png", "Non-Color")
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    nt.links.new(orm.outputs["Color"], sep.inputs["Color"])
    nt.links.new(sep.outputs["Green"], bsdf.inputs["Roughness"])
    nt.links.new(sep.outputs["Blue"], bsdf.inputs["Metallic"])
    out = nt.nodes.new("ShaderNodeGroup")
    out.node_tree = gltf_output_group()
    nt.links.new(sep.outputs["Red"], out.inputs["Occlusion"])
    mat.use_backface_culling = not double
    mat.diffuse_color = (*(tint or (0.5, 0.5, 0.5)), 1.0)
    return mat


def poster_material(i):
    mat = bpy.data.materials.new(f"Poster_{i}")
    bsdf = principled(mat)
    img = mat.node_tree.nodes.new("ShaderNodeTexImage")
    img.image = load_image(f"Poster_{i}.png", "sRGB")
    mat.node_tree.links.new(img.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.55
    return mat


def flat_material(name, color, roughness, metallic=0.0):
    mat = bpy.data.materials.new(name)
    bsdf = principled(mat)
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    mat.diffuse_color = (*color, 1.0)
    return mat


def glow_material(name, color, strength):
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
    for name, (tex, tile, tint, double) in TEXTURED.items():
        MATERIALS[name] = textured_material(name, tex, tint, double)
        TILE[name] = tile
    for name, (color, roughness, metallic) in FLAT.items():
        MATERIALS[name] = flat_material(name, color, roughness, metallic)
    for name, (color, strength) in GLOWS.items():
        MATERIALS[name] = glow_material(name, color, strength)
    MATERIALS["Glass"] = glass_material()
    for i in range(1, POSTERS + 1):
        MATERIALS[f"Poster_{i}"] = poster_material(i)


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

    def face(self, pts, mat, uvs=None):
        """A polygon from glTF points (counter-clockwise seen from its front). UVs: metres in its plane, or given."""
        f = self.bm.faces.new([self.bm.verts.new(G(*p)) for p in pts])
        f.material_index = self.mat(mat)
        if uvs is None:
            p = [Vector(q) for q in pts]
            normal = (p[1] - p[0]).cross(p[2] - p[0])
            n = [abs(normal.x), abs(normal.y), abs(normal.z)]
            d = n.index(max(n))
            u_axis, v_axis = [(2, 1), (0, 2), (0, 1)][d]
            scale = 1.0 / TILE.get(mat, 1.0)
            uvs = [(q[u_axis] * scale, q[v_axis] * scale) for q in p]
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        return f

    def hexa(self, c, mat):
        """A convex six-sided solid from its 8 corners c[(i, j, k)] (glTF points; i along x, j along y, k along z)."""
        centre = sum((Vector(p) for p in c.values()), Vector()) / 8
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
                    pts.reverse()
                self.face([tuple(p) for p in pts], mat)

    def box(self, lo, hi, mat):
        """An axis-aligned box from its glTF min and max corners."""
        c = {}
        for i in (0, 1):
            for j in (0, 1):
                for k in (0, 1):
                    c[(i, j, k)] = ((lo[0], hi[0])[i], (lo[1], hi[1])[j], (lo[2], hi[2])[k])
        self.hexa(c, mat)

    def boxc(self, centre, size, mat):
        """A box from its centre and full size."""
        self.box([centre[i] - size[i] / 2 for i in range(3)], [centre[i] + size[i] / 2 for i in range(3)], mat)

    def cylinder(self, a, b, radius, mat, segments=12, caps=True):
        """A cylinder from point a to point b (glTF)."""
        a, b = Vector(a), Vector(b)
        axis = (b - a).normalized()
        other = Vector((0, 1, 0)) if abs(axis.y) < 0.9 else Vector((1, 0, 0))
        u = axis.cross(other).normalized()
        v = axis.cross(u).normalized()
        ring = [(u * math.cos(2 * math.pi * i / segments) + v * math.sin(2 * math.pi * i / segments)) * radius
                for i in range(segments)]
        length = (b - a).length
        circumference = 2 * math.pi * radius
        tile = TILE.get(mat, 1.0)
        for i in range(segments):
            j = (i + 1) % segments
            pts = [a + ring[i], a + ring[j], b + ring[j], b + ring[i]]
            u0, u1 = circumference * i / segments / tile, circumference * (i + 1) / segments / tile
            self.face([tuple(p) for p in pts], mat, [(u0, 0), (u1, 0), (u1, length / tile), (u0, length / tile)])
        if caps:
            self.face([tuple(b + ring[i]) for i in range(segments)], mat)
            self.face([tuple(a + ring[i]) for i in reversed(range(segments))], mat)

    def finish(self, location=(0, 0, 0), yaw=0.0):
        """The object, its origin at glTF <location> turned <yaw> degrees about +Y (points were given in that frame)."""
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
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
# balcony; the mezzanine (the loft over the computer room, the strip outside bedroom 2, the landing) with a thin grate
# deck over a concrete slab (Decor_Slab), and bedroom 2's planks.
WALKABLE = {
    "Floor": [((PX - PT, -0.3, Z0 - T), (X1 + T, 0.0, Z1), "Concrete"),
              ((X0 - T, -0.3, WALL_Z), (PX - PT, 0.0, Z1), "Concrete")],
    "Tile_Bath": [((X0 - T, -0.3, Z0 - T), (PX - PT, 0.0, WALL_Z), "Tile")],
    "Balcony": [((X0 - T, -0.3, Z1), (X1 + T, 0.0, BZ), "Concrete")],
    "Mezzanine": [((X0, MZ - 0.04, WALL_Z + 0.12), (PX, MZ, Z1), "Grate"),
                  ((BED2_X, MZ - 0.04, Z0), (PX, MZ, WALL_Z + 0.12), "Grate"),
                  ((PX, MZ - 0.04, Z0), (LX, MZ, RAMP_TOP), "Grate")],
    "Planks_Bed2": [((X0, MZ - 0.04, Z0), (BED2_X, MZ, WALL_Z + 0.12), "Planks")],
}


def ramp_y(z):
    return (RAMP_FOOT - z) * RISE / TREAD


# ---------------------------------------------------------------------------------------------------------------------
# The shell


def shell():
    objs = []
    for name, boxes in WALKABLE.items():
        b = Builder(name)
        for lo, hi, mat in boxes:
            b.box(lo, hi, mat)
        objs.append(b.finish())
    # The mezzanine's concrete slab under the decks, and its steel edge.
    slab = Builder("Decor_Slab")
    slab.box((X0, MZ - SLAB, Z0), (PX, MZ - 0.04, Z1), "Ceiling")
    slab.box((PX, MZ - SLAB, Z0), (LX, MZ - 0.04, RAMP_TOP), "Ceiling")
    slab.box((PX - 0.02, MZ - SLAB - 0.02, RAMP_TOP), (PX + 0.02, MZ, Z1), "Steel")
    objs.append(slab.finish())
    # The ceiling, over the balcony too (the floor above's balcony): nobody climbs out.
    objs.append(single_box("Ceiling", (X0 - T, H, Z0 - T), (X1 + T, H + 0.3, BZ + 0.1), "Ceiling"))

    walls = Builder("Walls")
    wall(walls, (X0 - T, 0, Z0 - T), (X1 + T, H, Z0), 0, [(FRONT_X[0], FRONT_X[1], 0.0, 2.2)], "Wall")
    walls.box((X0 - T, 0, Z0), (X0, H, Z1), "Wall")
    walls.box((X1, 0, Z0), (X1 + T, H, Z1), "Wall")
    # The ground floor's partition (bathroom and computer room doors) and the wall between those rooms.
    wall(walls, (PX - PT, 0, Z0), (PX, MZ - SLAB, Z1), 2,
         [(BATH_DOOR_Z[0], BATH_DOOR_Z[1], 0.0, DOOR_H), (COMPUTER_DOOR_Z[0], COMPUTER_DOOR_Z[1], 0.0, DOOR_H)], "Wall")
    walls.box((X0, 0, WALL_Z), (PX - PT, MZ - SLAB, WALL_Z + 0.12), "Wall")
    # Bedroom 2 upstairs: its east wall (door) and its south wall onto the loft.
    wall(walls, (BED2_X, MZ, Z0), (BED2_X + PT, H, WALL_Z + 0.12), 2, [(BED2_DOOR_Z[0], BED2_DOOR_Z[1], MZ, MZ + DOOR_H)], "Wall")
    walls.box((X0, MZ, WALL_Z), (BED2_X, H, WALL_Z + 0.12), "Wall")
    # Door frames (steel jambs) round the inner doors' openings.
    for z0, z1 in (BATH_DOOR_Z, COMPUTER_DOOR_Z):
        for z in (z0, z1):
            walls.box((PX - PT - 0.01, 0, z - 0.03), (PX + 0.01, DOOR_H, z + 0.03), "Steel")
        walls.box((PX - PT - 0.01, DOOR_H, z0 - 0.03), (PX + 0.01, DOOR_H + 0.05, z1 + 0.03), "Steel")
    for z in BED2_DOOR_Z:
        walls.box((BED2_X - 0.01, MZ, z - 0.03), (BED2_X + PT + 0.01, MZ + DOOR_H, z + 0.03), "Steel")
    walls.box((BED2_X - 0.01, MZ + DOOR_H, BED2_DOOR_Z[0] - 0.03), (BED2_X + PT + 0.01, MZ + DOOR_H + 0.05, BED2_DOOR_Z[1] + 0.03), "Steel")
    for x in FRONT_X:
        walls.box((x - 0.04, 0, Z0 - 0.02), (x + 0.04, 2.25, Z0 + 0.03), "Steel")
    walls.box((FRONT_X[0] - 0.04, 2.2, Z0 - 0.02), (FRONT_X[1] + 0.04, 2.27, Z0 + 0.03), "Steel")
    objs.append(walls.finish())

    # Linings (no collision): the double-height walls in dark corrugated steel, the bathroom's tiles, a skirting.
    lining = Builder("Decor_Lining")
    lining.box((PX, 2.6, Z0), (X1, H, Z0 + 0.015), "Accent")                  # the living room's north wall, up high
    lining.box((X1 - 0.015, 2.6, Z0), (X1, H, Z1), "Accent")                  # the east wall, up high
    lining.box((X0, MZ, WALL_Z + 0.12), (X0 + 0.015, H, Z1), "Accent")        # the loft's west wall
    lining.box((X0, MZ, WALL_Z + 0.12), (BED2_X + PT, H, WALL_Z + 0.135), "Accent")  # the loft side of bedroom 2's wall
    lining.box((X0, 0, WALL_Z + 0.12), (X0 + 0.015, MZ - SLAB, Z1), "Accent")  # the computer room's west wall
    lining.box((X0, 0, WALL_Z + 0.12), (PX - PT, MZ - SLAB, WALL_Z + 0.135), "Accent")
    tile_h = 2.6
    lining.box((X0, 0, Z0), (PX - PT, tile_h, Z0 + 0.012), "Tile")
    lining.box((X0, 0, Z0), (X0 + 0.012, tile_h, WALL_Z), "Tile")
    lining.box((X0, 0, WALL_Z - 0.012), (PX - PT, tile_h, WALL_Z), "Tile")
    wall(lining, (PX - PT - 0.012, 0, Z0), (PX - PT, tile_h, WALL_Z), 2, [(BATH_DOOR_Z[0], BATH_DOOR_Z[1], 0.0, DOOR_H)], "Tile")
    for lo, hi in (((PX, 0, Z0 + 0.0), (X1, 0.08, Z0 + 0.02)), ((X1 - 0.02, 0, Z0), (X1, 0.08, Z1))):
        lining.box(lo, hi, "Black")
    objs.append(lining.finish())

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
    frame.box((X0, 0, 5.46), (X1, 0.06, 5.6), "Steel")
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
    for z in (RAMP_TOP, 0.5, 2.5, 4.5, Z1 - 0.05):
        rail.box((PX - 0.035, MZ, z - 0.02), (PX + 0.035, MZ + RAIL_H, z + 0.02), "Steel")
    objs.append(rail.finish())
    brail = Builder("Railing_Balcony")
    brail.box((X0 - T, 0, BZ - 0.08), (X1 + T, 1.1, BZ - 0.02), "Glass")
    brail.box((X0 - T, 1.1, BZ - 0.1), (X1 + T, 1.16, BZ), "Steel")
    for x0, x1 in ((X0 - T, X0 - T + 0.06), (X1 + T - 0.06, X1 + T)):
        brail.box((x0, 0, Z1 + 0.08), (x1, 1.1, BZ - 0.08), "Glass")
        brail.box((x0 - 0.01, 1.1, Z1 + 0.08), (x1 + 0.01, 1.16, BZ - 0.08), "Steel")
    for x in range(-7, 8, 2):
        brail.box((x - 0.02, 0, BZ - 0.1), (x + 0.02, 1.1, BZ), "Steel")
    objs.append(brail.finish())
    return objs


def structure():
    """Exposed steel beams under the ceiling, ducts and conduit (decoration: no collision)."""
    b = Builder("Decor_Structure")
    for x in (-4.5, -1.5, 1.5, 4.5):
        # I-beams across the room (north to south), under the ceiling.
        b.box((x - 0.09, H - 0.03, Z0), (x + 0.09, H, Z1), "Steel")
        b.box((x - 0.012, H - 0.36, Z0), (x + 0.012, H - 0.03, Z1), "Steel")
        b.box((x - 0.09, H - 0.39, Z0), (x + 0.09, H - 0.36, Z1), "Steel")
    # A round duct along the living room's north side and a branch towards the windows.
    b.cylinder((PX, H - 0.6, Z0 + 0.5), (X1, H - 0.6, Z0 + 0.5), 0.22, "Steel", 16)
    b.cylinder((4.0, H - 0.6, Z0 + 0.5), (4.0, H - 0.6, 3.5), 0.16, "Steel", 16)
    for z in (0.0, 3.4):
        b.boxc((4.0, H - 0.82, z), (0.5, 0.06, 0.3), "Black")  # vents
    # Conduit runs on the walls.
    b.cylinder((X1 - 0.05, 0.3, -5.4), (X1 - 0.05, 0.3, 4.0), 0.02, "Steel", 8)
    b.cylinder((X1 - 0.05, 0.3, 4.0), (X1 - 0.05, H, 4.0), 0.02, "Steel", 8)
    b.cylinder((-0.3, 0, Z0 + 0.06), (-0.3, H, Z0 + 0.06), 0.06, "Black", 12)  # a soil stack in the kitchen corner
    return b.finish()


def stairs():
    """Steel stairs climbing north between the partition and the landing's edge; collision is Collider_Stairs."""
    b = Builder("Stairs")
    for i in range(1, STEPS + 1):
        z_front = STAIR_NOSE - TREAD * (i - 1)
        b.box((PX + 0.04, RISE * i - 0.04, z_front - TREAD), (LX - 0.04, RISE * i, z_front + 0.02), "Grate")
        for x in (PX + 0.06, LX - 0.1):
            b.box((x, RISE * i - 0.07, z_front - TREAD + 0.02), (x + 0.04, RISE * i - 0.04, z_front), "Steel")
    for x0, x1 in ((PX, PX + 0.06), (LX - 0.06, LX)):
        sloped(b, RAMP_TOP, RAMP_FOOT, x0, x1, lambda z: ramp_y(z) - 0.3, lambda z: ramp_y(z) + 0.02, "Steel")
    return b.finish()


def door_leaf(name, hinge, yaw, width, height, mat="Door", thickness=0.04, keypad=False):
    """A hinged leaf along local -X from the hinge, a handle near its free edge on both faces."""
    b = Builder(name)
    b.box((-width, 0.0, -thickness / 2), (0.0, height, thickness / 2), mat)
    for s in (-1, 1):  # recessed panel lines
        b.box((-width + 0.08, 0.15, s * thickness / 2), (-0.08, 0.17, s * (thickness / 2 + 0.002)), "Black")
        b.box((-width + 0.08, height - 0.17, s * thickness / 2), (-0.08, height - 0.15, s * (thickness / 2 + 0.002)), "Black")
        b.box((-width + 0.06, 1.0, s * thickness / 2), (-width + 0.18, 1.03, s * (thickness / 2 + 0.05)), "Chrome")
        b.box((-width + 0.08, 0.95, s * (thickness / 2 + 0.03)), (-width + 0.1, 1.03, s * (thickness / 2 + 0.05)), "Chrome")
    if keypad:  # the front door's lock: a keypad with a red light, inside
        b.box((-width + 0.1, 1.2, thickness / 2), (-width + 0.24, 1.42, thickness / 2 + 0.02), "Black")
        b.box((-width + 0.13, 1.36, thickness / 2 + 0.02), (-width + 0.21, 1.39, thickness / 2 + 0.025), "Glow_Lock")
    return b.finish(hinge, yaw)


def doors():
    gap = 0.01
    objs = [
        # Hinges at the openings' north/south ends, so each leaf opens into its room (-X, yaw -90).
        door_leaf("Door_Bath", (PX - PT / 2, 0, BATH_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Computer", (PX - PT / 2, 0, COMPUTER_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Bed2", (BED2_X + PT / 2, MZ, BED2_DOOR_Z[1] - gap), -90, DOOR_W - 2 * gap, DOOR_H - gap),
        door_leaf("Door_Front", (FRONT_X[1], 0, Z0 - T / 2), 0, FRONT_X[1] - FRONT_X[0], 2.2, mat="Steel", thickness=0.08,
                  keypad=True),
    ]
    # The balcony slider: a glass leaf in a steel frame, inside the window wall's glass.
    w = SLIDER_X[1] - SLIDER_X[0]
    b = Builder("Door_Balcony")
    b.box((0.04, 0.04, -0.01), (w - 0.04, SLIDER_H - 0.04, 0.01), "Glass")
    for lo, hi in (((0, 0, -0.025), (0.04, SLIDER_H, 0.025)), ((w - 0.04, 0, -0.025), (w, SLIDER_H, 0.025)),
                   ((0, 0, -0.025), (w, 0.04, 0.025)), ((0, SLIDER_H - 0.04, -0.025), (w, SLIDER_H, 0.025))):
        b.box(lo, hi, "Steel")
    b.box((w - 0.16, 0.9, -0.07), (w - 0.12, 1.3, -0.025), "Chrome")
    objs.append(b.finish((SLIDER_X[0], 0, GLASS_Z[0] - 0.05)))
    return objs


# ---------------------------------------------------------------------------------------------------------------------
# Furniture


def poster(b, centre, normal_axis, sign, w, h, i):
    """A framed poster on a wall: centre (glTF), the wall's normal axis (0 = x, 2 = z) and its sign (into the room)."""
    cx, cy, cz = centre
    d = 0.02
    if normal_axis == 2:
        z = cz + sign * d
        b.box((cx - w / 2 - 0.025, cy - h / 2 - 0.025, min(cz, z)), (cx + w / 2 + 0.025, cy + h / 2 + 0.025, max(cz, z)), "Black")
        zf = z + sign * 0.002
        pts = [(cx - w / 2, cy - h / 2, zf), (cx + w / 2, cy - h / 2, zf), (cx + w / 2, cy + h / 2, zf), (cx - w / 2, cy + h / 2, zf)]
        uvs = [(0, 0), (1, 0), (1, 1), (0, 1)]
        if sign < 0:
            pts, uvs = [pts[1], pts[0], pts[3], pts[2]], [(0, 0), (1, 0), (1, 1), (0, 1)]
    else:
        x = cx + sign * d
        b.box((min(cx, x), cy - h / 2 - 0.025, cz - w / 2 - 0.025), (max(cx, x), cy + h / 2 + 0.025, cz + w / 2 + 0.025), "Black")
        xf = x + sign * 0.002
        pts = [(xf, cy - h / 2, cz + w / 2), (xf, cy - h / 2, cz - w / 2), (xf, cy + h / 2, cz - w / 2), (xf, cy + h / 2, cz + w / 2)]
        uvs = [(0, 0), (1, 0), (1, 1), (0, 1)]
        if sign < 0:
            pts = [pts[1], pts[0], pts[3], pts[2]]
    b.face(pts, f"Poster_{i}", uvs)


def plant(name, base, height, rng):
    b = Builder(name)
    x, y, z = base
    b.cylinder((x, y, z), (x, y + 0.42, z), 0.2, "Rubber", 14)
    b.cylinder((x, y + 0.38, z), (x, y + 0.4, z), 0.18, "Soil", 14)
    for i in range(14):  # blade-like leaves: thin tilted boxes fanning out of the pot
        a = 2 * math.pi * i / 14 + rng.uniform(-0.2, 0.2)
        tilt = rng.uniform(0.15, 0.45)
        length = height * rng.uniform(0.6, 1.0)
        top = (x + math.cos(a) * length * tilt, y + 0.4 + length, z + math.sin(a) * length * tilt)
        b.cylinder((x, y + 0.4, z), top, 0.03, "Leaf", 4, caps=False)
    return b.finish()


def furniture(rng):
    F = []

    def furn(name, build):
        b = Builder(name)
        build(b)
        F.append(b.finish())

    # ── Kitchen: base cabinets with a concrete top, uppers, the fridge, the island and its stools ─────────────────
    def counter(b):
        b.box((-0.2, 0.1, Z0), (3.6, 0.88, Z0 + 0.6), "Wood")
        b.box((-0.2, 0.0, Z0), (3.6, 0.1, Z0 + 0.55), "Black")  # toe kick
        b.box((-0.22, 0.88, Z0), (3.62, 0.92, Z0 + 0.62), "Concrete")
        for x in (0.35, 0.95, 1.55, 2.15, 2.75, 3.35):  # door gaps and pulls
            b.box((x - 0.005, 0.12, Z0 + 0.6), (x + 0.005, 0.86, Z0 + 0.603), "Black")
            b.box((x - 0.15, 0.8, Z0 + 0.6), (x - 0.03, 0.81, Z0 + 0.62), "Chrome")
        b.box((1.35, 0.915, Z0 + 0.1), (2.05, 0.925, Z0 + 0.5), "Steel")  # the sink's rim
        b.box((1.4, 0.92, Z0 + 0.14), (2.0, 0.926, Z0 + 0.46), "Black")    # the basin, seen from above
        b.box((2.4, 0.925, Z0 + 0.1), (3.2, 0.93, Z0 + 0.5), "Black")      # induction hob
    furn("Furn_Counter", counter)

    def uppers(b):
        b.box((-0.2, 1.55, Z0), (3.6, 2.3, Z0 + 0.35), "Wood")
        for x in (0.75, 1.7, 2.65):
            b.box((x - 0.005, 1.56, Z0 + 0.35), (x + 0.005, 2.29, Z0 + 0.353), "Black")
    furn("Furn_UpperCabinets", uppers)

    def fridge(b):
        b.box((3.62, 0, Z0), (4.3, 2.1, Z0 + 0.7), "Steel")
        b.box((3.62, 1.3, Z0 + 0.7), (4.3, 1.31, Z0 + 0.703), "Black")
        b.box((3.7, 0.5, Z0 + 0.7), (3.73, 1.25, Z0 + 0.74), "Chrome")
        b.box((3.7, 1.35, Z0 + 0.7), (3.73, 1.9, Z0 + 0.74), "Chrome")
        b.box((4.0, 1.5, Z0 + 0.703), (4.2, 1.6, Z0 + 0.706), "Glow_Cyan")  # a tiny status display
    furn("Furn_Fridge", fridge)

    def island(b):
        b.box((0.65, 0, -3.45), (2.75, 0.88, -2.65), "Wood")
        b.box((0.6, 0.88, -3.5), (2.8, 0.92, -2.6), "Concrete")
        b.box((0.65, 0.02, -2.66), (2.75, 0.05, -2.64), "Glow_Pink")  # underglow
    furn("Furn_Island", island)
    for i, x in enumerate((1.0, 1.7, 2.4)):
        def stool(b, x=x):
            b.cylinder((x, 0.0, -2.2), (x, 0.62, -2.2), 0.025, "Chrome", 8)
            b.cylinder((x, 0.0, -2.2), (x, 0.02, -2.2), 0.18, "Black", 14)
            b.cylinder((x, 0.62, -2.2), (x, 0.7, -2.2), 0.19, "Leather", 16)
            b.cylinder((x, 0.25, -2.2), (x, 0.27, -2.2), 0.14, "Chrome", 12)
        furn(f"Furn_Stool_{i + 1}", stool)

    # ── Living room: the sectional with a red strip, the hex glass table, the TV wall, the bookshelf and its ladder ─
    def sofa_a(b):  # along x = 2.4..3.3, z 0.4..3.6, back to the west
        b.box((2.45, 0.08, 0.4), (3.3, 0.38, 3.6), "Leather")
        b.box((2.4, 0.0, 0.45), (3.25, 0.08, 3.55), "Black")
        b.box((2.4, 0.38, 0.4), (2.62, 0.82, 3.6), "Leather")               # back
        for z in (0.4, 1.47, 2.54):
            b.box((2.62, 0.38, z + 0.03), (3.28, 0.5, z + 1.03), "Leather")  # seat cushions
            b.box((2.6, 0.48, z + 0.08), (2.78, 0.78, z + 0.98), "Leather")  # back cushions
        b.box((2.45, 0.38, 3.42), (3.3, 0.62, 3.6), "Leather")              # arm
        b.box((3.27, 0.03, 0.45), (3.29, 0.05, 3.55), "Glow_Red")           # the strip under the seat
    furn("Furn_SofaA", sofa_a)

    def sofa_b(b):  # along z = 0.4..1.3, x 3.3..5.0, back to the north
        b.box((3.3, 0.08, 0.45), (5.0, 0.38, 1.3), "Leather")
        b.box((3.3, 0.0, 0.5), (4.95, 0.08, 1.25), "Black")
        b.box((3.3, 0.38, 0.4), (5.0, 0.82, 0.62), "Leather")
        for x in (3.3, 4.13):
            b.box((x + 0.03, 0.38, 0.62), (x + 0.83, 0.5, 1.28), "Leather")
            b.box((x + 0.08, 0.48, 0.6), (x + 0.78, 0.78, 0.78), "Leather")
        b.box((4.82, 0.38, 0.45), (5.0, 0.62, 1.3), "Leather")
        b.box((3.3, 0.03, 1.27), (4.95, 0.05, 1.29), "Glow_Red")
    furn("Furn_SofaB", sofa_b)

    def coffee_table(b):
        cx, cz, r = 4.4, 2.45, 0.58
        pts = [(cx + r * math.cos(math.pi / 3 * k), cz + r * math.sin(math.pi / 3 * k)) for k in range(6)]
        for y, mat, rr in ((0.38, "Glass", 1.0), (0.4, "Glass", 1.0)):
            b.face([(cx + (p[0] - cx) * rr, y, cz + (p[1] - cz) * rr) for p in (pts if y > 0.39 else list(reversed(pts)))], mat)
        for k in range(6):
            a, c = pts[k], pts[(k + 1) % 6]
            b.face([(a[0], 0.38, a[1]), (c[0], 0.38, c[1]), (c[0], 0.4, c[1]), (a[0], 0.4, a[1])], "Steel")
        for k in range(0, 6, 2):
            p = pts[k]
            b.cylinder((cx + (p[0] - cx) * 0.8, 0, cz + (p[1] - cz) * 0.8), (cx + (p[0] - cx) * 0.8, 0.38, cz + (p[1] - cz) * 0.8), 0.02, "Black", 8)
        b.box((4.2, 0.4, 2.3), (4.42, 0.44, 2.45), "Black")  # a tablet
        b.cylinder((4.65, 0.4, 2.6), (4.65, 0.5, 2.6), 0.04, "Porcelain", 12)  # a mug
    furn("Furn_CoffeeTable", coffee_table)

    F.append(single_box("Rug_Living", (3.0, 0, 0.0), (6.2, 0.012, 4.0), "Rug"))

    def tv_stand(b):
        b.box((6.5, 0.05, 1.0), (X1, 0.5, 3.0), "Wood")
        b.box((6.5, 0.0, 1.05), (X1, 0.05, 2.95), "Black")
        b.box((6.48, 0.1, 1.98), (6.5, 0.45, 2.02), "Black")
        b.box((6.49, 0.02, 1.05), (6.5, 0.04, 2.95), "Glow_Pink")
    furn("Furn_TvStand", tv_stand)
    tv = Builder("Decor_Tv")
    tv.box((6.94, 0.85, 0.75), (6.99, 2.25, 3.25), "Black")
    F.append(tv.finish())
    # The TV's picture: one quad facing the room (-X) with 0..1 UVs (u to the right, v up), for the TV's feed.
    screen = Builder("Screen_Tv")
    screen.face([(6.93, 0.9, 0.8), (6.93, 0.9, 3.2), (6.93, 2.2, 3.2), (6.93, 2.2, 0.8)], "Screen",
                 [(0, 0), (1, 0), (1, 1), (0, 1)])
    F.append(screen.finish())
    stereo = Builder("Decor_Stereo")
    stereo.box((6.58, 0.5, 1.15), (6.95, 0.66, 1.65), "Rubber")
    for z in (1.2, 1.6):
        stereo.cylinder((6.565, 0.55, z), (6.58, 0.55, z), 0.03, "Chrome", 10)
    for z in (0.42, 3.58):  # floor-standing speakers either side of the TV
        stereo.box((6.6, 0.0, z - 0.15), (6.95, 1.5, z + 0.15), "Black")
        stereo.cylinder((6.598, 0.7, z), (6.6, 0.7, z), 0.1, "Rubber", 16)
        stereo.cylinder((6.598, 1.2, z), (6.6, 1.2, z), 0.05, "Rubber", 12)
    F.append(stereo.finish())
    F.append(single_box("Decor_StereoDisplay", (6.565, 0.58, 1.25), (6.58, 0.61, 1.55), "Display_Stereo"))

    def bookshelf(b):
        x0, x1, z0, z1, top = 6.6, X1, -3.2, -0.2, 5.4
        b.box((x0, 0, z0), (x1, top, z0 + 0.04), "Wood")
        b.box((x0, 0, z1 - 0.04), (x1, top, z1), "Wood")
        b.box((x1 - 0.02, 0, z0), (x1, top, z1), "Black")
        for i in range(6):
            y = 0.35 + i * 1.0 if i < 5 else top - 0.04
            b.box((x0, y - 0.04, z0), (x1, y, z1), "Wood")
        b.box((x0, 0, z0), (x1, 0.1, z1), "Wood")
        b.box((x0 - 0.01, 0.31 + 4 * 1.0, z0 + 0.05), (x0, 0.33 + 4 * 1.0, z1 - 0.05), "LED_Living")  # a shelf strip
    furn("Furn_Bookshelf", bookshelf)
    books = Builder("Decor_Books")
    for shelf in range(5):
        y = 0.35 + shelf * 1.0
        z = -3.14
        while z < -0.3:
            w = rng.uniform(0.03, 0.07)
            h = rng.uniform(0.2, 0.34)
            if rng.random() < 0.08:  # a gap or an object
                z += rng.uniform(0.1, 0.25)
                continue
            lean = 0.0
            books.box((6.7 + rng.uniform(0, 0.04), y, z), (6.96, y + h, z + w + lean),
                      rng.choice(["Leather", "Fabric", "Rug", "Wood", "Black", "RedPaint", "Rubber"]))
            z += w + rng.uniform(0.0, 0.015)
    F.append(books.finish())
    ladder = Builder("Decor_Ladder")
    ladder.cylinder((6.52, 5.3, -3.25), (6.52, 5.3, -0.15), 0.02, "Steel", 8)  # the rail
    for side in (-0.2, 0.2):
        ladder.cylinder((6.53, 5.3, -1.7 + side), (5.75, 0.0, -1.7 + side), 0.022, "Steel", 8)
    for i in range(1, 15):
        t = i / 15
        x = 6.53 + (5.75 - 6.53) * t
        y = 5.3 * (1 - t)
        ladder.box((x - 0.02, y - 0.015, -1.9), (x + 0.02, y + 0.015, -1.5), "Wood")
    F.append(ladder.finish())

    def console(b):
        b.box((6.5, 0.1, -5.3), (X1, 0.9, -4.3), "Wood")
        b.box((6.5, 0.0, -5.25), (X1, 0.1, -4.35), "Black")
        b.cylinder((6.75, 0.9, -4.6), (6.75, 1.25, -4.6), 0.06, "Porcelain", 14)  # a vase
        b.box((6.6, 0.9, -5.2), (6.95, 0.95, -4.9), "Black")                     # a tray
    furn("Furn_Console", console)

    F.append(plant("Decor_Plant_Living", (6.5, 0, 4.9), 1.3, rng))
    F.append(plant("Decor_Plant_Kitchen", (-0.1, 0.92, -5.2), 0.35, rng))

    # ── Bathroom: the shower's glass, plumbing and valve, the vanity and lit mirror, the toilet, a towel rail ─────
    sh = Builder("Glass_Shower")
    sh.box((-5.44, 0, Z0), (-5.4, 2.2, -4.0), "Glass")
    sh.box((X0, 0, -4.04), (-6.1, 2.2, -4.0), "Glass")
    F.append(sh.finish())
    trim = Builder("Decor_ShowerFrame")
    trim.box((-5.45, 2.2, Z0), (-5.39, 2.24, -4.0), "Steel")
    trim.box((X0, 2.2, -4.05), (-6.1, 2.24, -3.99), "Steel")
    F.append(trim.finish())
    plumbing = Builder("Decor_Shower")
    plumbing.cylinder((X0, 2.35, -4.75), (-6.25, 2.35, -4.75), 0.018, "Chrome", 10)
    plumbing.cylinder((-6.25, 2.3, -4.75), (-6.25, 2.35, -4.75), 0.018, "Chrome", 10)
    plumbing.cylinder((-6.25, 2.28, -4.75), (-6.25, 2.31, -4.75), 0.13, "Chrome", 20)  # the rain head
    plumbing.box((-6.5, 0.003, -4.85), (-6.3, 0.006, -4.65), "Steel")                   # the drain
    plumbing.box((-5.33, 1.05, Z0), (-5.17, 1.25, Z0 + 0.012), "Chrome")                 # the valve's plate
    plumbing.cylinder((-5.25, 1.15, Z0 + 0.012), (-5.25, 1.15, Z0 + 0.06), 0.03, "Chrome", 12)
    plumbing.cylinder((-6.9, 1.6, -3.0), (-6.9, 1.6, -2.0), 0.015, "Chrome", 8)        # towel rail
    plumbing.box((-6.93, 0.9, -2.9), (-6.88, 1.6, -2.1), "Fabric")                      # a towel
    F.append(plumbing.finish())

    def vanity(b):
        b.box((-3.3, 0.2, Z0), (-2.1, 0.8, Z0 + 0.5), "Wood")
        b.box((-3.32, 0.8, Z0), (-2.08, 0.85, Z0 + 0.52), "Porcelain")
        b.box((-3.0, 0.85, Z0 + 0.12), (-2.4, 0.9, Z0 + 0.44), "Porcelain")  # vessel basin
    furn("Furn_Vanity", vanity)
    taps = Builder("Decor_Taps")
    taps.cylinder((1.7, 0.92, Z0 + 0.06), (1.7, 1.25, Z0 + 0.06), 0.018, "Chrome")  # the kitchen's
    taps.cylinder((1.7, 1.25, Z0 + 0.06), (1.7, 1.25, Z0 + 0.22), 0.015, "Chrome")
    taps.cylinder((1.7, 1.25, Z0 + 0.22), (1.7, 1.2, Z0 + 0.22), 0.015, "Chrome")
    taps.cylinder((-2.7, 0.85, Z0 + 0.06), (-2.7, 1.1, Z0 + 0.06), 0.015, "Chrome")  # the vanity's
    taps.cylinder((-2.7, 1.1, Z0 + 0.06), (-2.7, 1.1, Z0 + 0.17), 0.012, "Chrome")
    F.append(taps.finish())

    def toilet(b):
        b.box((-4.35, 0, Z0 + 0.15), (-3.85, 0.38, Z0 + 0.68), "Porcelain")
        b.box((-4.38, 0.38, Z0 + 0.12), (-3.82, 0.42, Z0 + 0.7), "Porcelain")
        b.box((-4.35, 0.42, Z0 + 0.02), (-3.85, 0.8, Z0 + 0.2), "Porcelain")
    furn("Furn_Toilet", toilet)
    mirror = Builder("Decor_Mirror")
    mirror.box((-3.25, 1.1, Z0 + 0.012), (-2.15, 2.0, Z0 + 0.03), "Mirror")
    F.append(mirror.finish())

    # ── Computer room: the desk with six monitors on arms, the PC, two racks, gear cases, the chair, a cable tray ──
    def desk(b):
        b.box((-6.9, 0.72, 0.3), (-5.9, 0.75, 3.7), "Rubber")
        b.box((-6.92, 0.69, 0.28), (-5.88, 0.72, 3.72), "Steel")
        for z in (0.35, 3.65):
            b.box((-6.85, 0, z - 0.03), (-5.95, 0.69, z + 0.03), "Steel")
        b.box((-6.85, 0.69, 0.35), (-6.82, 0.70, 3.65), "LED_Computer")
        b.box((-6.3, 0.75, 1.75), (-6.12, 0.77, 2.25), "Black")    # keyboard
        b.box((-6.18, 0.77, 1.78), (-6.13, 0.775, 2.22), "Glow_PC")
        b.box((-6.25, 0.75, 2.45), (-6.15, 0.77, 2.52), "Black")   # mouse
        for z in (0.9, 2.0, 3.1):  # the monitor arms' poles
            b.cylinder((-6.75, 0.75, z), (-6.75, 1.9, z), 0.025, "Steel", 10)
    furn("Furn_Desk", desk)
    pc = Builder("Furn_PC")
    pc.box((-6.85, 0, 3.0), (-6.35, 0.5, 3.25), "Black")
    pc.box((-6.35, 0.03, 3.02), (-6.34, 0.47, 3.23), "Glass")
    pc.box((-6.8, 0.05, 3.05), (-6.4, 0.06, 3.2), "Glow_PC")
    pc.box((-6.8, 0.44, 3.05), (-6.4, 0.45, 3.2), "Glow_PC")
    for y in (0.15, 0.3):
        pc.cylinder((-6.6, y, 3.04), (-6.6, y, 3.06), 0.06, "Glow_PC", 14)
    F.append(pc.finish())

    def rack(b, x0, x1):
        b.box((x0, 0, -1.3), (x1, 2.0, -0.62), "Black")
        b.box((x0 + 0.03, 0.05, -0.62), (x1 - 0.03, 1.95, -0.6), "Glass")
        for i in range(16):
            y = 0.12 + i * 0.115
            b.box((x0 + 0.05, y, -0.64), (x1 - 0.05, y + 0.09, -0.63), "Steel")
            for k in range(4):
                if (i * 7 + k * 3) % 5:
                    xx = x0 + 0.09 + k * 0.035
                    b.box((xx, y + 0.03, -0.635), (xx + 0.012, y + 0.042, -0.628), "Glow_Rack")
    furn("Furn_Rack1", lambda b: rack(b, -2.4, -1.75))
    furn("Furn_Rack2", lambda b: rack(b, -3.3, -2.65))

    def cases(b):
        y = 0.0
        for w, h, d in ((0.9, 0.45, 0.6), (0.8, 0.4, 0.55), (0.6, 0.3, 0.45)):
            x0 = -4.2 - w / 2
            b.box((x0, y, -1.42), (x0 + w, y + h, -1.42 + d), "Rubber")
            for xx in (x0 + 0.12, x0 + w - 0.2):
                b.box((xx, y + h - 0.14, -1.42 + d), (xx + 0.08, y + h - 0.06, -1.42 + d + 0.025), "RedPaint")
            b.box((x0 + w / 2 - 0.15, y + h, -1.42 + d / 2 - 0.02), (x0 + w / 2 + 0.15, y + h + 0.03, -1.42 + d / 2 + 0.02), "Black")
            y += h
    furn("Furn_Cases", cases)

    def chair(b):  # a racing chair facing -X (the monitors); the back on the +X side
        cx, cz = -5.2, 2.0
        for k in range(5):
            a = 2 * math.pi * k / 5
            b.cylinder((cx, 0.08, cz), (cx + 0.32 * math.cos(a), 0.05, cz + 0.32 * math.sin(a)), 0.025, "Black", 6)
        b.cylinder((cx, 0.08, cz), (cx, 0.42, cz), 0.035, "Chrome", 10)
        b.box((cx - 0.27, 0.42, cz - 0.27), (cx + 0.27, 0.52, cz + 0.27), "Leather")
        b.box((cx + 0.22, 0.5, cz - 0.27), (cx + 0.32, 1.25, cz + 0.27), "Leather")
        b.box((cx + 0.2, 0.62, cz - 0.17), (cx + 0.225, 1.15, cz + 0.17), "Fabric")
        for s in (-1, 1):
            b.box((cx + 0.21, 0.5, cz + s * 0.27 - 0.04 * s), (cx + 0.33, 1.2, cz + s * 0.3), "RedPaint")
            b.box((cx - 0.15, 0.62, cz + s * 0.3 - 0.03), (cx + 0.15, 0.65, cz + s * 0.3 + 0.03), "Black")
            b.box((cx - 0.02, 0.5, cz + s * 0.3 - 0.015), (cx + 0.02, 0.62, cz + s * 0.3 + 0.015), "Black")
    furn("Furn_Chair", chair)
    tray = Builder("Decor_CableTray")
    tray.box((-6.8, MZ - SLAB - 0.12, 1.0), (-1.7, MZ - SLAB - 0.1, 1.3), "Grate")
    for z in (1.0, 1.28):
        tray.box((-6.8, MZ - SLAB - 0.12, z), (-1.7, MZ - SLAB - 0.05, z + 0.02), "Steel")
    for x in (-6.0, -4.0, -2.2):
        tray.box((x, MZ - SLAB - 0.1, 1.14), (x + 0.02, MZ - SLAB, 1.16), "Steel")
    for k in range(5):
        tray.cylinder((-6.8, MZ - SLAB - 0.09, 1.05 + k * 0.045), (-1.7, MZ - SLAB - 0.09, 1.05 + k * 0.045), 0.015,
                      ["Black", "RedPaint", "Black", "Steel", "Black"][k], 6, caps=False)
    tray.cylinder((-6.8, MZ - SLAB - 0.09, 1.1), (-6.8, 0.75, 1.1), 0.02, "Black", 6)  # down to the desk
    F.append(tray.finish())

    # The monitors: three columns of two in an arc round the chair, each facing it (the screen's face is local +X).
    monitors = []
    for i in range(6):
        a = math.radians(35 * (i % 3 - 1))
        x, z = -5.2 - 1.1 * math.cos(a), 2.0 + 1.1 * math.sin(a)
        y = 1.0 + 0.4 * (i // 3)
        # The picture: one quad on local +X (towards the chair) with 0..1 UVs (u to the viewer's right, -Z; v up).
        m = Builder(f"Screen_Monitor_{i + 1}")
        m.face([(0.0, -0.17, 0.29), (0.0, -0.17, -0.29), (0.0, 0.17, -0.29), (0.0, 0.17, 0.29)], "Screen",
               [(0, 0), (1, 0), (1, 1), (0, 1)])
        F.append(m.finish((x, y, z), math.degrees(a)))
        bezel = Builder(f"Decor_Bezel_{i + 1}")
        bezel.box((-0.04, -0.185, -0.305), (-0.002, 0.185, 0.305), "Black")
        bezel.box((-0.12, -0.03, -0.03), (-0.045, 0.03, 0.03), "Steel")
        F.append(bezel.finish((x, y, z), math.degrees(a)))
        # The socket's +Z faces the chair.
        monitors.append(((x + 0.014 * math.cos(a), y, z - 0.014 * math.sin(a)), math.degrees(a) + 90))

    # ── Loft: the bed and its bedding, nightstands, the wardrobe, the rug ──────────────────────────────────────────
    def bed(b):
        b.box((-6.9, MZ, 1.4), (-4.9, MZ + 0.28, 3.6), "Wood")
        b.box((-6.85, MZ + 0.28, 1.45), (-4.95, MZ + 0.5, 3.55), "Linen")
        b.box((-6.86, MZ + 0.5, 1.44), (-5.6, MZ + 0.55, 3.56), "Blanket")   # the blanket, turned down
        b.box((-5.6, MZ + 0.5, 1.48), (-5.3, MZ + 0.56, 3.52), "Blanket")
        for z in (1.65, 2.55):
            b.box((-4.9 - 0.42, MZ + 0.5, z), (-4.98, MZ + 0.64, z + 0.8), "Linen")
        b.box((-4.9, MZ, 1.35), (-4.82, MZ + 1.0, 3.65), "Leather")         # headboard
    furn("Furn_Bed", bed)
    for z in (0.9, 3.85):
        def nightstand(b, z=z):
            b.box((-5.3, MZ, z - 0.25), (-4.9, MZ + 0.5, z + 0.25), "Wood")
            b.cylinder((-5.1, MZ + 0.5, z), (-5.1, MZ + 0.85, z), 0.012, "Chrome", 8)
            b.cylinder((-5.1, MZ + 0.82, z), (-5.1, MZ + 0.95, z), 0.09, "LED_Loft", 14)
        furn(f"Furn_Nightstand_{1 if z < 2 else 2}", nightstand)

    def wardrobe(b):
        b.box((X0, MZ, -1.3), (-6.4, MZ + 2.2, 0.6), "Wood")
        for z in (-0.35, 0.6 - 0.01):
            b.box((-6.4, MZ + 0.02, z - 0.005), (-6.398, MZ + 2.18, z + 0.005), "Black")
        for z in (-0.45, -0.25):
            b.box((-6.4, MZ + 1.0, z - 0.01), (-6.37, MZ + 1.4, z + 0.01), "Chrome")
    furn("Furn_Wardrobe", wardrobe)
    F.append(single_box("Rug_Loft", (-6.2, MZ, -0.8), (-3.0, MZ + 0.012, 1.2), "Rug"))

    # ── Bedroom 2: the bunk, a desk and chair, a neon sign ─────────────────────────────────────────────────────────
    def bunk(b):
        x0, x1, z0, z1 = -6.9, -5.9, Z0 + 0.1, -3.4
        for x in (x0, x1 - 0.05):
            for z in (z0, z1 - 0.05):
                b.box((x, MZ, z), (x + 0.05, MZ + 1.9, z + 0.05), "Steel")
        for y in (0.3, 1.3):
            b.box((x0, MZ + y, z0), (x1, MZ + y + 0.05, z1), "Steel")
            b.box((x0 + 0.03, MZ + y + 0.05, z0 + 0.03), (x1 - 0.03, MZ + y + 0.22, z1 - 0.03), "Linen")
            b.box((x0 + 0.03, MZ + y + 0.22, z0 + 0.5), (x1 - 0.03, MZ + y + 0.26, z1 - 0.03), "Fabric")
            b.box((x0 + 0.15, MZ + y + 0.22, z0 + 0.06), (x1 - 0.15, MZ + y + 0.32, z0 + 0.4), "Linen")
        b.box((x0, MZ + 1.62, z0), (x1, MZ + 1.66, z0 + 0.05), "Steel")
    furn("Furn_Bunk", bunk)

    def bed2_desk(b):
        b.box((-5.5, MZ + 0.72, -2.2), (-4.0, MZ + 0.75, WALL_Z), "Wood")
        for x in (-5.45, -4.1):
            b.box((x, MZ, -2.15), (x + 0.05, MZ + 0.72, WALL_Z - 0.02), "Steel")
        b.box((-5.0, MZ + 0.75, -1.75), (-4.5, MZ + 1.05, -1.7), "Black")   # a small monitor
        b.box((-4.98, MZ + 0.77, -1.75), (-4.52, MZ + 1.03, -1.755), "Glow_Cyan")
    furn("Furn_Bed2Desk", bed2_desk)
    sign = Builder("Decor_Sign_Bed2")
    glyph_sign(sign, (-6.98, MZ + 1.9, -2.7), 2, rng, "Glow_Pink", along=-1)
    F.append(sign.finish())

    # ── Balcony: the AC unit, planters, two chairs and a table, a big neon sign on the facade ──────────────────────
    def ac_unit(b):
        b.box((5.6, 0.05, 6.0), (6.6, 0.7, 6.7), "Steel")
        b.box((5.65, 0, 6.05), (6.55, 0.05, 6.65), "Black")
        b.cylinder((6.1, 0.38, 6.7), (6.1, 0.38, 6.72), 0.25, "Grate", 20)
    furn("Furn_AcUnit", ac_unit)
    for i, x in enumerate((-6.3, -0.5)):
        F.append(plant(f"Decor_Plant_Balcony_{i + 1}", (x, 0, 7.4), 1.0, rng))

    def lounger(b, x):
        b.box((x - 0.3, 0.35, 6.3), (x + 0.3, 0.42, 6.9), "Rubber")
        b.box((x - 0.3, 0.42, 6.85), (x + 0.3, 0.9, 6.92), "Rubber")
        for dx in (-0.27, 0.25):
            for dz in (6.32, 6.85):
                b.box((x + dx, 0, dz), (x + dx + 0.03, 0.35, dz + 0.03), "Steel")
    furn("Furn_BalconyChair_1", lambda b: lounger(b, -3.6))
    furn("Furn_BalconyChair_2", lambda b: lounger(b, -2.2))
    facade = Builder("Decor_Sign_Facade")
    glyph_sign(facade, (X1 + T + 0.35, 4.6, BZ - 0.3), 6, random.Random(7), "Glow_Cyan", along=0, vertical=True)
    facade.box((X1 + T, 0.4, BZ - 0.35), (X1 + T + 0.3, 5.8, BZ - 0.25), "Steel")
    F.append(facade.finish())

    # ── Posters ─────────────────────────────────────────────────────────────────────────────────────────────────────
    posters = Builder("Decor_Posters")
    poster(posters, (6.0, 1.75, Z0), 2, 1, 0.6, 0.9, 1)                   # the entry
    poster(posters, (0.4, 4.2, Z0 + 0.015), 2, 1, 1.0, 1.5, 2)            # high on the living room's north wall
    poster(posters, (1.9, 4.2, Z0 + 0.015), 2, 1, 1.0, 1.5, 4)
    poster(posters, (-6.2, MZ + 1.9, WALL_Z + 0.135), 2, 1, 0.7, 1.05, 3)  # the loft, over the wardrobe's side
    poster(posters, (-4.0, MZ + 1.9, WALL_Z + 0.135), 2, 1, 0.7, 1.05, 5)
    poster(posters, (-6.2, 1.7, WALL_Z + 0.135), 2, 1, 0.6, 0.9, 6)        # the computer room
    poster(posters, (X0 + 0.01, MZ + 1.6, -3.0), 0, 1, 0.6, 0.9, 7)        # bedroom 2
    poster(posters, (BED2_X - 0.01, MZ + 1.6, -2.3), 0, -1, 0.5, 0.75, 8)
    F.append(posters.finish())
    return F, monitors


def glyph_sign(b, origin, count, rng, glow, along=2, vertical=False):
    """A neon sign of made-up glyphs (thin glowing strokes on a 4x4 grid per cell, not any script's characters), in the
    plane facing along the wall's normal: <along> = 0 for a sign on an x wall (strokes in z, y), 2 for a z wall, -1 for
    the west wall's face. Vertical stacks the cells downwards."""
    s = 0.28
    ox, oy, oz = origin
    for c in range(count):
        cell_u = 0.0 if vertical else c * s * 1.3
        cell_v = -c * s * 1.3 if vertical else 0.0
        for _ in range(rng.randint(3, 5)):
            gu, gv = rng.randint(0, 4) / 4 * s, rng.randint(0, 4) / 4 * s
            if rng.random() < 0.5:
                du, dv = rng.choice((0.5, 0.75, 1.0)) * s * (1 if gu < s / 2 else -1), 0.0
            else:
                du, dv = 0.0, -rng.choice((0.25, 0.5, 0.75)) * s
            u0, u1 = sorted((cell_u + gu, cell_u + gu + du))
            v0, v1 = sorted((cell_v + gv - s, cell_v + gv - s + dv))
            u1, v1 = max(u1, u0 + 0.025), max(v1, v0 + 0.025)
            if along in (0, -1):
                b.box((ox - 0.015, oy + v0, oz + u0 - (count * s * 0.65 if not vertical else 0)),
                      (ox + 0.015, oy + v1, oz + u1 - (count * s * 0.65 if not vertical else 0)), glow)
            else:
                b.box((ox + u0, oy + v0, oz - 0.015), (ox + u1, oy + v1, oz + 0.015), glow)


def fixtures():
    """The lights' glowing fixtures (LED_<Room>_<n>, material LED_<Room>) in steel housings, and the switch plates."""
    objs = []
    for name, lo, hi, hang in (
            ("LED_Living_1", (-0.5, H - 0.9, -2.5), (1.5, H - 0.87, -2.45), True),
            ("LED_Living_2", (3.5, H - 0.9, 1.0), (5.5, H - 0.87, 1.05), True),
            ("LED_Living_5", (3.5, H - 0.9, 3.0), (5.5, H - 0.87, 3.05), True),
            ("LED_Living_3", (0.2, 1.52, -5.2), (3.4, 1.55, -5.1), False),
            ("LED_Living_4", (5.0, 2.95, -4.7), (5.8, 3.0, -4.5), False),
            ("LED_Bath_1", (-5.0, MZ - SLAB - 0.03, -3.6), (-3.6, MZ - SLAB, -3.4), False),
            ("LED_Bath_2", (-3.27, 2.02, Z0 + 0.02), (-2.13, 2.05, Z0 + 0.05), False),
            ("LED_Computer_1", (-6.8, MZ - SLAB - 0.03, 0.0), (-1.8, MZ - SLAB, 0.04), False),
            ("LED_Computer_2", (-6.8, MZ - SLAB - 0.03, 4.0), (-1.8, MZ - SLAB, 4.04), False),
            ("LED_Loft_1", (-6.8, H - 0.03, 2.4), (-1.8, H, 2.44), False),
            ("LED_Bed2_1", (-6.0, H - 0.03, -3.6), (-4.0, H, -3.4), False),
            ("LED_Balcony_1", (-1.0, H - 0.03, 7.2), (1.0, H, 7.3), False),
            ("LED_Balcony_2", (-6.0, H - 0.03, 7.2), (-4.0, H, 7.3), False),
    ):
        room = name.split("_")[1]
        b = Builder(name)
        b.box(lo, hi, f"LED_{room}")
        if hang:  # a hanging bar: a housing over the strip and two rods to the ceiling
            b.box((lo[0] - 0.02, hi[1], lo[2] - 0.03), (hi[0] + 0.02, hi[1] + 0.06, hi[2] + 0.03), "Black")
            for x in (lo[0] + 0.2, hi[0] - 0.2):
                b.cylinder((x, hi[1] + 0.06, (lo[2] + hi[2]) / 2), (x, H, (lo[2] + hi[2]) / 2), 0.006, "Steel", 6)
        objs.append(b.finish())
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

# Blinds panels: (position on the wall's face, normal axis, sign); each drives its room's Blinds_<Room>_<n>.
PANELS = {
    "Living": ((SLIDER_X[0] - 0.04, 1.2, 5.475), 2, -1),
    "Computer": ((X0, 1.2, 5.0), 0, 1),
    "Loft": ((X0, MZ + 1.2, 5.0), 0, 1),
}

BLINDS = {
    # name: (top centre, half width, drop)
    "Blinds_Living_1": ((0.25, H - 0.12, 5.42), 1.7, H - 0.3),
    "Blinds_Living_2": ((5.2, H - 0.12, 5.42), 1.72, H - 0.3),
    "Blinds_Living_3": ((2.7, H - 0.12, 5.42), 0.66, H - SLIDER_H - 0.3),
    "Blinds_Computer_1": ((-4.25, MZ - SLAB - 0.04, 5.42), 2.68, MZ - SLAB - 0.2),
    "Blinds_Loft_1": ((-4.25, H - 0.12, 5.42), 2.68, H - MZ - 0.3),
}

LIGHTS = {
    # name: (position, range)
    "Light_Living_1": ((0.5, 5.0, -1.0), 10.0),
    "Light_Living_2": ((4.5, 5.0, 2.0), 10.0),
    "Light_Living_3": ((1.7, 1.4, -4.9), 4.0),
    "Light_Living_4": ((5.4, 2.6, -4.6), 4.0),
    "Light_Living_5": ((6.4, 4.0, -1.7), 4.5),
    "Light_Bath_1": ((-4.3, 2.4, -3.5), 5.0),
    "Light_Bath_2": ((-2.7, 1.9, -5.0), 2.5),
    "Light_Computer_1": ((-4.3, 2.4, 2.0), 6.5),
    "Light_Computer_2": ((-2.5, 1.2, -0.3), 3.5),
    "Light_Computer_3": ((-6.2, 0.8, 2.0), 2.5),
    "Light_Loft_1": ((-4.3, 5.4, 2.0), 7.0),
    "Light_Loft_2": ((-5.1, MZ + 1.0, 0.9), 3.0),
    "Light_Bed2_1": ((-5.0, 5.4, -3.5), 5.0),
    "Light_Bed2_2": ((-6.6, MZ + 1.7, -2.8), 3.0),
    "Light_Balcony_1": ((0.0, 5.5, 6.8), 9.0),
    "Light_Balcony_2": ((-5.0, 5.5, 6.8), 7.0),
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
    for prefix, table in (("Plate", SWITCHES), ("Plate_Blinds", PANELS)):
        for room, (p, axis, sign) in table.items():
            half = [0.04, 0.06, 0.04] if prefix == "Plate" else [0.05, 0.08, 0.05]
            half[axis] = 0.008
            c = list(p)
            c[axis] += sign * half[axis]
            b = Builder(f"{prefix}_{room}")
            b.box([c[i] - half[i] for i in range(3)], [c[i] + half[i] for i in range(3)], "Black")
            led = list(c)
            led[axis] += sign * (half[axis] + 0.001)
            led[1] += half[1] * 0.6
            b.boxc(led, [0.012 if i != axis else 0.002 for i in range(3)], "Glow_Cyan" if prefix != "Plate" else "Glow_Lock")
            objs.append(b.finish())
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
    for room, (p, axis, sign) in PANELS.items():
        c = list(p)
        c[axis] += sign * 0.008
        E.append(empty(f"Panel_Blinds_{room}", c, size=0.1))
    for name, (top, half, drop) in BLINDS.items():
        E.append(empty(name, top, size=0.2, scale=(half, drop, 1.0)))
    for room, (centre, half) in ZONES.items():
        E.append(empty(f"Zone_{room}", centre, size=0.5, scale=half))
    # Colliders: the walkable slabs' boxes,
    for name, boxes in WALKABLE.items():
        for i, (lo, hi, _) in enumerate(boxes):
            if hi[1] > 1.0:  # the mezzanine's thin decks: the collider is the slab's full thickness
                lo = (lo[0], MZ - SLAB, lo[2])
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
    # Interactables' places.
    E.append(empty("Shower", (-6.25, 2.27, -4.75)))
    E.append(empty("Valve_Shower", (-5.25, 1.15, Z0 + 0.06)))
    E.append(empty("Tap_Kitchen", (1.7, 1.19, Z0 + 0.22)))
    E.append(empty("Tap_Bath", (-2.7, 1.08, Z0 + 0.17)))
    E.append(empty("Tv", (6.89, 1.55, 2.0), yaw=90))
    E.append(empty("Stereo", (6.75, 0.58, 1.4)))
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
              export_image_format="AUTO", export_texture_dir="Textures", export_cameras=False, export_lights=False,
              export_animations=False, export_skins=False, export_morph=False, export_extras=False)
    try:
        bpy.ops.export_scene.gltf(**kw, export_vertex_color="NONE")
    except TypeError:
        bpy.ops.export_scene.gltf(**kw)
    with open(path) as f:
        gltf = json.load(f)
    # Point the images at the committed textures (the exporter's copies are thrown away).
    for img in gltf.get("images", []):
        name = os.path.basename(img["uri"])
        if not os.path.exists(os.path.join(TEX, name)):
            sys.exit(f"exporter wrote an image that is not one of the texture sets: {name}")
        img["uri"] = f"Textures/{name}"
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
    render(os.path.join(out_dir, "computer.png"), (-2.3, 1.6, 4.4), (-6.5, 1.0, 1.5), lens=18, exposure=1.0)
    render(os.path.join(out_dir, "bath.png"), (-2.0, 1.6, -2.2), (-6.0, 1.0, -5.0), lens=16, exposure=1.0)
    render(os.path.join(out_dir, "balcony.png"), (-5.5, 1.6, 6.0), (4.0, 0.8, 7.5), lens=18, exposure=1.0)


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

    meshes = shell() + [structure(), stairs()] + doors()
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
