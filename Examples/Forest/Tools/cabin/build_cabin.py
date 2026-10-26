"""Builds the Forest's log cabin: Examples/Forest/Assets/Art/Cabin/cabin.gltf + cabin.bin (glTF separate; textures
referenced from Assets/Art/Cabin/Textures, written by `fetch_assets.py --only cabin`).

    just forest-cabin
    "$BLENDER" -b --factory-startup --python Examples/Forest/Tools/cabin/build_cabin.py [-- --preview DIR] [-- --verify DIR]

The geometry is original and generated here (seeded, so every run writes the same file); the only art is the CC0 texture
sets in CABIN_TEXTURES (ambientCG and Poly Haven, listed in NOTICE.md). The Poly Haven furniture in Assets/Art/Props is
imported only to fit the Place_<id> sockets (rest on the floor, the table, the hearth...) and for the previews; it is
never exported.

Conventions (glTF space): metres, +Y up, the door (front) wall faces +Z, the origin is ground level (y = 0) at the
footprint's centre and the floor's top is at y = FLOOR. Empties exported as mesh-less nodes:

- Place_<polyhaven id>[.001]: where each Poly Haven scan stands (instantiate its glTF at the node, scale 1).
- Fire (centre of the firebox floor), ChimneyTop (top of the flue), CandleFlame (the candle's wick on the mantel).
- Interior: location = the room's centre at floor level; scale = (half width x, wall height above the floor, half depth z).
- R9: a camera pose for a reference shot; the node's -Z axis is the view direction, +Y up.
- The Door mesh's origin is on its hinge axis at floor level: rotating it by a NEGATIVE angle about local +Y swings it
  open inward (into the room); 0 = closed.

--preview DIR renders previews (exterior 3/4 front and back, through the door, R9 with the furniture, a floor plan) and
--verify DIR re-imports the exported cabin.gltf into an empty scene, places the furniture at its sockets and renders.
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
FOREST = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(FOREST, "Assets", "Art", "Cabin")
TEX = os.path.join(OUT, "Textures")
PROPS = os.path.join(FOREST, "Assets", "Art", "Props")

# ---------------------------------------------------------------------------------------------------------------------
# Dimensions (glTF space)

IN_X, IN_Z = 3.0, 2.5          # interior half extents (6.0 x 5.0 m)
R = 0.155                      # nominal log radius (~0.31 m logs)
WX, WZ = IN_X + R, IN_Z + R    # wall log centre lines
OX, OZ = WX + R, WZ + R        # outer faces
END = 0.35                     # log ends project this far past the crossing wall's outer face
FLOOR = 0.45                   # floor top
S = 0.26                       # course spacing (< 2 R: logs overlap, so no daylight between them)
Y0_X = 0.40                    # first front/back course centre; side walls are half a course higher
Y0_Z = Y0_X + S / 2
X_COURSES = 11                 # front/back courses k = 0..10 full (+ k = 11, cut by the roof, closes the eave)
RIDGE_UNDER = 4.95             # roof underside at the ridge
EAVE_TOP = Y0_X + (X_COURSES - 1) * S + R
SLOPE = (RIDGE_UNDER - EAVE_TOP) / OZ
ROOF_T = 0.14                  # roof thickness (decking + shingles), perpendicular to the slope
ROOF_X = OX + 0.55             # rake overhang
ROOF_Z = OZ + 0.50             # eave overhang
CUT_OFF = 0.05                 # logs are cut this far above the roof's underside (the cut faces hide in the roof)

COS_A = 1 / math.sqrt(1 + SLOPE * SLOPE)
SIN_A = SLOPE * COS_A

# Openings: (wall, along-wall cut interval, course centres cut (y range)) — the frames cover the cut log ends.
DOOR_X = (0.95, 1.85)          # clear opening, front wall; 1.95 m tall from the floor
W1_X = (-1.40, -0.60)          # front window
W2_X = (-0.75, 0.05)           # back window, behind the table
W3_Z = (0.50, 1.30)            # right (east, +x) window
FIRE_Z = (-0.75, 0.75)         # fireplace opening, left (west, -x) wall
JAMB = 0.06

# Texture sets: material -> (texture id, metres per repeat (u, v)). fetch_assets.py CABIN lists the same ids.
CABIN_TEXTURES = {
    "LogWall": "rough_wood",
    "LogEnd": "TreeEnd002",
    "Floor": "old_wood_floor",
    "Roof": "WoodSiding012",
    "Stone": "stone_wall",
    "DoorWood": "weathered_planks",
    "Fabric": "wool_boucle",
    "Linen": "Fabric045",
    "Leather": "fabric_leather_02",
}
TINTS = {"LogEnd": (0.50, 0.47, 0.46), "Linen": (0.86, 0.82, 0.74)}  # the sawn ends weathered toward the logs' grey (baseColorFactor)
LOG_U, LOG_V = 1.0, 1.2        # one repeat around a log, 1.2 m along it (rough_wood is a 0.5 m scan, grain along v)
FLOOR_REP = 3.0                # old_wood_floor: 3 m scan, boards along u
ROOF_REP = 1.8                 # WoodSiding012: a 1.4 m scan of eight shingle courses, shown as 22 cm shakes
STONE_REP = 1.8                # stone_wall: 2 m scan
PLANK_REP = 2.0                # weathered_planks: 2 m scan, planks along v
FABRIC_REP = (0.63, 0.78)      # wool_boucle: 0.31 x 0.39 m scan shown at twice its size (a bolder check)
LINEN_REP = 0.6
LEATHER_REP = 0.5


def G(x, y, z):
    """glTF (x, y up, z front) -> Blender (x, -z, y)."""
    return Vector((x, -z, y))


def roof_under(z):
    return RIDGE_UNDER - SLOPE * abs(z)


# ---------------------------------------------------------------------------------------------------------------------
# Mesh building


class Builder:
    """One object: a BMesh with a UV layer and a material list."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.mats = []

    def mat(self, name):
        if name not in self.mats:
            self.mats.append(name)
        return self.mats.index(name)

    def face(self, pts, uvs, mat, smooth=False):
        vs = [self.bm.verts.new(p) for p in pts]
        f = self.bm.faces.new(vs)
        f.material_index = self.mat(mat)
        f.smooth = smooth
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        return f

    def finish(self, origin=None, weld=False):
        bm = self.bm
        if weld:
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bmesh.ops.dissolve_degenerate(bm, dist=1e-6, edges=bm.edges)
        mesh = bpy.data.meshes.new(self.name)
        bm.to_mesh(mesh)
        bm.free()
        obj = bpy.data.objects.new(self.name, mesh)
        for m in self.mats:
            mesh.materials.append(MATERIALS[m])
        if origin is not None:
            mesh.transform(Matrix.Translation(-origin))
            obj.location = origin
        bpy.context.scene.collection.objects.link(obj)
        return obj


def box(b, center, axes, half, mat, uv="grain", rep=PLANK_REP, bevel=0.0, mats=None, uv_fn=None):
    """A box in Blender space: center, three orthonormal axes, half sizes. uv: 'grain' (v along the longest axis,
    local coords), 'world' (box projection from world coords: stone), or uv_fn(face_axis, sign, local) -> (u, v).
    mats: {(axis, sign): material} overrides per face."""
    m = Matrix((axes[0], axes[1], axes[2])).transposed()
    center = Vector(center)
    corners = {}
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                corners[(sx, sy, sz)] = center + m @ Vector((sx * half[0], sy * half[1], sz * half[2]))
    long_axis = max(range(3), key=lambda i: half[i])
    faces = []
    for axis in range(3):
        for sign in (-1, 1):
            a, c = [i for i in range(3) if i != axis]
            quad = []
            for p, q in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                key = [0, 0, 0]
                key[axis], key[a], key[c] = sign, p, q
                quad.append(tuple(key))
            if sign < 0:
                quad.reverse()
            pts = [corners[k] for k in quad]
            locs = [Vector((k[0] * half[0], k[1] * half[1], k[2] * half[2])) for k in quad]
            if uv_fn is not None:
                uvs = [uv_fn(axis, sign, l) for l in locs]
            elif uv == "world":
                uvs = [world_uv(p, m @ Vector([1 if i == axis else 0 for i in range(3)]), rep) for p in pts]
            else:
                if axis == long_axis:  # end grain: across the two others
                    uvs = [(l[a] / rep, l[c] / rep) for l in locs]
                else:
                    other = [i for i in range(3) if i not in (axis, long_axis)][0]
                    uvs = [(l[other] / rep, l[long_axis] / rep) for l in locs]
            fm = (mats or {}).get((axis, sign), mat)
            faces.append(b.face(pts, uvs, fm))
    # ordered (not set) element lists: the output must be byte-identical run to run
    bmesh.ops.remove_doubles(b.bm, verts=list(dict.fromkeys(v for f in faces for v in f.verts)), dist=1e-6)
    if bevel > 0:
        edges = list(dict.fromkeys(e for f in faces if f.is_valid for e in f.edges))
        bmesh.ops.bevel(b.bm, geom=edges, offset=bevel, segments=1, affect="EDGES", profile=0.5)
    return faces


