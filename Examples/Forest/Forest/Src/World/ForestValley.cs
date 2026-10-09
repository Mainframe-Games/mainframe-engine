using System.Diagnostics;
using System.Numerics;
using MainframeEngine;
using MainframeEngine.Trees;
using DrawingColor = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The Forest's valley, generated when the node is ready (the scene file holds this node and its knobs only): the
/// <see cref="Terrain3D"/> from <see cref="ValleyGenerator"/> with the eight ambientCG layers, the stream
/// (<see cref="River3D"/>, carved) and the pond, ground cover (grass, reeds, ferns, stones), trees and bushes
/// (<see cref="TreeScatter"/>), the Poly Haven props with their collision (the log bridge among them) and invisible walls
/// at the map's edge. Everything it builds is an unowned child, never saved. It also stands the player on the ground,
/// resolves footsteps from the terrain's layers and hands the pine density to the audio.
/// </summary>
public sealed class ForestValley : Node3D
{
    /// <summary>Seed of the noise, trees and props (the layout is fixed).</summary>
    [Export] public int Seed { get; set; } = 1;

    /// <summary>Load the CC0 art (terrain layers, props, fern). Off: plain materials and procedural stand-ins (tests, CI without LFS).</summary>
    [Export] public bool Art { get; set; } = true;

    /// <summary>Build trees and bushes.</summary>
    [Export] public bool Trees { get; set; } = true;

    /// <summary>Build the ground cover (grass, reeds, ferns, stones).</summary>
    [Export] public bool GroundCover { get; set; } = true;

    /// <summary>Build the Poly Haven props (the bridge's walkway is always built).</summary>
    [Export] public bool PropModels { get; set; } = true;

    /// <summary>Multiplies every ground-cover density (a quality knob).</summary>
    [Export(Range = "0,2,0.05")] public float GroundCoverDensity { get; set; } = 0.8f;

    /// <summary>Distance at which grass is gone (it thins over the last 12 m).</summary>
    [Export(Range = "10,80,1")] public float GrassDistance { get; set; } = 32f;

    /// <summary>Tree levels: level 1 from, level 2 from, and the distance at which trees end.</summary>
    [Export] public float TreeLod1Distance { get; set; } = 22f;

    [Export] public float TreeLod2Distance { get; set; } = 55f;

    [Export] public float TreeMaxDistance { get; set; } = 400f;

    /// <summary>The finest-to-coarsest tree level that still casts sun shadows (<see cref="TreeScatter.ShadowMaxLod"/>).</summary>
    [Export(Range = "0,2,1")] public int TreeShadowMaxLod { get; set; } = 1;

    private readonly List<(GeometryInstance3D Node, bool Visible, float Begin, float End, int Count)> _prewarm = [];
    private int _frames;

    /// <summary>
    /// Frames drawn with everything shown at load: every frame-in-flight slot's buffers, with margin for frames skipped
    /// while the swapchain is rebuilt (a window resize on the first frames).
    /// </summary>
    public const int PrewarmFrames = 8;

    /// <summary>The generator (after ready).</summary>
    public ValleyGenerator? Generator { get; private set; }

    public Terrain3D? Terrain { get; private set; }

    public River3D? Stream { get; private set; }

    public TreeScatter? Forest { get; private set; }

    public TreeScatter? Bushes { get; private set; }

    public Node3D? Props { get; private set; }

    /// <summary>The log bridge's walkable body.</summary>
    public SurfaceBody3D? Bridge { get; private set; }

    /// <summary>The fall's position (top of the drop) for the audio.</summary>
    public static Vector3 FallPosition
    {
        get
        {
            var lip = ValleyLayout.StreamPoints[2].Position;
            var foot = ValleyLayout.StreamPoints[3].Position;
            return (lip + foot) * 0.5f;
        }
    }

