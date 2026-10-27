using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Neon;

/// <summary>
/// The city outside the apartment, built when ready from <see cref="CityLayout"/> (seeded by <see cref="Seed"/>) and the
/// Blender kit (<see cref="CityKit"/>): no node of it is saved in the scene and nothing in it is a light (the neon is
/// emission and the post glow). It builds:
/// <list type="bullet">
/// <item>the near ring: one <see cref="MultiMeshInstance3D"/> per tower archetype (<c>facade.gdshader</c>, each tower's
/// window seed, hue, lit fraction and style in <c>INSTANCE_CUSTOM</c>), and the apartment's own tower round it;</item>
/// <item>the ground: pavements, streets and crossings in one mesh (<c>street.gdshader</c>: wet asphalt, neon kerbs);</item>
/// <item>the far ring: three cylinders of skyline cards (<c>skyline.gdshader</c>);</item>
/// <item>billboards, blade signs and signboards (<c>screen.gdshader</c>, <c>sign.gdshader</c>) and the holograms
/// (<c>hologram.gdshader</c>);</item>
/// <item>the elevated rail's track and pillars along <see cref="CityLayout.RailLoop"/>;</item>
/// <item>the rain (<see cref="NeonRain"/>) and the balcony's puddles;</item>
/// <item>a first, simple traffic on the loops (<see cref="CityTraffic"/>).</item>
/// </list>
/// Phase N4's traffic reads <see cref="Layout"/> (loops in scene space) and the kit's vehicles.
/// </summary>
[Tool]
public sealed class NeonCity : Node3D
{
    public const string FacadeShader = "Assets/Shaders/facade.gdshader";
    public const string SkylineShader = "Assets/Shaders/skyline.gdshader";
    public const string StreetShader = "Assets/Shaders/street.gdshader";
    public const string HologramShader = "Assets/Shaders/hologram.gdshader";
    public const string ScreenShader = "Assets/Shaders/screen.gdshader";
    public const string SignShader = "Assets/Shaders/sign.gdshader";

    private bool _built;

    /// <summary>The layout's seed.</summary>
    [Export] public int Seed { get; set; } = CityLayout.DefaultSeed;

    /// <summary>Whether the rain falls (off for still screenshots of the city alone).</summary>
    [Export] public bool Rain { get; set; } = true;

    /// <summary>Whether the first, simple traffic runs (<see cref="CityTraffic"/>; phase N4 replaces it).</summary>
    [Export] public bool Traffic { get; set; } = true;

    /// <summary>The layout the city was built from (null until built).</summary>
    public CityLayout? Layout { get; private set; }

    /// <summary>The towers' MultiMeshes by archetype (index into <see cref="CityLayout.Archetypes"/>).</summary>
    public List<MultiMeshInstance3D> Towers { get; } = [];

    /// <summary>The facade material of each kit surface (<c>part</c> 0–3: facade, roof, trim, beacon).</summary>
    public ShaderMaterial[] FacadeMaterials { get; } = new ShaderMaterial[4];

    protected override void OnReady()
    {
        if (!_built)
            Build();
    }

    /// <summary>Builds the city now (it builds itself when ready otherwise).</summary>
    public void Build()
    {
        _built = true;
        Layout = CityLayout.Generate(Seed);
        var towersKit = CityKit.Load(CityKit.TowersPath);
        var signsKit = CityKit.Load(CityKit.SignsPath);
        var railKit = CityKit.Load(CityKit.RailPath);

        for (var part = 0; part < 4; part++)
        {
            var material = new ShaderMaterial { ResourceName = "Facade" + part, Shader = ResourceLoader.Load<Shader>(FacadeShader) };
            material.SetShaderParameter("part", (float)part);
            material.SetShaderParameter("ground_y", CityLayout.GroundY);
            material.SetShaderParameter("storey", CityLayout.StoreyHeight);
            FacadeMaterials[part] = material;
        }

        BuildTowers(towersKit);
        BuildHomeTower();
        BuildGround();
        BuildSkyline();
        BuildSigns(signsKit);
        BuildHolograms(signsKit);
        BuildRail(railKit);
        BuildPuddles();
        if (Traffic)
            AddChild(new CityTraffic { Name = "Traffic", Layout = Layout });
        if (Rain)
            AddChild(new NeonRain { Name = "Rain" });
    }