def superquad(b, center, axes, half, e, mat, rep, nu=28, nv=12):
    """A smooth superellipsoid (e -> 0: a box with round edges; 1: an ellipsoid) for soft furnishings."""
    sp = lambda c, x: math.copysign(abs(c) ** x, c)
    center = Vector(center)

    def pt(v, u):
        cx, cy, cz = sp(math.cos(v), e) * sp(math.cos(u), e), sp(math.cos(v), e) * sp(math.sin(u), e), sp(math.sin(v), e)
        return center + axes[0] * (cx * half[0]) + axes[1] * (cy * half[1]) + axes[2] * (cz * half[2]), (cx, cy, cz)

    rings = []
    for i in range(1, nv):
        v = -math.pi / 2 + math.pi * i / nv
        rings.append([pt(v, -math.pi + 2 * math.pi * j / nu) for j in range(nu)])
    uv = lambda c: ((c[0] * half[0] + c[2] * half[2] * 0.3) / rep, (c[1] * half[1] + c[2] * half[2]) / rep)
    vs = [[b.bm.verts.new(p) for p, _ in ring] for ring in rings]
    mi = b.mat(mat)

    def add(verts, cs):
        f = b.bm.faces.new(verts)
        f.material_index, f.smooth = mi, True
        for loop, c in zip(f.loops, cs):
            loop[b.uv].uv = uv(c)

    for i in range(len(rings) - 1):
        for j in range(nu):
            j1 = (j + 1) % nu
            add([vs[i][j], vs[i][j1], vs[i + 1][j1], vs[i + 1][j]],
                [rings[i][j][1], rings[i][j1][1], rings[i + 1][j1][1], rings[i + 1][j][1]])
    for ring, vring, z in ((rings[0], vs[0], -1), (rings[-1], vs[-1], 1)):
        pole = b.bm.verts.new(center + axes[2] * (z * half[2]))
        for j in range(nu):
            j1 = (j + 1) % nu
            add([vring[j1], vring[j], pole] if z < 0 else [vring[j], vring[j1], pole],
                [ring[j1][1], ring[j][1], (0, 0, z)] if z < 0 else [ring[j][1], ring[j1][1], (0, 0, z)])


def world_uv(p, n, rep):
    n = Vector((abs(n.x), abs(n.y), abs(n.z)))
    if n.z >= n.x and n.z >= n.y:
        return (p.x / rep, p.y / rep)
    if n.x >= n.y:
        return (p.y / rep, p.z / rep)
    return (p.x / rep, p.z / rep)


def reproject_world(obj_builder, mat, rep):
    """Box-projects every face of one material from world coordinates (after bevels)."""
    idx = obj_builder.mat(mat)
    for f in obj_builder.bm.faces:
        if f.material_index != idx:
            continue
        f.normal_update()
        for loop in f.loops:
            loop[obj_builder.uv].uv = world_uv(loop.vert.co, f.normal, rep)


def gbox(b, lo, hi, mat, **kw):
    """Axis-aligned box from glTF min/max corners."""
    p0, p1 = G(*lo), G(*hi)
    c = (p0 + p1) / 2
    h = [abs(p1[i] - p0[i]) / 2 for i in range(3)]
    return box(b, c, (Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))), h, mat, **kw)


class Log:
    """A deterministic, slightly irregular round log along a straight axis (Blender coords).
    The same Log can emit several pieces (cut around openings) that line up exactly."""

    def __init__(self, p0, p1, r, rng, taper=0.012, bow=0.012, sides=16, butt=1):
        self.p0, self.p1 = Vector(p0), Vector(p1)
        self.length = (self.p1 - self.p0).length
        self.dir = (self.p1 - self.p0).normalized()
        up = Vector((0, 0, 1))
        if abs(self.dir.dot(up)) > 0.9:
            up = Vector((1, 0, 0))
        self.n = self.dir.cross(up).normalized()      # horizontal, perpendicular to the log
        self.w = self.n.cross(self.dir).normalized()  # up-ish
        self.r = r
        self.taper = taper * butt
        self.bow = rng.uniform(-bow, bow)
        self.sides = sides
        self.a2, self.p2 = rng.uniform(0.01, 0.025), rng.uniform(0, 6.28)
        self.a3, self.p3 = rng.uniform(0.005, 0.015), rng.uniform(0, 6.28)
        self.drift = rng.uniform(-1.5, 1.5)
        self.u0, self.v0 = rng.random(), rng.random()
        self.cap_rot = rng.uniform(0, 6.28)
        self.seed = rng.random() * 1000

    def radius(self, t, theta):
        base = self.r + self.taper * (0.5 - t) * 2
        wob = 1 + self.a2 * math.sin(2 * theta + self.p2 + self.drift * t) + self.a3 * math.sin(3 * theta + self.p3)
        ring = 1 + 0.004 * math.sin(self.seed + t * 37.0)
        return base * wob * ring

    def point(self, t, theta):
        c = self.p0 + (self.p1 - self.p0) * t + self.n * (self.bow * math.sin(math.pi * t))
        r = self.radius(t, theta)
        return c + self.n * (math.cos(theta) * r) + self.w * (math.sin(theta) * r)

    def piece(self, b, t0, t1, cap0=True, cap1=True, wall="LogWall", end="LogEnd", ring_step=0.40, end_uv=0.36):
        n = self.sides
        steps = max(1, int(math.ceil((t1 - t0) * self.length / ring_step)))
        ts = [t0 + (t1 - t0) * i / steps for i in range(steps + 1)]
        rings = [[self.point(t, 2 * math.pi * j / n) for j in range(n)] for t in ts]
        vs = [[b.bm.verts.new(p) for p in ring] for ring in rings]
        mi = b.mat(wall)
        for i in range(steps):
            for j in range(n):
                j1 = (j + 1) % n
                f = b.bm.faces.new((vs[i][j], vs[i][j1], vs[i + 1][j1], vs[i + 1][j]))
                f.material_index = mi
                f.smooth = True
                uvs = ((j / n, ts[i]), ((j + 1) / n, ts[i]), ((j + 1) / n, ts[i + 1]), (j / n, ts[i + 1]))
                for loop, (uu, tt) in zip(f.loops, uvs):
                    loop[b.uv].uv = (self.u0 + uu, self.v0 + tt * self.length / LOG_V)
        me = b.mat(end)
        for ring, t, cap in ((vs[0], ts[0], cap0), (vs[-1], ts[-1], cap1)):
            if not cap:
                continue
            f = b.bm.faces.new(ring if t == ts[-1] else list(reversed(ring)))
            f.material_index = me
            f.smooth = False
            rmax = max(self.radius(t, 2 * math.pi * j / n) for j in range(n))
            for loop in f.loops:
                j = ring.index(loop.vert)
                th = 2 * math.pi * j / n
                rr = self.radius(t, th) / rmax * end_uv
                loop[b.uv].uv = (0.52 + rr * math.cos(th + self.cap_rot), 0.50 + rr * math.sin(th + self.cap_rot))
            for e in f.edges:
                e.smooth = False