    protected override void OnReady()
    {
        if (Parent is { } scene && GameHost.UserArgs.Count > 0)
            ForestDev.ApplyOverrides(scene, GameHost.UserArgs, valley: true);
        var watch = Stopwatch.StartNew();
        var valley = Generator = ValleyGenerator.Generate(Seed);
        var generated = watch.Elapsed.TotalMilliseconds;

        var data = valley.CreateTerrainData();
        var terrain = Terrain = new Terrain3D
        {
            Name = "Terrain",
            Data = data,
            Material = Art ? CreateTerrainMaterial() : new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = DrawingColor.FromArgb(255, 92, 96, 62), Roughness = 0.95f },
            WaterMaterial = CreatePondMaterial(),
        };
        var stream = Stream = new River3D
        {
            Name = "Stream",
            Curve = valley.StreamCurve,
            Terrain = terrain,
            CarveId = "stream",
            BankWidth = 2.2f,
            ShoreLift = 0.06f,
            SectionLength = 0.75f,
            CrossSegments = 6,
            Material = CreateStreamMaterial(),
        };
        stream.Carve();
        ValleyGenerator.ApplyPond(data);
        valley.PaintWeights(data);
        var carved = watch.Elapsed.TotalMilliseconds;

        var bed = data.BedHeights.ToArray();
        var grid = data.Grid;
        float Height(float x, float z) => grid.HeightAt(bed, x, z);
        float Slope(float x, float z) => float.RadiansToDegrees(MathF.Acos(Math.Clamp(grid.SmoothNormalAt(bed, x, z).Y, -1f, 1f)));

        if (GroundCover)
            data.FoliageTypes = CreateGroundCover();
        AddChild(terrain);
        AddChild(stream);

        if (Trees)
        {
            Forest = new TreeScatter
            {
                Name = "Trees",
                Species = ForestVegetation.CreateTreeSpecies(),
                Lod1Distance = TreeLod1Distance,
                Lod2Distance = TreeLod2Distance,
                MaxDistance = TreeMaxDistance,
                ShadowMaxLod = TreeShadowMaxLod,
            };
            Forest.SetPlacements(ForestVegetation.PlaceTrees(valley, Height, Slope));
            AddChild(Forest);

            Bushes = new TreeScatter
            {
                Name = "Bushes",
                Species = ForestVegetation.CreateBushSpecies(),
                ChunkSize = 32f,
                Lod1Distance = 18f,
                Lod2Distance = 40f,
                MaxDistance = 75f,
                ShadowMaxLod = 0,
                Collision = false,
            };
            Bushes.SetPlacements(ForestVegetation.PlaceBushes(valley, Height, Slope));
            AddChild(Bushes);
        }

        var trees = watch.Elapsed.TotalMilliseconds;
        Props = new Node3D { Name = "Props" };
        AddChild(Props);
        BuildProps(valley, Height, Slope);
        BuildWalls();
        var props = watch.Elapsed.TotalMilliseconds;

        ConnectPlayer(terrain);
        ConnectAudio(valley);
        Log.Info($"[Forest] Valley: generated {generated:0} ms, carved and painted {carved - generated:0} ms, trees {trees - carved:0} ms " +
                 $"({Forest?.PlacementCount ?? 0} trees, {Bushes?.PlacementCount ?? 0} bushes), props {props - trees:0} ms.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        // The terrain's foliage builds its tiles in its first process, so the pre-warm starts on the second frame; after
        // it everything gets back what LOD, distance and thinning chose.
        _frames++;
        if (_frames == 2)
            BeginPrewarm();
        else if (_frames == 2 + PrewarmFrames)
            EndPrewarm();
    }

    /// <summary>
    /// Shows every terrain LOD level, foliage tile, tree and bush batch and prop for the first frames, so the renderer
    /// creates all their GPU resources and pipelines while loading instead of the first time each comes into view
    /// (a hitch and an allocation in play). Then each gets back what it had.
    /// </summary>
    private void BeginPrewarm()
    {
        _prewarm.Clear();
        Collect(this);
        // The foliage thins and hides its tiles every frame: it waits while everything is shown.
        if (Terrain?.Foliage is { } foliage)
            foliage.ProcessMode = ProcessMode.Disabled;
        foreach (var (node, _, _, _, _) in _prewarm)
        {
            node.Visible = true;
            node.VisibilityRangeBegin = 0f;
            node.VisibilityRangeEnd = 0f;
            if (node is MultiMeshInstance3D { Multimesh: { } multimesh })
                multimesh.VisibleInstanceCount = -1;
        }

        void Collect(Node node)
        {
            if (node is GeometryInstance3D geometry)
                _prewarm.Add((geometry, geometry.Visible, geometry.VisibilityRangeBegin, geometry.VisibilityRangeEnd,
                    (geometry as MultiMeshInstance3D)?.Multimesh?.VisibleInstanceCount ?? -1));
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
                Collect(children[i]);
        }
    }