    // ── Towers ────────────────────────────────────────────────────────────────────────────────────

    private void BuildTowers(Dictionary<string, ArrayMesh> kit)
    {
        var layout = Layout!;
        for (var a = 0; a < CityLayout.Archetypes.Length; a++)
        {
            var name = CityLayout.Archetypes[a];
            if (!kit.TryGetValue(name.Name, out var mesh))
            {
                Log.Warning($"[Neon] The city kit has no {name.Name}.");
                continue;
            }

            ApplyFacade(mesh);
            var count = 0;
            foreach (var tower in layout.Towers)
                if (tower.Archetype == a)
                    count++;
            var multimesh = new MultiMesh { Mesh = mesh, UseCustomData = true, InstanceCount = count };
            var i = 0;
            foreach (var tower in layout.Towers)
            {
                if (tower.Archetype != a)
                    continue;
                multimesh.SetInstanceTransform(i, tower.Transform);
                multimesh.SetInstanceCustomData(i, tower.Custom);
                i++;
            }

            var instance = new MultiMeshInstance3D { Name = name.Name, Multimesh = multimesh, CastShadows = false };
            AddChild(instance);
            Towers.Add(instance);
        }
    }

    /// <summary>The kit's tower surfaces get the facade shader by their material's name.</summary>
    private void ApplyFacade(ArrayMesh mesh)
    {
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            surface.Material = (surface.Material?.ResourceName) switch
            {
                "Roof" => FacadeMaterials[1],
                "Trim" => FacadeMaterials[2],
                "Beacon" => FacadeMaterials[3],
                _ => FacadeMaterials[0],
            };
        }
    }

    /// <summary>
    /// The apartment's own tower: its walls from the street to the roof with the hole for the apartment in the front face,
    /// as one instance of a MultiMesh (the facade shader reads its window seed from <c>INSTANCE_CUSTOM</c>), and the
    /// neighbours' balconies beside and below it.
    /// </summary>
    private void BuildHomeTower()
    {
        var box = CityLayout.HomeTower;
        var (x0, x1, y0, y1, z0, z1) = (box.Min.X, box.Max.X, box.Min.Y, box.Max.Y, box.Min.Z, box.Max.Z);
        var hole0 = CityLayout.HomeHoleMin;
        var hole1 = CityLayout.HomeHoleMax;
        var walls = new CityMeshBuilder();
        // Front (+Z): round the hole. UV.x = metres from the west corner.
        walls.Wall(new Vector3(x0, y0, z1), new Vector3(hole0.X, y1, z1), Vector3.UnitZ, x0);
        walls.Wall(new Vector3(hole1.X, y0, z1), new Vector3(x1, y1, z1), Vector3.UnitZ, x0);
        walls.Wall(new Vector3(hole0.X, y0, z1), new Vector3(hole1.X, hole0.Y, z1), Vector3.UnitZ, x0);
        walls.Wall(new Vector3(hole0.X, hole1.Y, z1), new Vector3(hole1.X, y1, z1), Vector3.UnitZ, x0);
        walls.Wall(new Vector3(x1, y0, z0), new Vector3(x1, y1, z1), Vector3.UnitX, z0);
        walls.Wall(new Vector3(x0, y0, z0), new Vector3(x0, y1, z1), -Vector3.UnitX, z0);
        walls.Wall(new Vector3(x0, y0, z0), new Vector3(x1, y1, z0), -Vector3.UnitZ, x0);
        var roof = new CityMeshBuilder();
        roof.Box(new Vector3(x0, y1, z0), new Vector3(x1, y1 + 0.6f, z1));
        // The neighbours' balconies: a slab and a glass-less rail every other storey, either side of the apartment.
        var trim = new CityMeshBuilder();
        for (var storey = 1; storey < 31; storey += 2)
        {
            var y = y0 + storey * CityLayout.StoreyHeight;
            if (y > y1 - 4f || (y > hole0.Y - 4f && y < hole1.Y + 1f))
                continue;
            foreach (var (bx0, bx1) in (ReadOnlySpan<(float, float)>)[(-19f, -9f), (9f, 19f)])
            {
                roof.Box(new Vector3(bx0, y - 0.25f, z1), new Vector3(bx1, y, z1 + 2.2f));
                roof.Box(new Vector3(bx0, y + 1.0f, z1 + 2.1f), new Vector3(bx1, y + 1.08f, z1 + 2.2f));
                trim.Box(new Vector3(bx0, y - 0.32f, z1 + 2.15f), new Vector3(bx1, y - 0.25f, z1 + 2.22f));
            }
        }

        var mesh = new ArrayMesh();
        mesh.AddSurface(walls.Build(FacadeMaterials[0]));
        mesh.AddSurface(roof.Build(FacadeMaterials[1]));
        mesh.AddSurface(trim.Build(FacadeMaterials[2]));
        var multimesh = new MultiMesh { Mesh = mesh, UseCustomData = true, InstanceCount = 1 };
        multimesh.SetInstanceCustomData(0, new Vector4(0.37f, 0.02f, 0.45f, 0.31f));
        AddChild(new MultiMeshInstance3D { Name = "HomeTower", Multimesh = multimesh, CastShadows = false });
    }

    // ── Ground ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ground at <see cref="CityLayout.GroundY"/>, tiled without overlaps: the grid's intervals along X and Z are
    /// pavement or street, so each cell is a pavement, a street segment or a crossing; a frame of pavement reaches the
    /// skyline beyond the grid.
    /// </summary>
    private void BuildGround()
    {
        var layout = Layout!;
        var xs = Intervals(layout.NorthSouth);
        var zs = Intervals(layout.EastWest);
        var b = new CityMeshBuilder();
        var y = CityLayout.GroundY;
        foreach (var (xa, xb, xStreet) in xs)
        {
            foreach (var (za, zb, zStreet) in zs)
            {
                if (xStreet is { } ns && zStreet is { } ew)
                    b.Ground(xa, xb, za, zb, y, _ => Vector2.Zero, new Vector4(MathF.Min(ns.Width, ew.Width) / 128f, 1f, 0f, 1f));
                else if (zStreet is { } street)
                    b.Ground(xa, xb, za, zb, y, p => new Vector2(p.X - xa, p.Y - street.Center), new Vector4(street.Width / 128f, 0f, (xb - xa) / 1024f, 1f));
                else if (xStreet is { } avenue)
                    b.Ground(xa, xb, za, zb, y, p => new Vector2(p.Y - za, p.X - avenue.Center), new Vector4(avenue.Width / 128f, 0f, (zb - za) / 1024f, 1f));
                else
                    b.Ground(xa, xb, za, zb, y, p => p, new Vector4(0f, 2f, 0f, 1f));
            }
        }

        // The frame out to the skyline.
        const float E = CityLayout.GridExtent;
        const float F = 2600f;
        var pave = new Vector4(0f, 2f, 0f, 1f);
        b.Ground(-F, F, E, F, y, p => p, pave);
        b.Ground(-F, F, -F, -E, y, p => p, pave);
        b.Ground(-F, -E, -E, E, y, p => p, pave);
        b.Ground(E, F, -E, E, y, p => p, pave);

        var material = new ShaderMaterial { ResourceName = "Street", Shader = ResourceLoader.Load<Shader>(StreetShader) };
        var mesh = new ArrayMesh();
        mesh.AddSurface(b.Build(material));
        AddChild(new MeshInstance3D { Name = "Ground", Mesh = mesh, CastShadows = false });

        static List<(float A, float B, CityStreet? Street)> Intervals(IReadOnlyList<CityStreet> lines)
        {
            var list = new List<(float, float, CityStreet?)>();
            var at = -CityLayout.GridExtent;
            foreach (var line in lines)
            {
                var a = line.Center - line.Width * 0.5f;
                var c = line.Center + line.Width * 0.5f;
                if (a > at)
                    list.Add((at, a, null));
                list.Add((a, c, line));
                at = c;
            }

            if (at < CityLayout.GridExtent)
                list.Add((at, CityLayout.GridExtent, null));
            return list;
        }
    }

    // ── Far ring ──────────────────────────────────────────────────────────────────────────────────

    private void BuildSkyline()
    {
        var shader = ResourceLoader.Load<Shader>(SkylineShader);
        (float Min, float Max, float Haze)[] layers = [(50f, 250f, 0.4f), (60f, 320f, 0.55f), (80f, 390f, 0.68f)];
        for (var i = 0; i < CityLayout.SkylineRadii.Length; i++)
        {
            var radius = CityLayout.SkylineRadii[i];
            var (min, max, haze) = layers[i];
            var circumference = MathF.Tau * radius;
            var material = new ShaderMaterial { ResourceName = "Skyline" + i, Shader = shader };
            material.SetShaderParameter("seed", i * 0.31f + 0.17f);
            material.SetShaderParameter("slot", circumference / MathF.Round(circumference / (26f + i * 6f)));
            material.SetShaderParameter("min_height", min);
            material.SetShaderParameter("max_height", max);
            material.SetShaderParameter("haze", haze);
            var b = new CityMeshBuilder();
            b.Cylinder(radius, CityLayout.GroundY, max + 40f, 192);
            var mesh = new ArrayMesh();
            mesh.AddSurface(b.Build(material));
            AddChild(new MeshInstance3D { Name = "Skyline" + i, Mesh = mesh, CastShadows = false });
        }
    }

    // ── Signs and holograms ───────────────────────────────────────────────────────────────────────

    private void BuildSigns(Dictionary<string, ArrayMesh> kit)
    {
        var words = ResourceLoader.Load<Texture2D>(CityKit.WordsPath);
        var frame = new StandardMaterial3D
        {
            ResourceName = "SignFrame",
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = DrawingColor.FromArgb(255, 12, 12, 14),
            Metallic = 0f,
            Roughness = 0.85f,
        };
        var screen = new ShaderMaterial { ResourceName = "Screen", Shader = ResourceLoader.Load<Shader>(ScreenShader) };
        screen.SetShaderParameter("words", words);
        var blade = new ShaderMaterial { ResourceName = "SignBlade", Shader = ResourceLoader.Load<Shader>(SignShader) };
        blade.SetShaderParameter("words", words);
        blade.SetShaderParameter("vertical", 1f);
        blade.SetShaderParameter("panel", new Vector2(1.6f, 0.96f));
        var board = new ShaderMaterial { ResourceName = "SignBoard", Shader = ResourceLoader.Load<Shader>(SignShader) };
        board.SetShaderParameter("words", words);
        board.SetShaderParameter("vertical", 0f);
        board.SetShaderParameter("panel", Vector2.One);

        AddSigns(kit, "Billboard", CitySignKind.Billboard, screen, "Screen", frame);
        AddSigns(kit, "Sign_Blade", CitySignKind.Blade, blade, "Panel", frame);
        AddSigns(kit, "Sign_Board", CitySignKind.Board, board, "Panel", frame);
    }

    private void AddSigns(Dictionary<string, ArrayMesh> kit, string meshName, CitySignKind kind, Material lit, string litSurface, Material frame)
    {
        if (!kit.TryGetValue(meshName, out var mesh))
            return;
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            surface.Material = surface.Material?.ResourceName == litSurface ? lit : frame;
        }

        var signs = Layout!.Signs.Where(s => s.Kind == kind).ToList();
        var multimesh = new MultiMesh { Mesh = mesh, UseCustomData = true, InstanceCount = signs.Count };
        for (var i = 0; i < signs.Count; i++)
        {
            multimesh.SetInstanceTransform(i, signs[i].Transform);
            multimesh.SetInstanceCustomData(i, signs[i].Custom);
        }

        AddChild(new MultiMeshInstance3D { Name = meshName + "s", Multimesh = multimesh, CastShadows = false });
    }

    private void BuildHolograms(Dictionary<string, ArrayMesh> kit)
    {
        var shader = ResourceLoader.Load<Shader>(HologramShader);
        foreach (var hologram in Layout!.Holograms)
        {
            var material = new ShaderMaterial { ResourceName = "Hologram" + hologram.Kind, Shader = shader };
            Mesh? mesh;
            Aabb? bounds = null;
            switch (hologram.Kind)
            {
                case CityHologramKind.Koi:
                    mesh = kit.GetValueOrDefault("Holo_Koi");
                    material.SetShaderParameter("shape", 0f);
                    material.SetShaderParameter("color", new Vector4(0.15f, 0.85f, 1f, 1f));
                    material.SetShaderParameter("color2", new Vector4(1f, 0.42f, 0.12f, 1f));
                    material.SetShaderParameter("orbit", 1.2f);
                    material.SetShaderParameter("speed", 0.09f);
                    material.SetShaderParameter("phase", 1.4f); // broadside to the window at the start
                    material.SetShaderParameter("line_spacing", 0.5f);
                    bounds = new Aabb(new Vector3(-1.8f, -0.4f, -1.8f), new Vector3(1.8f, 0.4f, 1.8f));
                    break;
                case CityHologramKind.Logo:
                    mesh = kit.GetValueOrDefault("Holo_Logo");
                    material.SetShaderParameter("shape", 1f);
                    material.SetShaderParameter("color", new Vector4(1f, 0.2f, 0.65f, 1f));
                    material.SetShaderParameter("speed", 0.6f);
                    bounds = new Aabb(new Vector3(-0.6f), new Vector3(0.6f));
                    break;
                default:
                    mesh = CityMeshBuilder.UprightQuad();
                    material.SetShaderParameter("shape", 2f);
                    material.SetShaderParameter("color", new Vector4(0.2f, 0.9f, 1f, 1f));
                    material.SetShaderParameter("color2", new Vector4(1f, 0.15f, 0.7f, 1f));
                    material.SetShaderParameter("line_spacing", 0.8f);
                    break;
            }

            if (mesh is null)
                continue;
            var scale = hologram.Kind == CityHologramKind.Dancer ? new Vector3(hologram.Size * 0.6f, hologram.Size, 1f) : new Vector3(hologram.Size);
            AddChild(new MeshInstance3D
            {
                Name = "Hologram" + hologram.Kind,
                Mesh = mesh,
                MaterialOverride = material,
                CastShadows = false,
                CustomAabb = bounds,
                Transform = Transform3D.FromTrs(hologram.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, hologram.Yaw), scale),
            });
        }
    }

    // ── Rail ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The track along the rail loop in 8 m pieces (stretched to close the loop), a pillar under every fourth.</summary>
    private void BuildRail(Dictionary<string, ArrayMesh> kit)
    {
        if (!kit.TryGetValue("Rail_Track", out var track) || !kit.TryGetValue("Rail_Pillar", out var pillar) || Layout!.RailLoop.Points.Count < 3)
            return;
        var curve = Layout.RailLoop.CreateCurve();
        var length = curve.GetBakedLength();
        var pieces = (int)MathF.Round(length / 8f);
        var step = length / pieces;
        var tracks = new MultiMesh { Mesh = track, InstanceCount = pieces };
        var pillars = new MultiMesh { Mesh = pillar, InstanceCount = (pieces + 3) / 4 };
        for (var i = 0; i < pieces; i++)
        {
            var offset = (i + 0.5f) * step;
            var position = curve.SampleBaked(offset);
            var tangent = curve.SampleBakedTangent(offset);
            tangent = Vector3.Normalize(new Vector3(tangent.X, 0f, tangent.Z));
            var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, tangent));
            var basis = new Basis(side, Vector3.UnitY, tangent * (step / 8f + 0.02f));
            tracks.SetInstanceTransform(i, new Transform3D(basis, position - new Vector3(0f, 1f, 0f)));
            if (i % 4 == 0)
                pillars.SetInstanceTransform(i / 4, new Transform3D(new Basis(side, Vector3.UnitY, tangent), position with { Y = CityLayout.GroundY }));
        }

        var concrete = new StandardMaterial3D { ResourceName = "RailConcrete", ShadingMode = ShadingMode.Pbr, AlbedoColor = DrawingColor.FromArgb(255, 40, 40, 44), Roughness = 0.8f };
        foreach (var mesh in (ReadOnlySpan<ArrayMesh>)[track, pillar])
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                surface.Material = surface.Material?.ResourceName == "Trim" ? FacadeMaterials[2] : concrete;
            }

        tracks.UseCustomData = true;
        for (var i = 0; i < pieces; i++)
            tracks.SetInstanceCustomData(i, new Vector4(0.5f, 0.17f, 0f, 0f)); // the trims glow cyan
        AddChild(new MultiMeshInstance3D { Name = "RailTrack", Multimesh = tracks, CastShadows = false });
        AddChild(new MultiMeshInstance3D { Name = "RailPillars", Multimesh = pillars, CastShadows = false });
    }

    // ── The balcony ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Rain puddles on the balcony floor: shallow water with screen-space reflections of the city.</summary>
    private void BuildPuddles()
    {
        var water = new WaterMaterial3D
        {
            ResourceName = "Puddle",
            RefractionEnabled = true,
            ScreenSpaceReflections = true,
            RefractionRoughness = 0.05f,
            Absorption = new Vector3(90f, 80f, 70f), // a film of water over dark concrete: it reads by its reflection
            ScatterColor = DrawingColor.FromArgb(255, 8, 8, 12),
            ScatterStrength = 0.2f,
            NormalScaleNear = 0.6f,
            NormalScaleFar = 2.5f,
            NormalStrength = 0.25f,
            Roughness = 0.03f,
            ReflectionStrength = 1.2f,
            CausticsStrength = 0f,
            FoamStrength = 0f,
            SoftEdgeDistance = 0.004f,
        };
        var b = new CityMeshBuilder();
        var rng = new Random(Seed + 1);
        foreach (var (cx, cz, rx, rz) in (ReadOnlySpan<(float, float, float, float)>)[(-4.6f, 6.9f, 1.1f, 0.5f), (1.0f, 7.3f, 0.8f, 0.38f), (5.0f, 6.5f, 0.9f, 0.55f)])
            b.Puddle(new Vector3(cx, 0.006f, cz), rx, rz, 0.012f, rng);
        var mesh = new ArrayMesh();
        mesh.AddSurface(b.Build(water));
        AddChild(new MeshInstance3D { Name = "Puddles", Mesh = mesh, CastShadows = false });
    }
}