def log_pieces(b, log, cuts, along0, along1, roof_cut=False):
    """Emits a log minus the cut intervals (in along-axis coordinates between along0 and along1)."""
    keep = []
    a = along0
    for c0, c1 in sorted(cuts):
        if c1 <= a or c0 >= along1:
            continue
        if c0 > a:
            keep.append((a, c0))
        a = max(a, c1)
    if a < along1:
        keep.append((a, along1))
    span = along1 - along0
    for k0, k1 in keep:
        if not roof_cut:
            log.piece(b, (k0 - along0) / span, (k1 - along0) / span)
        else:
            tmp = Builder("tmp")
            log.piece(tmp, (k0 - along0) / span, (k1 - along0) / span)
            cut_by_roof(tmp)
            merge(tmp, b)


def cut_by_roof(tmp):
    bm = tmp.bm
    for zs in (1, -1):
        # glTF plane y = RIDGE_UNDER + CUT_OFF - SLOPE * zs * z  ->  normal (0, 1, SLOPE*zs) in glTF
        co = G(0, RIDGE_UNDER + CUT_OFF, 0)
        no = G(0, 1, SLOPE * zs).normalized()
        if not bm.verts:
            return
        res = bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces), plane_co=co,
                                     plane_no=no, clear_outer=True)
        bnd = [e for e in bm.edges if e.is_boundary]
        if bnd:
            filled = bmesh.ops.holes_fill(bm, edges=bnd, sides=0)
            for f in filled["faces"]:
                f.material_index = tmp.mat("LogWall")
                f.smooth = False


def merge(src, dst):
    """Appends src's bmesh into dst (materials by name)."""
    vmap = {}
    for v in src.bm.verts:
        vmap[v] = dst.bm.verts.new(v.co)
    for f in src.bm.faces:
        nf = dst.bm.faces.new([vmap[v] for v in f.verts])
        nf.material_index = dst.mat(src.mats[f.material_index])
        nf.smooth = f.smooth
        for l0, l1 in zip(f.loops, nf.loops):
            l1[dst.uv].uv = l0[src.uv].uv
    for e in src.bm.edges:
        if not e.smooth:
            ne = dst.bm.edges.get([vmap[v] for v in e.verts])
            if ne:
                ne.smooth = False
    src.bm.free()


def cylinder(b, p0, p1, r, mat, end_mat=None, sides=8, rep=0.5, caps=True):
    log = Log(p0, p1, r, random.Random(round(p0.x * 1000 + p0.y * 37 + p0.z * 7)), taper=0, bow=0, sides=sides)
    log.a2 = log.a3 = 0
    log.piece(b, 0, 1, caps, caps, wall=mat, end=end_mat or mat, ring_step=10)


# ---------------------------------------------------------------------------------------------------------------------
# Materials


MATERIALS = {}


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
        sys.exit(f"missing {path}: run `python3 Examples/Forest/Tools/fetch_assets.py --only cabin` (and git lfs pull)")
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = colorspace
    return img


def pbr_material(name, tex, double=False, tint=None):
    mat = bpy.data.materials.new(name)
    try:
        mat.use_nodes = True
    except Exception:
        pass
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
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
    return mat


def glass_material():
    mat = bpy.data.materials.new("Glass")
    try:
        mat.use_nodes = True
    except Exception:
        pass
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.42, 0.50, 0.46, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.06
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Alpha"].default_value = 0.25
    try:
        mat.surface_render_method = "BLENDED"
    except Exception:
        mat.blend_method = "BLEND"
    mat.use_backface_culling = False
    return mat


def make_materials():
    for name, tex in CABIN_TEXTURES.items():
        MATERIALS[name] = pbr_material(name, tex, double=name in ("Fabric",), tint=TINTS.get(name))
    MATERIALS["Glass"] = glass_material()


# ---------------------------------------------------------------------------------------------------------------------
# The cabin


def walls(rng):
    w = Builder("Walls")
    # Front/back (x) walls: full-length logs; side (z) walls: half a course up. Corners cross (saddle notches implied by
    # the overlap); ends project END past the crossing wall.
    x_cuts = {
        +1: [(DOOR_X[0] - JAMB + 0.02, DOOR_X[1] + JAMB - 0.02, 0.0, 2.60),
             (W1_X[0] - 0.04, W1_X[1] + 0.04, 1.30, 2.10)],
        -1: [(W2_X[0] - 0.04, W2_X[1] + 0.04, 1.30, 2.10)],
    }
    z_cuts = {
        +1: [(W3_Z[0] - 0.04, W3_Z[1] + 0.04, 1.45, 2.20)],
        -1: [(FIRE_Z[0], FIRE_Z[1], 0.0, 1.90)],
    }
    def ends(k):
        # neighbouring courses alternate long/short by >= 3 cm: a visible end cap never lies inside the next log
        e = END + (0.025 if k % 2 == 0 else -0.015)
        return e + rng.uniform(-0.007, 0.007), e + rng.uniform(-0.007, 0.007)

    for zs in (1, -1):
        for k in range(X_COURSES + 1):
            y = Y0_X + k * S
            e0, e1 = ends(k)
            log = Log(G(-OX - e0, y, zs * WZ), G(OX + e1, y, zs * WZ), R + rng.uniform(-0.004, 0.008), rng,
                      butt=1 if (k + (zs > 0)) % 2 else -1)
            cuts = [(c0, c1) for c0, c1, y0, y1 in x_cuts[zs] if y0 <= y <= y1]
            log_pieces(w, log, cuts, -OX - e0, OX + e1, roof_cut=y + R > EAVE_TOP - 0.02)
    for xs in (1, -1):
        k = 0
        while True:
            y = Y0_Z + k * S
            if y - R > RIDGE_UNDER + CUT_OFF:
                break
            # along-axis = glTF z, from -OZ-END to OZ-END. Blender: G(x, y, z)
            e0, e1 = ends(k + 1)
            log = Log(G(xs * WX, y, -OZ - e0), G(xs * WX, y, OZ + e1), R + rng.uniform(-0.004, 0.008), rng,
                      butt=1 if (k + (xs > 0)) % 2 else -1)
            cuts = [(c0, c1) for c0, c1, y0, y1 in z_cuts[xs] if y0 <= y <= y1]
            log_pieces(w, log, cuts, -OZ - e0, OZ + e1, roof_cut=y + R > EAVE_TOP - 0.3)
            k += 1
    return w.finish()


def beams(rng):
    b = Builder("Beams")
    # Purlins and the ridge log (along x, under the roof), and two tie beams across the room at the eaves.
    for zs in (1, -1):
        y = roof_under(1.4) - R + 0.03
        log = Log(G(-ROOF_X, y, zs * 1.4), G(ROOF_X, y, zs * 1.4), R, rng, taper=0.008)
        log.piece(b, 0, 1)
    y = RIDGE_UNDER - R + 0.03
    log = Log(G(-OX + 0.01, y, 0), G(ROOF_X, y, 0), R + 0.01, rng, taper=0.008)
    log.piece(b, 0, 1)
    for x in (-1.5, 1.5):
        log = Log(G(x, EAVE_TOP - 0.13, -WZ - 0.05), G(x, EAVE_TOP - 0.13, WZ + 0.05), 0.13, rng, taper=0.006)
        log.piece(b, 0, 1)
    return b.finish()


def roof():
    b = Builder("Roof")
    for zs in (1, -1):
        d = G(0, -SIN_A, zs * COS_A)   # down the slope
        n = G(0, COS_A, zs * SIN_A)    # up, out of the roof
        L = ROOF_Z / COS_A
        ridge = G(0, RIDGE_UNDER, 0)
        center = ridge + d * (L / 2) + n * (ROOF_T / 2)
        ax = Vector((1, 0, 0))
        axes = (ax, d, n)
        half = (ROOF_X, L / 2, ROOF_T / 2)

        def uv_fn(axis, sign, l):
            if axis == 2 and sign > 0:      # shingles: courses along x, v up the slope
                return (l[0] / ROOF_REP, (L / 2 - l[1]) / ROOF_REP)
            if axis == 2:                   # decking underside: boards down the slope
                return (l[1] / FLOOR_REP, l[0] / FLOOR_REP)
            if axis == 1:                   # eave fascia / ridge end: grain along x
                return (l[2] / PLANK_REP, l[0] / PLANK_REP)
            return (l[2] / PLANK_REP, l[1] / PLANK_REP)  # barge boards: grain down the slope

        mats = {(2, 1): "Roof", (2, -1): "Floor"}
        box(b, center, axes, half, "DoorWood", mats=mats, uv_fn=uv_fn)
        # ridge cap board on this side
        top_ridge = G(0, RIDGE_UNDER + ROOF_T / COS_A, 0)
        cap_c = top_ridge + d * 0.10 + n * 0.018
        box(b, cap_c, axes, (ROOF_X + 0.02, 0.11, 0.018), "DoorWood", uv="grain")
    return b.finish()