    private void EndPrewarm()
    {
        foreach (var (node, visible, begin, end, count) in _prewarm)
        {
            node.Visible = visible;
            node.VisibilityRangeBegin = begin;
            node.VisibilityRangeEnd = end;
            if (node is MultiMeshInstance3D { Multimesh: { } multimesh })
                multimesh.VisibleInstanceCount = count;
        }

        _prewarm.Clear();
        _prewarm.Capacity = 0;
        if (Terrain?.Foliage is { } foliage)
            foliage.ProcessMode = ProcessMode.Inherit;
    }

    // ── Materials ───────────────────────────────────────────────────────────

    /// <summary>
    /// The terrain's look: <see cref="ForestAssets.CreateTerrainMaterial"/> with every layer's roughness lifted
    /// (<see cref="RoughenOrm"/>) and the grass and leaf photos tinted for a shaded morning forest.
    /// </summary>
    private static TerrainSplatMaterial3D CreateTerrainMaterial()
    {
        var material = ForestAssets.CreateTerrainMaterial();
        foreach (var layer in material.Layers)
            if (layer.Orm is { } orm)
                layer.Orm = RoughenOrm(orm);
        material.DetailDistance = 45f;
        material.FarDistance = 110f;
        material.AntiTiling = false; // hex-tiling costs ≈ 3.5 ms at 1080p on an M5; the macro variation breaks the repeats instead
        material.MacroStrength = 0.22f;
        // The meadow photo is bright yellow-green: deeper and cooler under the morning sun.
        material.Layers[ValleyGenerator.Grass].Tint = DrawingColor.FromArgb(255, 150, 168, 118);
        // Pale, dry leaf litter: browner and darker, so sunlit patches do not read white.
        material.Layers[ValleyGenerator.Leaves].Tint = DrawingColor.FromArgb(255, 168, 150, 128);
        return material;
    }

    /// <summary>
    /// A copy of an ORM map with roughness r → 0.6 + 0.4 r: ambientCG's ground scans measure 0.45–0.6, which under a low
    /// back-light gives dry soil and grass a wet sheen (a white glare where the ground faces the sun). Forest floor,
    /// grass and bark scatter far more; mid-tone detail stays.
    /// </summary>
    public static Texture2D RoughenOrm(Texture2D orm)
    {
        var (rgba, width, height) = orm.DecodePixels();
        for (var i = 1; i < rgba.Length; i += 4)
            rgba[i] = (byte)Math.Min(255, 153 + rgba[i] * 102 / 255);
        return Texture2D.FromPixels(width, height, rgba, orm.ImportSettings with { ColorSpace = TextureImportColorSpace.Linear });
    }

    private static WaterMaterial3D CreateStreamMaterial() => new()
    {
        ResourceName = "Stream water",
        Absorption = new Vector3(0.5f, 0.16f, 0.12f),
        ScatterColor = DrawingColor.FromArgb(255, 30, 52, 40),
        ScatterStrength = 0.45f,
        NormalScaleNear = 1.2f,
        NormalScaleFar = 5f,
        NormalStrength = 0.6f,
        Roughness = 0.05f, // a narrow glitter path towards the low sun rather than a broad glare
        ReflectionStrength = 0.35f, // the sky's reflection, as if the canopy hid most of it (no SSR yet)
        FoamStrength = 1f,
    };

    private static WaterMaterial3D CreatePondMaterial() => new()
    {
        ResourceName = "Pond water",
        Absorption = new Vector3(1.6f, 0.75f, 0.7f), // peaty: the deep middle goes dark
        ScatterColor = DrawingColor.FromArgb(255, 20, 38, 28),
        ScatterStrength = 0.4f,
        NormalScaleNear = 3f,
        NormalScaleFar = 12f,
        NormalStrength = 0.35f,
        Roughness = 0.04f,
        ReflectionStrength = 0.8f,
        WindDrift = 0.08f,
        FoamStrength = 0.4f,
    };

    // ── Ground cover ────────────────────────────────────────────────────────