/// <summary>The city kit's glTF files (<c>Tools/city/build_city_kit.py</c>) and a loader for their meshes by name.</summary>
public static class CityKit
{
    public const string TowersPath = "Assets/Art/City/towers.gltf";
    public const string RailPath = "Assets/Art/City/rail.gltf";
    public const string VehiclesPath = "Assets/Art/City/vehicles.gltf";
    public const string SignsPath = "Assets/Art/City/signs.gltf";

    /// <summary>The neon words atlas (horizontal words in the top half, vertical ones in the bottom half).</summary>
    public const string WordsPath = "Assets/Art/City/signs.png";

    /// <summary>The binary buffer of the towers (LFS): tests that need the kit skip when it is a pointer.</summary>
    public const string TowersBufferPath = "Assets/Art/City/towers.bin";

    /// <summary>
    /// The meshes of a kit file by their node names. They are the loaded model's own (shared by every call): whoever builds
    /// with a mesh sets its surfaces' materials, so each mesh has one user.
    /// </summary>
    public static Dictionary<string, ArrayMesh> Load(string path)
    {
        var meshes = new Dictionary<string, ArrayMesh>(StringComparer.Ordinal);
        var scene = ResourceLoader.Load<PackedScene>(path);
        Node root;
        try
        {
            root = scene.Instantiate<Node>();
        }
        finally
        {
            scene.Release();
        }

        Walk(root);
        root.Free();
        return meshes;

        void Walk(Node node)
        {
            if (node is MeshInstance3D { Mesh: ArrayMesh mesh })
                meshes[node.Name] = mesh;
            foreach (var child in node.Children)
                Walk(child);
        }
    }
}