def floor():
    b = Builder("Floor")

    def uv_fn(axis, sign, l):
        return ((l[0]) / FLOOR_REP, (l[1]) / FLOOR_REP) if axis == 2 else (l[0] / FLOOR_REP, l[2] / FLOOR_REP)

    lo, hi = G(-IN_X - 0.05, 0.38, -IN_Z - 0.05), G(IN_X + 0.05, FLOOR, IN_Z + 0.05)
    c = (lo + hi) / 2
    h = [abs(hi[i] - lo[i]) / 2 for i in range(3)]
    box(b, c, (Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))), h, "Floor",
        uv_fn=lambda axis, sign, l: ((c.x + l[0]) / FLOOR_REP, (c.y + l[1]) / FLOOR_REP))
    return b.finish()


def footing():
    b = Builder("Footing")
    for zs in (1, -1):
        gbox(b, (-OX - 0.08, -0.30, zs * WZ - 0.21), (OX + 0.08, 0.28, zs * WZ + 0.21), "Stone", uv="world",
             rep=STONE_REP, bevel=0.03)
    for xs in (1, -1):
        gbox(b, (xs * WX - 0.21, -0.30, -WZ - 0.17), (xs * WX + 0.21, Y0_Z - R + 0.03, WZ + 0.17), "Stone", uv="world",
             rep=STONE_REP, bevel=0.03)
    # under the threshold, and two steps up to it (rises 0.15, 0.15, 0.16)
    gbox(b, (DOOR_X[0] - JAMB, 0.0, WZ - 0.20), (DOOR_X[1] + JAMB, FLOOR - 0.05, WZ + 0.20), "Stone", uv="world",
         rep=STONE_REP)
    gbox(b, (DOOR_X[0] - 0.20, -0.20, OZ), (DOOR_X[1] + 0.20, 0.30, OZ + 0.42), "Stone", uv="world",
         rep=STONE_REP, bevel=0.03)
    gbox(b, (DOOR_X[0] - 0.30, -0.20, OZ + 0.37), (DOOR_X[1] + 0.30, 0.15, OZ + 0.78), "Stone", uv="world",
         rep=STONE_REP, bevel=0.03)
    reproject_world(b, "Stone", STONE_REP)
    return b.finish()


FIRE_FRONT = -IN_X + 0.25      # the surround projects 0.25 m into the room
CHIM_IN = -OX + 0.02           # chimney mass starts at the wall's outer face (overlapping it slightly)
HEARTH_TOP = FLOOR + 0.15
OPEN_TOP = HEARTH_TOP + 0.75
SURROUND_TOP = 1.95
MANTEL = (OPEN_TOP + 0.02, OPEN_TOP + 0.25)
MANTEL_X = FIRE_FRONT + 0.06   # the beam (0.30 deep) projects 0.21 m in front of the surround


def fireplace(rng):
    b = Builder("Fireplace")
    st = dict(uv="world", rep=STONE_REP)
    # raised hearth slab (also the firebox floor)
    gbox(b, (-OX, 0.38, -1.10), (FIRE_FRONT + 0.50, HEARTH_TOP, 1.10), "Stone", bevel=0.02, **st)
    # jambs and the lintel block around the 0.9 x 0.75 x 0.53 firebox
    for zs in (1, -1):
        z0, z1 = sorted((zs * 0.45, zs * 0.92))
        gbox(b, (-OX, HEARTH_TOP - 0.01, z0), (FIRE_FRONT, SURROUND_TOP, z1), "Stone", **st)
    gbox(b, (-OX, OPEN_TOP, -0.45), (FIRE_FRONT, SURROUND_TOP, 0.45), "Stone", **st)
    reproject_world(b, "Stone", STONE_REP)
    # the mantel: a heavy hewn beam
    log_rng = random.Random(7)
    mz = 1.05
    c = G(MANTEL_X, (MANTEL[0] + MANTEL[1]) / 2, 0)
    hy = (MANTEL[1] - MANTEL[0]) / 2

    def mantel_uv(axis, sign, l):
        if axis == 0:  # the beam's ends: end grain
            return (0.52 + l[1] / 0.15 * 0.3, 0.50 + l[2] / hy * 0.3 * hy / 0.15)
        return ((l[1] if axis != 1 else l[2]) / LOG_U, l[0] / LOG_V)

    box(b, c, (Vector((0, -1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1))), (mz, 0.15, hy), "LogWall", uv_fn=mantel_uv,
        mats={(0, 1): "LogEnd", (0, -1): "LogEnd"})
    return b.finish()


def chimney():
    b = Builder("Chimney")
    st = dict(uv="world", rep=STONE_REP)
    x_out = -OX - 0.95
    gbox(b, (x_out, -0.25, -0.95), (CHIM_IN, 2.35, 0.95), "Stone", **st)
    # shoulder: a tapered block from the base to the stack
    f = gbox(b, (x_out, 2.35, -0.95), (CHIM_IN, 2.95, 0.95), "Stone", **st)
    for v in {v for face in f for v in face.verts}:
        if v.co.z > 2.6:   # Blender z = glTF y
            v.co.x = max(v.co.x, -OX - 0.70)
            v.co.y = max(min(v.co.y, 0.55), -0.55)
    stack_x0 = -OX - 0.70
    gbox(b, (stack_x0, 2.95, -0.55), (CHIM_IN, 5.50, 0.55), "Stone", **st)
    # the cap: a corbelled ring around a 0.24 m flue
    fx = (stack_x0 + CHIM_IN) / 2
    x0, x1, z0 = stack_x0 - 0.06, CHIM_IN + 0.06, 0.61
    gbox(b, (x0, 5.50, -z0), (fx - 0.12, 5.78, z0), "Stone", **st)
    gbox(b, (fx + 0.12, 5.50, -z0), (x1, 5.78, z0), "Stone", **st)
    gbox(b, (fx - 0.12, 5.50, -z0), (fx + 0.12, 5.78, -0.12), "Stone", **st)
    gbox(b, (fx - 0.12, 5.50, 0.12), (fx + 0.12, 5.78, z0), "Stone", **st)
    reproject_world(b, "Stone", STONE_REP)
    return b.finish(), fx


def door():
    b = Builder("Door")
    hinge = G(DOOR_X[1] - 0.01, FLOOR, IN_Z)
    w, h, t = DOOR_X[1] - DOOR_X[0] - 0.02, 1.925, 0.045
    ax = (Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, -1, 0)))  # local x, up, toward the outside (glTF +z)

    def lb(x0, x1, y0, y1, z0, z1, rot=0.0):
        c = hinge + G((x0 + x1) / 2, (y0 + y1) / 2 + 0.015, (z0 + z1) / 2) - G(0, 0, 0)
        if rot:
            r = Matrix.Rotation(rot, 3, Vector((0, -1, 0)))  # about glTF +z
            axes = tuple(r @ a for a in (Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, -1, 0))))
        else:
            axes = (Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, -1, 0)))
        box(b, c, axes, ((x1 - x0) / 2, (y1 - y0) / 2, (z1 - z0) / 2), "DoorWood",
            uv_fn=lambda axis, sign, l: (l[0] / PLANK_REP + 0.3, l[1] / PLANK_REP) if not rot else
            (l[1] / PLANK_REP, l[0] / PLANK_REP))

    # the boards (local x from -w to 0: the free edge at -w, hinge at 0), outer face at +t
    lb(-w, 0, 0, h, 0, t)
    # ledges and a Z brace on the inner (room) face, short of the hinge so the door clears the jamb when open
    for y in (0.22, 1.56):
        lb(-w + 0.05, -0.07, y, y + 0.15, -0.028, 0.0)
    # the brace rises from the hinge side's bottom ledge to the free side's top ledge
    y0, y1 = 0.37, 1.56
    xh, xf = -0.14, -w + 0.12
    ln = math.hypot(xf - xh, y1 - y0)
    ang = math.atan2(y1 - y0, xf - xh)
    c = ((xh + xf) / 2, (y0 + y1) / 2)
    lb(c[0] - ln / 2 + 0.05, c[0] + ln / 2 - 0.05, c[1] - 0.06, c[1] + 0.06, -0.026, -0.002, rot=ang)
    # pull handles, inside and out
    for z0, z1 in ((t, t + 0.035), (-0.028 - 0.035, -0.028)):
        lb(-w + 0.06, -w + 0.10, 0.95, 1.20, z0, z1)
    obj = b.finish(origin=hinge)
    return obj