    private FoliageType[] CreateGroundCover()
    {
        var grassMaterial = GrassMesh.CreateMaterial();
        grassMaterial.WindStrength = 0.45f;
        var density = GroundCoverDensity;
        const uint grass = 1u << ValleyGenerator.Grass, leaves = 1u << ValleyGenerator.Leaves, moss = 1u << ValleyGenerator.Moss,
            needles = 1u << ValleyGenerator.Needles, mud = 1u << ValleyGenerator.Mud, gravel = 1u << ValleyGenerator.Gravel;

        var types = new List<FoliageType>
        {
            new()
            {
                ResourceName = "Meadow grass",
                Mesh = GrassMesh.Clump(blades: 16, height: 0.48f, width: 0.04f, bend: 0.45f, seed: 1,
                    rootColor: new Vector4(0.09f, 0.14f, 0.04f, 1f), tipColor: new Vector4(0.42f, 0.5f, 0.2f, 1f)),
                Material = grassMaterial,
                Density = 8f * density,
                LayerMask = grass,
                ScaleMin = 0.7f,
                ScaleMax = 1.35f,
                AlignToNormal = 0.4f,
                CullDistance = GrassDistance,
                ThinBand = 12f,
                Subdivisions = 2,
            },
            new()
            {
                ResourceName = "Tall grass",
                Mesh = GrassMesh.Clump(blades: 10, height: 0.85f, width: 0.03f, bend: 0.5f, seed: 7,
                    rootColor: new Vector4(0.1f, 0.13f, 0.05f, 1f), tipColor: new Vector4(0.55f, 0.55f, 0.3f, 1f)),
                Material = grassMaterial,
                Density = 0.9f * density,
                LayerMask = grass,
                ScaleMin = 0.8f,
                ScaleMax = 1.3f,
                AlignToNormal = 0.2f,
                CullDistance = GrassDistance,
                ThinBand = 12f,
                Subdivisions = 2,
                Seed = 3,
            },
            new()
            {
                ResourceName = "Woodland grass",
                Mesh = GrassMesh.Clump(blades: 9, height: 0.32f, width: 0.03f, bend: 0.45f, seed: 2,
                    rootColor: new Vector4(0.07f, 0.1f, 0.03f, 1f), tipColor: new Vector4(0.3f, 0.38f, 0.14f, 1f)),
                Material = grassMaterial,
                Density = 1.4f * density,
                LayerMask = leaves | moss,
                ScaleMin = 0.7f,
                ScaleMax = 1.2f,
                CullDistance = GrassDistance * 0.8f,
                ThinBand = 10f,
                Seed = 4,
            },
            new()
            {
                ResourceName = "Reeds",
                Mesh = GrassMesh.Clump(blades: 12, height: 1.15f, width: 0.022f, bend: 0.18f, seed: 5,
                    rootColor: new Vector4(0.12f, 0.16f, 0.06f, 1f), tipColor: new Vector4(0.45f, 0.48f, 0.22f, 1f)),
                Material = grassMaterial,
                Density = 1.6f * density,
                LayerMask = mud,
                HeightMax = ValleyLayout.PondLevel + 1.2f, // the pond's shore only
                ScaleMin = 0.75f,
                ScaleMax = 1.3f,
                AlignToNormal = 0.1f,
                SlopeMaxDegrees = 30f,
                CullDistance = GrassDistance + 8f,
                ThinBand = 12f,
                Seed = 5,
            },
        };

        types.Add(Art ? FernFromArt(density, needles | leaves | moss) : new FoliageType
        {
            ResourceName = "Ferns",
            Mesh = GrassMesh.Fern(seed: 2),
            Material = grassMaterial,
            Density = 0.2f * density,
            LayerMask = needles | leaves | moss,
            CullDistance = 50f,
            ThinBand = 15f,
            Seed = 6,
        });
        types.Add(new FoliageType
        {
            ResourceName = "Stones",
            Mesh = Art ? Recentre(ForestAssets.Rock07, withWind: false) : GrassMesh.Rock(radius: 0.2f, seed: 3),
            Material = Art ? ForestAssets.CreatePropMaterial(ForestAssets.Rock07) : new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, Roughness = 0.85f },
            Density = 0.06f * density,
            LayerMask = gravel | moss,
            ScaleMin = Art ? 1f : 0.6f,
            ScaleMax = Art ? 2.6f : 1.6f,
            AlignToNormal = 0.8f,
            SinkMeters = 0.05f,
            SlopeMaxDegrees = 35f,
            CullDistance = 45f,
            ThinBand = 15f,
            Seed = 7,
        });
        return [.. types];
    }

    private static FoliageType FernFromArt(float density, uint mask)
    {
        var prop = ForestAssets.Fern;
        var source = ForestAssets.CreatePropMaterial(prop);
        var material = new FoliageMaterial3D
        {
            ResourceName = "Fern",
            AlbedoTexture = source.AlbedoTexture,
            NormalTexture = source.NormalTexture,
            OrmTexture = source.OrmTexture,
            AlphaCutout = true,
            AlphaCutoff = 0.45f,
            BackFace = FoliageBackFace.Flip,
            Translucency = 0.5f,
            ShadingMode = ShadingMode.Pbr,
            Roughness = 1f,
            WindStrength = 0.5f,
            WindBranchBend = 0.3f,
        };
        return new FoliageType
        {
            ResourceName = "Ferns",
            Mesh = Recentre(prop, withWind: true),
            Material = material,
            Density = 0.24f * density,
            LayerMask = mask,
            ScaleMin = 0.55f,
            ScaleMax = 0.95f,
            AlignToNormal = 0.3f,
            SinkMeters = 0.05f,
            SlopeMaxDegrees = 38f,
            CullDistance = 55f,
            ThinBand = 15f,
            Seed = 6,
        };
    }

    /// <summary>
    /// A prop's meshes merged into one surface standing on the origin (base at y = 0, centred in XZ), with the foliage
    /// stream (<c>Custom0</c>: wind weight by height, flutter, phase by position, AO) when <paramref name="withWind"/>.
    /// </summary>
    internal static ArrayMesh Recentre(ForestPropAsset prop, bool withWind)
    {
        var parts = ForestAssets.LoadPropMeshes(prop);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        foreach (var (mesh, transform) in parts)
        {
            var matrix = transform.ToMatrix4x4();
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                var start = positions.Count;
                foreach (var p in surface.Positions)
                    positions.Add(Vector3.Transform(p, matrix));
                foreach (var n in surface.Normals)
                    normals.Add(Vector3.Normalize(Vector3.TransformNormal(n, matrix)));
                uvs.AddRange(surface.UVs);
                foreach (var i in surface.Indices)
                    indices.Add(start + i);
            }
        }

        var bounds = Aabb.FromPoints(positions.ToArray());
        var offset = new Vector3(bounds.Center.X, bounds.Min.Y, bounds.Center.Z);
        var height = MathF.Max(bounds.Size.Y, 1e-3f);
        var custom = new Vector4[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            positions[i] -= offset;
            var h = Math.Clamp(positions[i].Y / height, 0f, 1f);
            var phase = ValleyNoise.Hash01((int)(positions[i].X * 4f), (int)(positions[i].Z * 4f), 17);
            custom[i] = new Vector4(MathF.Pow(h, 1.4f), 1f, phase, 0.55f + 0.45f * h);
        }

        var result = new ArrayMesh { ResourceName = prop.AssetId };
        var merged = new MeshSurface([.. positions], [.. normals], [.. uvs], [.. indices]);
        if (withWind)
            merged.Custom0 = custom;
        result.AddSurface(merged);
        return result;
    }

    // ── Props ───────────────────────────────────────────────────────────────

    private void BuildProps(ValleyGenerator valley, Func<float, float, float> height, Func<float, float, float> slope)
    {
        var placements = ForestVegetation.PlaceProps(valley, slope);
        if (!Art || !PropModels)
        {
            // Without the art only the bridge's walkway (collision) is built.
            var log = placements[0];
            Bridge = BridgeWalkway(valley, log, new Aabb(new Vector3(-2f, 0f, -0.5f), new Vector3(2f, 1.06f, 0.5f)));
            Props!.AddChild(Bridge);
            return;
        }

        var materials = new Dictionary<ForestPropAsset, StandardMaterial3D>();
        var bounds = new Dictionary<ForestPropAsset, List<Aabb>>();
        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            var prop = placement.Prop;
            if (!materials.TryGetValue(prop, out var material))
            {
                materials[prop] = material = ForestAssets.CreatePropMaterial(prop);
                bounds[prop] = PartBounds(prop);
            }

            var parts = bounds[prop];
            var whole = Aabb.Empty;
            foreach (var part in parts)
                whole = whole.Merge(part);

            var node = ForestAssets.InstantiateProp(prop, material);
            node.Name = $"{prop.AssetId}_{i}";
            var yaw = float.DegreesToRadians(placement.YawDegrees);
            float y;
            if (i == 0)
            {
                // The bridge: the log's top just above the raised banks.
                y = valley.BridgeBankHeight + 0.14f - whole.Max.Y * placement.Scale.Y;
            }
            else
            {
                y = height(placement.Position.X, placement.Position.Y) - placement.Sink;
            }

            node.Position = new Vector3(placement.Position.X, y, placement.Position.Y);
            node.RotationDegrees = new Vector3(0f, placement.YawDegrees, 0f);
            node.Scale = placement.Scale;
            Props!.AddChild(node);

            if (i == 0)
            {
                Bridge = BridgeWalkway(valley, placement, whole);
                Props.AddChild(Bridge);
            }
            else if (placement.Collision)
            {
                Props.AddChild(Collider(placement, node.Position, yaw, parts));
            }
        }
    }

    private static List<Aabb> PartBounds(ForestPropAsset prop)
    {
        var list = new List<Aabb>();
        foreach (var (mesh, transform) in ForestAssets.LoadPropMeshes(prop))
            list.Add(mesh.Bounds.Transform(transform.ToMatrix4x4()));
        return list;
    }

    private static SurfaceBody3D Collider(PropPlacement placement, Vector3 position, float yaw, List<Aabb> parts)
    {
        // Bodies are never scaled: the scale goes into the boxes.
        var body = new SurfaceBody3D { Name = placement.Prop.AssetId + "_body", Surface = placement.Surface, Position = position, RotationDegrees = new Vector3(0f, float.RadiansToDegrees(yaw), 0f) };
        foreach (var part in parts)
        {
            // Slightly inside the visual (rough scans), at least 10 cm thick.
            var size = Vector3.Max(part.Size * placement.Scale * 0.9f, new Vector3(0.1f));
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = part.Center * placement.Scale });
        }

        return body;
    }

    private static SurfaceBody3D BridgeWalkway(ValleyGenerator valley, PropPlacement log, Aabb whole)
    {
        var length = whole.Size.X * log.Scale.X;
        var top = valley.BridgeBankHeight + 0.05f;
        var body = new SurfaceBody3D
        {
            Name = "BridgeWalkway",
            Surface = "wood",
            Position = new Vector3(log.Position.X, top - 0.15f, log.Position.Y),
            RotationDegrees = new Vector3(0f, log.YawDegrees, 0f),
        };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(length + 1.5f, 0.3f, 0.9f) } });
        return body;
    }

    private void BuildWalls()
    {
        var size = ValleyLayout.SizeMeters;
        var walls = new StaticBody3D { Name = "Walls" };
        (Vector3 Centre, Vector3 Size)[] boxes =
        [
            (new Vector3(-1f, 50f, size / 2f), new Vector3(2f, 200f, size + 4f)),
            (new Vector3(size + 1f, 50f, size / 2f), new Vector3(2f, 200f, size + 4f)),
            (new Vector3(size / 2f, 50f, -1f), new Vector3(size + 4f, 200f, 2f)),
            (new Vector3(size / 2f, 50f, size + 1f), new Vector3(size + 4f, 200f, 2f)),
        ];
        foreach (var (centre, extent) in boxes)
            walls.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = extent }, Position = centre });
        AddChild(walls);
    }

    // ── Player and audio ────────────────────────────────────────────────────

    private void ConnectPlayer(Terrain3D terrain)
    {
        if (Parent?.FindChildren<FirstPersonController>(owned: false) is not [var player, ..])
            return;
        player.SurfaceResolver = position => FootstepSurface(terrain.SurfaceTagAt(position.X, position.Z));
        var p = player.Position;
        player.Position = new Vector3(p.X, terrain.HeightAt(p.X, p.Z) + 0.02f, p.Z);
    }

    /// <summary>The footstep set for a terrain layer tag (needles sound like leaf litter).</summary>
    public static string? FootstepSurface(string? tag) => tag switch
    {
        null or "" => null,
        "needles" => "leaves",
        _ => tag,
    };

    private void ConnectAudio(ValleyGenerator valley)
    {
        if (Parent?.FindChildren<ForestAudio>(owned: false) is [var audio, ..])
            audio.PineDensity = p => valley.PineMask(p.X, p.Z);
    }
}