/// <summary>Builds the city's procedural meshes (walls, boxes, the ground, cylinders, puddles) into one surface.</summary>
internal sealed class CityMeshBuilder
{
    private readonly List<Vector3> _positions = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector2> _uvs = [];
    private readonly List<Vector4> _colors = [];
    private readonly List<Vector4> _custom = [];
    private readonly List<int> _indices = [];

    private int Vertex(Vector3 p, Vector3 n, Vector2 uv, Vector4 color = default, Vector4 custom = default)
    {
        _positions.Add(p);
        _normals.Add(n);
        _uvs.Add(uv);
        _colors.Add(color);
        _custom.Add(custom);
        return _positions.Count - 1;
    }

    /// <summary>A quad a b c d, counter-clockwise seen from its front.</summary>
    private void Quad(int a, int b, int c, int d)
    {
        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
        _indices.Add(a);
        _indices.Add(c);
        _indices.Add(d);
    }

    /// <summary>
    /// A vertical wall over the box min..max (flat along the normal's axis), facing <paramref name="normal"/>; UV.x is
    /// metres along it from <paramref name="uOrigin"/>, UV.y metres up.
    /// </summary>
    public void Wall(Vector3 min, Vector3 max, Vector3 normal, float uOrigin)
    {
        var alongX = MathF.Abs(normal.Z) > 0.5f;
        var right = Vector3.Cross(Vector3.UnitY, normal); // the wall's right seen from its front
        Vector3 bl, br;
        if (alongX)
            (bl, br) = right.X > 0 ? (new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z)) : (new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z));
        else
            (bl, br) = right.Z > 0 ? (new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z)) : (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z));
        var h = max.Y - min.Y;
        float U(Vector3 p) => (alongX ? p.X : p.Z) - uOrigin;
        var a = Vertex(bl, normal, new Vector2(U(bl), bl.Y));
        var b = Vertex(br, normal, new Vector2(U(br), br.Y));
        var c = Vertex(br + new Vector3(0f, h, 0f), normal, new Vector2(U(br), max.Y));
        var d = Vertex(bl + new Vector3(0f, h, 0f), normal, new Vector2(U(bl), max.Y));
        Quad(a, b, c, d);
    }

    /// <summary>An axis-aligned box (all six faces, UV in metres).</summary>
    public void Box(Vector3 min, Vector3 max)
    {
        Wall(new Vector3(min.X, min.Y, max.Z), max, Vector3.UnitZ, 0f);
        Wall(min, new Vector3(max.X, max.Y, min.Z), -Vector3.UnitZ, 0f);
        Wall(new Vector3(max.X, min.Y, min.Z), max, Vector3.UnitX, 0f);
        Wall(min, new Vector3(min.X, max.Y, max.Z), -Vector3.UnitX, 0f);
        Flat(min.X, max.X, min.Z, max.Z, max.Y, Vector3.UnitY, p => p, default);
        Flat(min.X, max.X, min.Z, max.Z, min.Y, -Vector3.UnitY, p => p, default);
    }

    /// <summary>A ground rectangle facing up, UV from the (x, z) point, COLOR = <paramref name="color"/>.</summary>
    public void Ground(float x0, float x1, float z0, float z1, float y, Func<Vector2, Vector2> uv, Vector4 color) =>
        Flat(x0, x1, z0, z1, y, Vector3.UnitY, uv, color);

    private void Flat(float x0, float x1, float z0, float z1, float y, Vector3 normal, Func<Vector2, Vector2> uv, Vector4 color)
    {
        Vector2[] corners = [new(x0, z1), new(x1, z1), new(x1, z0), new(x0, z0)]; // counter-clockwise seen from above
        Span<int> v = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            v[i] = Vertex(new Vector3(corners[i].X, y, corners[i].Y), normal, uv(corners[i]), color);
        if (normal.Y > 0)
            Quad(v[0], v[1], v[2], v[3]);
        else
            Quad(v[3], v[2], v[1], v[0]);
    }

    /// <summary>An open cylinder round the origin facing inwards: UV.x metres round it, UV.y metres above the street.</summary>
    public void Cylinder(float radius, float y0, float height, int segments)
    {
        var first = _positions.Count;
        for (var i = 0; i <= segments; i++)
        {
            var a = i * MathF.Tau / segments;
            var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            var u = a * radius;
            Vertex(dir * radius + new Vector3(0f, y0, 0f), -dir, new Vector2(u, 0f));
            Vertex(dir * radius + new Vector3(0f, y0 + height, 0f), -dir, new Vector2(u, height));
        }

        for (var i = 0; i < segments; i++)
        {
            var a = first + i * 2;
            Quad(a, a + 1, a + 3, a + 2);
        }
    }

    /// <summary>A puddle: an irregular disc of water whose depth (CUSTOM0.x) falls to nothing at its edge.</summary>
    public void Puddle(Vector3 centre, float rx, float rz, float depth, Random rng)
    {
        const int n = 20;
        var c = Vertex(centre, Vector3.UnitY, new Vector2(centre.X, centre.Z), default, new Vector4(depth, 0f, 0f, 0f));
        var first = _positions.Count;
        var wobble = new float[4];
        for (var k = 0; k < 4; k++)
            wobble[k] = (float)rng.NextDouble() * MathF.Tau;
        for (var i = 0; i < n; i++)
        {
            var a = i * MathF.Tau / n;
            var r = 1f + 0.18f * MathF.Sin(a * 2f + wobble[0]) + 0.1f * MathF.Sin(a * 3f + wobble[1]) + 0.06f * MathF.Sin(a * 5f + wobble[2]);
            var p = centre + new Vector3(MathF.Cos(a) * rx * r, 0f, MathF.Sin(a) * rz * r);
            Vertex(p, Vector3.UnitY, new Vector2(p.X, p.Z), default, Vector4.Zero);
        }

        for (var i = 0; i < n; i++)
        {
            // counter-clockwise seen from above: centre, next, this (angles grow from +X towards +Z, clockwise seen from above)
            _indices.Add(c);
            _indices.Add(first + (i + 1) % n);
            _indices.Add(first + i);
        }
    }

    /// <summary>A unit quad standing on the origin facing +Z (x −0.5..0.5, y 0..1), UV 0..1 with v down.</summary>
    public static ArrayMesh UprightQuad()
    {
        var mesh = new ArrayMesh();
        mesh.AddSurface(
            [new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f)],
            [0, 1, 2, 0, 2, 3]);
        return mesh;
    }

    public MeshSurface Build(Material material)
    {
        var surface = new MeshSurface([.. _positions], [.. _normals], [.. _uvs], [.. _indices], material);
        if (_colors.Exists(c => c != default))
            surface.Colors = [.. _colors];
        if (_custom.Exists(c => c != default))
            surface.Custom0 = [.. _custom];
        return surface;
    }
}