def door_frame():
    b = Builder("DoorFrame")
    zi, zo = IN_Z - 0.04, OZ + 0.04
    top = FLOOR + 1.95
    gbox(b, (DOOR_X[0] - JAMB, FLOOR + 0.012, zi), (DOOR_X[0], top, zo), "DoorWood")
    gbox(b, (DOOR_X[1], FLOOR + 0.012, zi), (DOOR_X[1] + JAMB, top, zo), "DoorWood")
    gbox(b, (DOOR_X[0] - JAMB, top, zi), (DOOR_X[1] + JAMB, Y0_X + 9 * S, zo), "DoorWood")
    gbox(b, (DOOR_X[0] - JAMB - 0.01, 0.40, zi - 0.02), (DOOR_X[1] + JAMB + 0.01, FLOOR + 0.012, zo + 0.06), "DoorWood")
    zs0, zs1 = IN_Z + 0.050, IN_Z + 0.075   # stops, just outside the closed door's outer face (IN_Z + 0.045)
    gbox(b, (DOOR_X[0], FLOOR + 0.012, zs0), (DOOR_X[0] + 0.03, top, zs1), "DoorWood")
    gbox(b, (DOOR_X[1] - 0.03, FLOOR + 0.012, zs0), (DOOR_X[1], top, zs1), "DoorWood")
    gbox(b, (DOOR_X[0] + 0.03, top - 0.03, zs0), (DOOR_X[1] - 0.03, top, zs1), "DoorWood")
    return b.finish()


def windows():
    frames = Builder("WindowFrames")
    glass = []
    # (wall axis, sign, clear along-wall interval, clear y, y of the uncut log centres below/above)
    specs = [
        ("x", 1, W1_X, (Y0_X + 3 * S + R + 0.03, Y0_X + 7 * S - R - 0.01), (Y0_X + 3 * S, Y0_X + 7 * S)),
        ("x", -1, W2_X, (Y0_X + 3 * S + R + 0.03, Y0_X + 7 * S - R - 0.01), (Y0_X + 3 * S, Y0_X + 7 * S)),
        ("z", 1, W3_Z, (Y0_Z + 3 * S + R + 0.03, Y0_Z + 7 * S - R - 0.01), (Y0_Z + 3 * S, Y0_Z + 7 * S)),
    ]
    for i, (axis, sign, (a0, a1), (y0, y1), (ylo, yhi)) in enumerate(specs):
        wc = sign * (WZ if axis == "x" else WX)  # wall centre line
        out = sign  # outward along the wall normal

        def wb(b0, b1, yy0, yy1, d0, d1, mat="DoorWood"):
            # along-wall b, height y, depth d (outward positive, relative to the wall centre)
            if axis == "x":
                return gbox(frames, (b0, yy0, wc + out * d0 if out > 0 else wc + out * d1),
                            (b1, yy1, wc + out * d1 if out > 0 else wc + out * d0), mat)
            return gbox(frames, (wc + out * d0 if out > 0 else wc + out * d1, yy0, b0),
                        (wc + out * d1 if out > 0 else wc + out * d0, yy1, b1), mat)

        dw = R + 0.04
        wb(a0 - JAMB, a0, y0, y1, -dw, dw)
        wb(a1, a1 + JAMB, y0, y1, -dw, dw)
        wb(a0 - JAMB - 0.04, a1 + JAMB + 0.04, ylo, y0, -dw, dw + 0.06)   # sill, projecting outside
        wb(a0 - JAMB, a1 + JAMB, y1, yhi, -dw, dw)                        # head
        # sash border, mullion cross
        s, d = 0.045, 0.03
        wb(a0, a1, y0, y0 + s, -d, d)
        wb(a0, a1, y1 - s, y1, -d, d)
        wb(a0, a0 + s, y0 + s, y1 - s, -d, d)
        wb(a1 - s, a1, y0 + s, y1 - s, -d, d)
        am, ym = (a0 + a1) / 2, (y0 + y1) / 2
        wb(am - 0.018, am + 0.018, y0 + s, y1 - s, -d + 0.004, d - 0.004)
        wb(a0 + s, a1 - s, ym - 0.018, ym + 0.018, -d + 0.008, d - 0.008)
        # glass: one double-sided quad in the wall's middle plane
        gname = "Glass" if i == 0 else f"Glass.{i:03d}"
        g = Builder(gname)
        if axis == "x":
            pts = [G(a0, y0, wc), G(a1, y0, wc), G(a1, y1, wc), G(a0, y1, wc)]
        else:
            pts = [G(wc, y0, a0), G(wc, y0, a1), G(wc, y1, a1), G(wc, y1, a0)]
        g.face(pts, [(0, 0), (1, 0), (1, 1), (0, 1)], "Glass")
        glass.append(g)
    objs = [frames.finish()]
    for g in glass:
        c = sum((v.co for v in g.bm.verts), Vector()) / len(g.bm.verts)
        objs.append(g.finish(origin=c))
    return objs


def rug():
    b = Builder("Rug")
    cx, cz, ra, rb = -1.30, 0.40, 1.00, 0.68
    n = 40
    y = FLOOR + 0.012
    top = [G(cx + ra * math.cos(2 * math.pi * i / n), y, cz + rb * math.sin(2 * math.pi * i / n)) for i in range(n)]
    bot = [G(cx + (ra + 0.01) * math.cos(2 * math.pi * i / n), FLOOR + 0.001, cz + (rb + 0.01) * math.sin(2 * math.pi * i / n))
           for i in range(n)]
    uv = lambda p: (p.x / FABRIC_REP[0], p.y / FABRIC_REP[1])
    b.face(top, [uv(p) for p in top], "Fabric")
    for i in range(n):
        j = (i + 1) % n
        q = [bot[i], bot[j], top[j], top[i]]
        b.face(q, [uv(p) for p in q], "Fabric")
    return b.finish(weld=True)


def firewood(rng):
    b = Builder("Firewood")
    x0 = -IN_X + 0.02
    z0, z1 = -IN_Z + 0.06, -1.38
    y = FLOOR
    row = 0
    while y < FLOOR + 0.62:
        r = 0.062
        z = z0 + r + (0.03 if row % 2 else 0)
        while z + r <= z1:
            rr = rng.uniform(0.048, 0.07)
            ln = rng.uniform(0.36, 0.44)
            xa = x0 + rng.uniform(0.0, 0.03)
            yy = y + rr + rng.uniform(-0.004, 0.004)
            log = Log(G(xa, yy, z), G(xa + ln, yy, z), rr, rng, taper=0.004, bow=0.006, sides=7)
            log.piece(b, 0, 1, ring_step=1.0, end_uv=0.30)
            z += 2 * r + rng.uniform(0.0, 0.01)
        y += 0.105
        row += 1
    # two loose pieces on the floor by the hearth
    for (xa, za, ang) in ((-2.52, -1.30, 0.12), (-2.45, -1.13, -0.30)):
        rr = 0.06
        d = Vector((math.cos(ang), 0, math.sin(ang))) * 0.42
        log = Log(G(xa, FLOOR + rr, za), G(xa + d.x, FLOOR + rr, za + d.z), rr, rng, taper=0.004, bow=0.0, sides=7)
        log.piece(b, 0, 1, ring_step=1.0, end_uv=0.30)
    return b.finish()


def shelves():
    b = Builder("Shelves")
    # a plank shelf on two brackets, front wall west of the window
    zi = IN_Z + 0.02
    gbox(b, (-2.75, 1.98, zi - 0.26), (-1.78, 2.02, zi), "DoorWood")
    for x in (-2.55, -1.98):
        gbox(b, (x - 0.02, 1.76, zi - 0.05), (x + 0.02, 1.98, zi), "DoorWood")
        gbox(b, (x - 0.02, 1.93, zi - 0.22), (x + 0.02, 1.98, zi - 0.05), "DoorWood")
    # a peg rail by the door
    gbox(b, (DOOR_X[1] + 0.22, 1.95, zi - 0.05), (DOOR_X[1] + 0.98, 2.07, zi), "DoorWood")
    for i in range(4):
        x = DOOR_X[1] + 0.32 + i * 0.18
        cylinder(b, G(x, 2.01, zi - 0.05), G(x, 2.04, zi - 0.16), 0.014, "DoorWood", sides=8)
    return b.finish()


def bench():
    """A plank bench against the front wall, between the window and the door."""
    b = Builder("Bench")
    zi = IN_Z + 0.02
    x0, x1 = W1_X[1] + JAMB + 0.12, DOOR_X[0] - JAMB - 0.10
    seat = FLOOR + 0.44
    gbox(b, (x0, seat, zi - 0.36), (x1, seat + 0.05, zi - 0.02), "DoorWood")
    for x in (x0 + 0.10, x1 - 0.14):
        gbox(b, (x, FLOOR, zi - 0.33), (x + 0.045, seat, zi - 0.05), "DoorWood")
    gbox(b, (x0 + 0.145, FLOOR + 0.12, zi - 0.22), (x1 - 0.14, FLOOR + 0.20, zi - 0.18), "DoorWood")
    return b.finish()


def candle(mantel_x):
    b = Builder("CandleHolder")
    x, z = mantel_x, 0.62
    y = MANTEL[1]
    cylinder(b, G(x, y, z), G(x, y + 0.025, z), 0.045, "DoorWood", sides=12)
    cylinder(b, G(x, y + 0.025, z), G(x, y + 0.14, z), 0.021, "Linen", sides=10)
    obj = b.finish()
    return obj, (x, y + 0.155, z)


def bedding(bed):
    """Mattress, pillow and a draped wool blanket on the iron bed (its spring deck measured from the scan)."""
    b = Builder("Bedding")
    loc, yaw, deck = bed
    hw, h0, h1 = 0.405, -0.90, 0.92   # mattress half width and its extent along the bed (head at +h, model space)
    t = 0.15
    rot = Matrix.Rotation(yaw, 3, "Z")
    base = G(loc[0], deck, loc[2])

    def P(x, y, z):  # model space (x across, y along toward the head, z up) -> Blender
        return base + rot @ Vector((x, y, z))

    ax = (rot @ Vector((1, 0, 0)), rot @ Vector((0, 1, 0)), Vector((0, 0, 1)))
    superquad(b, P(0, (h0 + h1) / 2, t / 2), ax, (hw, (h1 - h0) / 2, t / 2), 0.18, "Linen", LINEN_REP)
    superquad(b, P(0, h1 - 0.22, t + 0.055), ax, (0.31, 0.18, 0.065), 0.42, "Linen", LINEN_REP)
    # the blanket: a sheet over the top from the foot to below the pillow, hanging down both sides and the foot
    nx, ny = 14, 18
    fold = 0.34
    y_end = h1 - 0.48
    xs = [i / nx for i in range(nx + 1)]
    ys = [j / ny for j in range(ny + 1)]
    grid = []
    for j, v in enumerate(ys):
        yy = h0 - 0.06 + (y_end - h0 + 0.06) * v
        row = []
        for i, u in enumerate(xs):
            # u: 0..1 across; the outer 25% each side hang down
            s = (u - 0.5) * 2  # -1..1
            if abs(s) <= 0.62:
                x = s / 0.62 * (hw + 0.025)
                z = t + 0.025 + 0.012 * math.sin(yy * 9 + x * 3)
            else:
                k = (abs(s) - 0.62) / 0.38
                x = math.copysign(hw + 0.025 + 0.04 * k + 0.012 * math.sin(yy * 14), s)
                z = t + 0.025 - fold * k + 0.01 * math.sin(yy * 23)
            row.append(P(x, yy, z))
        grid.append(row)
    # the foot drop
    foot = []
    for i, u in enumerate(xs):
        s = (u - 0.5) * 2
        p = grid[0][i]
        foot.append(p + rot @ Vector((0, -0.04, -fold + 0.06)))
    uvf = lambda i, j: (xs[i] * 1.6 / FABRIC_REP[0], ys[j] * 1.9 / FABRIC_REP[1])
    for j in range(ny):
        for i in range(nx):
            q = [grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]]
            b.face(q, [uvf(i, j), uvf(i + 1, j), uvf(i + 1, j + 1), uvf(i, j + 1)], "Fabric", smooth=True)
    for i in range(nx):
        q = [foot[i], foot[i + 1], grid[0][i + 1], grid[0][i]]
        b.face(q, [(xs[i] * 1.6 / FABRIC_REP[0], -0.4), (xs[i + 1] * 1.6 / FABRIC_REP[0], -0.4),
                   uvf(i + 1, 0), uvf(i, 0)], "Fabric", smooth=True)
    return b.finish()


def books(shelf, rng):
    """Leather-bound books on three of Shelf_01's boards (measured board tops)."""
    b = Builder("Books")
    loc, yaw = shelf
    rot = Matrix.Rotation(yaw, 3, "Z")
    base = G(loc[0], loc[1], loc[2])
    boards = [0.942, 1.224, 1.505]
    for bi, top in enumerate(boards):
        x = -0.41
        end = 0.41 - (0.0 if bi != 1 else 0.30)
        while x < end - 0.04:
            th = rng.uniform(0.025, 0.06)
            hh = rng.uniform(0.17, 0.235)
            dd = rng.uniform(0.13, 0.17)
            if x + th > end:
                break
            tilt = 0.0
            if rng.random() < 0.08 and x + 0.12 < end:
                tilt = rng.uniform(0.15, 0.3)
            c = Vector((x + th / 2 + math.sin(tilt) * hh / 2, -0.04 - dd / 2 - rng.uniform(0, 0.02),
                        top + hh / 2 * math.cos(tilt) + th / 2 * math.sin(tilt)))
            r = rot @ Matrix.Rotation(-tilt, 3, "Y")
            ax = (r @ Vector((1, 0, 0)), r @ Vector((0, 1, 0)), r @ Vector((0, 0, 1)))
            box(b, base + rot @ c, ax, (th / 2, dd / 2, hh / 2), "Leather", rep=LEATHER_REP)
            x += th + rng.uniform(0.0, 0.006) + (math.sin(tilt) * hh if tilt else 0)
    # a lying stack on the middle board's free end
    top = boards[1]
    zz = top
    for i in range(3):
        th = rng.uniform(0.03, 0.05)
        c = Vector((0.27, -0.12, zz + th / 2))
        r = rot @ Matrix.Rotation(rng.uniform(-0.15, 0.15), 3, "Z")
        ax = (r @ Vector((0, 1, 0)), r @ Vector((1, 0, 0)), Vector((0, 0, 1)))
        box(b, base + rot @ c, ax, (rng.uniform(0.075, 0.09), rng.uniform(0.10, 0.12), th / 2), "Leather",
            rep=LEATHER_REP)
        zz += th
    return b.finish()


# ---------------------------------------------------------------------------------------------------------------------
# Furniture sockets: (poly haven id, x, z, yaw degrees (about +Y; 0 = the scan as authored), surface y, extra tilt)
# The scans face glTF +Z as authored (a yaw of -90 faces -X, toward the fireplace). The socket's y is fitted so the scan
# rests on its surface (its lowest vertex = the surface).

TABLE = (-0.35, -1.45)
BED = (IN_X - 0.04 - 0.457, -IN_Z + 0.05 + 1.0)
SHELF = (0.85, -IN_Z + 0.03)

SOCKETS = [
    ("old_bed_frame", BED[0], BED[1], 0.0, FLOOR, None),
    ("wooden_table_02", TABLE[0], TABLE[1], 0.0, FLOOR, None),
    ("painted_wooden_chair_02", TABLE[0] + 0.12, TABLE[1] - 0.56, 6.0, FLOOR, None),
    ("painted_wooden_chair_02", TABLE[0] - 0.86, TABLE[1] + 0.12, 74.0, FLOOR, None),
    ("Rockingchair_01", -1.45, 0.62, -112.0, FLOOR, None),
    ("Shelf_01", SHELF[0], SHELF[1], 0.0, FLOOR, None),
    ("wooden_lantern_01", TABLE[0] + 0.22, TABLE[1] + 0.08, 25.0, "table", None),
    ("wooden_bucket_01", -2.42, 1.55, 35.0, FLOOR, None),
    ("pot_enamel_01", FIRE_FRONT + 0.27, 0.74, 20.0, HEARTH_TOP, None),
    ("wooden_axe", -IN_X + 0.20, -1.22, 90.0, FLOOR, ("z", 14.0)),
]


def import_prop(pid):
    path = os.path.join(PROPS, pid, f"{pid}_1k.gltf")
    if not os.path.exists(path):
        sys.exit(f"missing {path}: run `python3 Examples/Forest/Tools/fetch_assets.py --only props` (and git lfs pull)")
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    return [o for o in new if o.parent is None], new


def place_sockets(table_top):
    """Creates the Place_ empties, importing each scan to fit it; returns (sockets, imported objects, info)."""
    sockets, imported, info = [], [], {}
    coll = bpy.data.collections.new("Furniture")
    bpy.context.scene.collection.children.link(coll)
    for pid, x, z, yaw, surface, tilt in SOCKETS:
        roots, objs = import_prop(pid)
        for o in objs:
            for c in o.users_collection:
                c.objects.unlink(o)
            coll.objects.link(o)
        emp = bpy.data.objects.new(f"Place_{pid}", None)
        bpy.context.scene.collection.objects.link(emp)
        emp.empty_display_type = "ARROWS"
        emp.empty_display_size = 0.3
        rot = Matrix.Rotation(math.radians(yaw), 4, "Z")
        if tilt:
            axis, deg = tilt
            # lean: rotate about the world (glTF) z axis = Blender -Y, after the yaw
            rot = Matrix.Rotation(math.radians(deg), 4, Vector((0, -1, 0)) if axis == "z" else Vector((1, 0, 0))) @ rot
        surf = table_top if surface == "table" else surface
        # lowest point of the scan under this rotation
        low = min((rot @ (o.matrix_world @ v.co)).z for o in objs if o.type == "MESH" for v in o.data.vertices)
        emp.matrix_world = Matrix.Translation(G(x, surf - low, z)) @ rot
        for o in roots:
            o.parent = emp
            o.matrix_parent_inverse = Matrix.Identity(4)
        sockets.append(emp)
        imported += objs
        info[emp.name] = objs
    return sockets, imported, coll


def measure(pid, fn):
    roots, objs = import_prop(pid)
    dg = bpy.context.evaluated_depsgraph_get()
    try:
        return fn(dg)
    finally:
        for o in objs:
            bpy.data.objects.remove(o, do_unlink=True)


def table_top_height():
    def fn(dg):
        hit, loc, *_ = bpy.context.scene.ray_cast(dg, Vector((0.1, 0.05, 3.0)), Vector((0, 0, -1)))
        return loc.z
    return FLOOR + measure("wooden_table_02", fn)


def bed_deck_height():
    # the spring deck is a wire mesh: take the highest hit over a grid of rays inside the frame
    def fn(dg):
        best = 0.0
        for x in (-0.3, -0.15, 0.0, 0.15, 0.3):
            for y in (-0.8, -0.4, 0.0, 0.4, 0.8):
                hit, loc, *_ = bpy.context.scene.ray_cast(dg, Vector((x, y, 1.0)), Vector((0, 0, -1)))
                if hit and loc.z < 0.7:
                    best = max(best, loc.z)
        return best
    return FLOOR + max(measure("old_bed_frame", fn), 0.45)


# ---------------------------------------------------------------------------------------------------------------------
# Empties


def empty(name, loc, size=0.25, scale=None, matrix=None):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = "PLAIN_AXES"
    e.empty_display_size = size
    bpy.context.scene.collection.objects.link(e)
    if matrix is not None:
        e.matrix_world = matrix
    else:
        e.location = loc
    if scale is not None:
        e.scale = scale
    return e


def look_matrix(eye_g, target_g):
    """An empty whose glTF -Z looks from eye to target (glTF +Y up): Blender local +Y forward, +Z up."""
    eye, tgt = G(*eye_g), G(*target_g)
    f = (tgt - eye).normalized()
    right = f.cross(Vector((0, 0, 1))).normalized()
    up = right.cross(f).normalized()
    m = Matrix((right, f, up)).transposed().to_4x4()
    m.translation = eye
    return m


R9_EYE = (1.86, FLOOR + 0.80, -0.30)
R9_TARGET = (-3.0, FLOOR + 0.70, -0.55)


# ---------------------------------------------------------------------------------------------------------------------
# Export


def export(objs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    tmp = tempfile.mkdtemp(prefix="cabin-gltf-")
    path = os.path.join(tmp, "cabin.gltf")
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
    for m in gltf.get("materials", []):
        m.setdefault("pbrMetallicRoughness", {})["metallicFactor"] = 0
    gltf.get("asset", {})["generator"] = "Examples/Forest/Tools/cabin/build_cabin.py (Blender glTF exporter)"
    os.makedirs(OUT, exist_ok=True)
    shutil.copyfile(os.path.join(tmp, "cabin.bin"), os.path.join(OUT, "cabin.bin"))
    with open(os.path.join(OUT, "cabin.gltf"), "w", newline="\n") as f:
        json.dump(gltf, f, indent=1)
        f.write("\n")
    shutil.rmtree(tmp)
    return gltf


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons) if obj.type == "MESH" else 0


# ---------------------------------------------------------------------------------------------------------------------
# Previews (not part of the asset)


def setup_render(res=(1280, 720), samples=64):
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
    bg.inputs["Color"].default_value = (0.55, 0.68, 0.85, 1)
    bg.inputs["Strength"].default_value = 0.9
    if "PreviewSun" not in bpy.data.objects:
        sun = bpy.data.lights.new("PreviewSun", "SUN")
        sun.energy = 4.0
        sun.angle = math.radians(2)
        so = bpy.data.objects.new("PreviewSun", sun)
        so.rotation_euler = (math.radians(50), 0, math.radians(-35))
        sc.collection.objects.link(so)
        ground = bpy.data.meshes.new("PreviewGround")
        bm = bmesh.new()
        bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=30)
        bm.to_mesh(ground)
        bm.free()
        go = bpy.data.objects.new("PreviewGround", ground)
        gm = bpy.data.materials.new("PreviewGroundMat")
        try:
            gm.use_nodes = True
        except Exception:
            pass
        gb = next(n for n in gm.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        gb.inputs["Base Color"].default_value = (0.16, 0.2, 0.1, 1)
        gb.inputs["Roughness"].default_value = 0.95
        ground.materials.append(gm)
        sc.collection.objects.link(go)
        fire = bpy.data.lights.new("PreviewFire", "POINT")
        fire.energy = 40
        fire.color = (1.0, 0.55, 0.25)
        fire.shadow_soft_size = 0.2
        fo = bpy.data.objects.new("PreviewFire", fire)
        fo.location = G(-IN_X - 0.02, HEARTH_TOP + 0.25, 0)
        sc.collection.objects.link(fo)


def render(path, eye_g, target_g, lens=24, ortho=None, exposure=0.0):
    sc = bpy.context.scene
    cam = bpy.data.cameras.new("PreviewCam")
    co = bpy.data.objects.new("PreviewCam", cam)
    sc.collection.objects.link(co)
    if ortho:
        cam.type = "ORTHO"
        cam.ortho_scale = ortho
    cam.lens = lens
    cam.clip_start = 0.05
    m = look_matrix(eye_g, target_g)
    # Blender cameras look down local -Z with +Y up: from the look matrix (X right, Y forward, Z up)
    r, f, u = m.col[0].to_3d(), m.col[1].to_3d(), m.col[2].to_3d()
    cm = Matrix((r, u, -f)).transposed().to_4x4()
    cm.translation = m.translation
    co.matrix_world = cm
    sc.camera = co
    sc.view_settings.exposure = exposure
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(co, do_unlink=True)
    print("render", path)


def render_from_empty(path, emp, lens=18, exposure=0.0):
    m = emp.matrix_world
    eye = m.translation
    f = m.col[1].to_3d()
    tgt = eye + f
    # back to glTF coords for render()
    render(path, (eye.x, eye.z, -eye.y), (tgt.x, tgt.z, -tgt.y), lens=lens, exposure=exposure)


def previews(out_dir, door_obj, cabin_objs, r9):
    os.makedirs(out_dir, exist_ok=True)
    setup_render()
    render(os.path.join(out_dir, "exterior_front.png"), (8.5, 2.4, 10.5), (0.0, 2.1, 0.0), lens=30)
    render(os.path.join(out_dir, "exterior_back.png"), (-10.5, 3.2, -8.0), (0.0, 2.3, 0.0), lens=30)
    door_obj.rotation_euler.z = math.radians(-80)
    render(os.path.join(out_dir, "interior_door.png"), (1.55, FLOOR + 1.30, 6.2), (0.9, FLOOR + 0.9, -1.5), lens=30,
           exposure=1.5)
    render(os.path.join(out_dir, "interior_wide.png"), (2.7, FLOOR + 1.7, 2.2), (-1.8, FLOOR + 0.9, -1.6), lens=16,
           exposure=1.5)
    render(os.path.join(out_dir, "interior_up.png"), (1.5, FLOOR + 1.0, 1.5), (-2.2, FLOOR + 3.2, -1.2), lens=14,
           exposure=1.5)
    render(os.path.join(out_dir, "bed.png"), (0.9, FLOOR + 1.5, 0.4), (2.5, FLOOR + 0.5, -1.4), lens=24, exposure=1.5)
    render(os.path.join(out_dir, "corner.png"), (5.2, 1.4, 4.6), (3.3, 1.2, 2.8), lens=35)
    if os.environ.get("CABIN_DETAIL"):
        details = {
            "roof_close": ((2.5, 4.6, 5.0), (1.0, 4.0, 1.5), 35, 0),
            "eave_under": ((2.0, 1.5, 4.4), (1.0, 3.3, 2.9), 24, 0),
            "rake": ((5.6, 2.2, 1.2), (3.5, 4.0, 0.3), 24, 0),
            "chimney_roof": ((-6.0, 5.8, 3.2), (-3.6, 4.9, 0.0), 35, 0),
            "to_door": ((-2.2, FLOOR + 1.5, -1.2), (1.6, FLOOR + 1.1, 2.5), 18, 2.5),
            "firebox": ((-1.5, FLOOR + 0.6, 0.15), (-3.1, FLOOR + 0.45, 0.0), 30, 2.5),
            "gable_inside": ((1.0, FLOOR + 1.6, 0.5), (3.0, 4.2, -0.3), 18, 2.5),
            "hearth": ((-0.7, FLOOR + 1.3, 0.2), (-2.8, FLOOR + 0.7, -0.3), 22, 2.5),
            "front_wall": ((0.6, FLOOR + 1.4, -0.8), (-0.4, FLOOR + 0.9, 2.5), 20, 2.5),
        }
        for name, (eye, tgt, lens, ex) in details.items():
            render(os.path.join(out_dir, f"{name}.png"), eye, tgt, lens=lens, exposure=ex)
    render_from_empty(os.path.join(out_dir, "r9.png"), r9, lens=17, exposure=1.5)
    door_obj.rotation_euler.z = 0
    hide = [o for o in cabin_objs if o.name in ("Roof", "Beams")]
    for o in hide:
        o.hide_render = True
    sun = bpy.data.objects["PreviewSun"]
    old = sun.rotation_euler.copy()
    sun.rotation_euler = (math.radians(15), 0, math.radians(-35))
    sc = bpy.context.scene
    sc.render.resolution_x, sc.render.resolution_y = 1100, 1100
    render(os.path.join(out_dir, "plan.png"), (-0.3, 12.0, 0.001), (-0.3, 0.0, 0.0), ortho=9.2)
    sc.render.resolution_x, sc.render.resolution_y = 1280, 720
    sun.rotation_euler = old
    for o in hide:
        o.hide_render = False


def verify(out_dir):
    """Re-imports the exported cabin into an empty scene, instances the scans at the sockets, renders."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, "cabin.gltf"))
    objs = list(bpy.data.objects)
    report = {}
    for o in objs:
        if o.type == "EMPTY" and o.name.startswith("Place_"):
            pid = o.name[len("Place_"):].split(".")[0]
            roots, new = import_prop(pid)
            for r in roots:
                r.parent = o
                r.matrix_parent_inverse = Matrix.Identity(4)
        report[o.name] = (o.type, len(o.data.materials) if o.type == "MESH" else 0)
    print("VERIFY objects", json.dumps(report))
    for m in bpy.data.materials:
        nt = m.node_tree
        imgs = sorted(n.image.name for n in nt.nodes if n.type == "TEX_IMAGE" and n.image) if nt else []
        print("VERIFY material", m.name, imgs)
    setup_render()
    os.makedirs(out_dir, exist_ok=True)
    render(os.path.join(out_dir, "verify_exterior.png"), (8.5, 2.4, 10.5), (0.0, 2.1, 0.0), lens=30)
    r9 = bpy.data.objects["R9"]
    render_from_empty(os.path.join(out_dir, "verify_r9.png"), r9, lens=17, exposure=1.5)


# ---------------------------------------------------------------------------------------------------------------------


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview_dir = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    verify_dir = argv[argv.index("--verify") + 1] if "--verify" in argv else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    make_materials()
    rng = random.Random(1879)

    table_top = table_top_height()
    deck = bed_deck_height()

    cabin = [walls(rng), beams(rng), roof(), floor(), footing(), fireplace(rng)]
    chim, flue_x = chimney()
    cabin.append(chim)
    door_obj = door()
    cabin += [door_obj, door_frame()]
    cabin += windows()
    cabin += [rug(), firewood(rng), shelves(), bench()]
    mantel_x = MANTEL_X
    cobj, flame = candle(mantel_x)
    cabin.append(cobj)

    sockets, furniture, coll = place_sockets(table_top)
    bed = next(s for s in sockets if s.name == "Place_old_bed_frame")
    bl = bed.matrix_world.translation
    cabin.append(bedding(((bl.x, 0, -bl.y), bed.matrix_world.to_euler().z, deck)))
    shelf = next(s for s in sockets if s.name == "Place_Shelf_01")
    sl = shelf.matrix_world.translation
    cabin.append(books(((sl.x, sl.z, -sl.y), shelf.matrix_world.to_euler().z), random.Random(42)))

    empties = list(sockets)
    empties.append(empty("Fire", G((CHIM_IN + FIRE_FRONT) / 2, HEARTH_TOP, 0.0)))
    empties.append(empty("ChimneyTop", G(flue_x, 5.78, 0.0)))
    empties.append(empty("CandleFlame", G(*flame), size=0.05))
    empties.append(empty("Interior", G(0, FLOOR, 0), scale=(IN_X, IN_Z, 2.60)))  # Blender (x, y, z) = glTF (x, z, y)
    r9 = empty("R9", None, matrix=look_matrix(R9_EYE, R9_TARGET))
    empties.append(r9)

    gltf = export(cabin + empties)
    print("CABIN triangles", {o.name: tri_count(o) for o in cabin}, "total", sum(tri_count(o) for o in cabin))
    print("CABIN nodes", [(n["name"], n.get("translation"), n.get("rotation"), n.get("scale"))
                          for n in gltf["nodes"] if "mesh" not in n])
    lo = Vector((1e9,) * 3)
    hi = -lo
    for o in cabin:
        for v in o.data.vertices:
            w = o.matrix_world @ v.co
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    print("CABIN bounds glTF min", (round(lo.x, 3), round(lo.z, 3), round(-hi.y, 3)), "max",
          (round(hi.x, 3), round(hi.z, 3), round(-lo.y, 3)))

    if preview_dir:
        previews(preview_dir, door_obj, cabin, r9)
    if verify_dir:
        verify(verify_dir)


main()
