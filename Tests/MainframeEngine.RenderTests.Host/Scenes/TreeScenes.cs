using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>Shared set-up of the ADR 0158 tree scenes: procedural sky with sky lighting, a sun with shadows, a ground plane and wind.</summary>
public abstract class TreeSceneBase(HostOptions host) : RenderTestGame(host)
{
    protected Camera3D Camera { get; private set; } = null!;
    protected WorldEnvironment Environment { get; private set; } = null!;

    protected Node3D CreateWorld(string name, float groundSize, Vector3 cameraPosition, Vector3 cameraTarget, bool windy)
    {
        var scene = new Node3D { Name = name };
        Camera = new Camera3D { Name = "Camera", Position = cameraPosition, Far = 600f };
        Camera.LookAt(cameraTarget);
        scene.AddChild(Camera);

        Environment = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            AmbientSource = AmbientSource.Sky,
            WindDirection = new Vector3(1f, 0f, 0.3f),
            WindStrength = windy ? 1.2f : 0f,
            WindFrequency = 0.5f,
            WindTurbulence = 0.4f,
        };
        scene.AddChild(Environment);

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1.2f };
        sun.LookAt(Vector3.Normalize(new Vector3(-0.45f, -0.7f, -0.55f)));
        scene.AddChild(sun);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(groundSize) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = Color.FromArgb(255, 92, 104, 62),
                Roughness = 0.95f,
            },
        });
        return scene;
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// ADR 0158: an Oak Medium and a Pine Medium <see cref="Tree3D"/> side by side at level 0 under a fixed wind (frame 90:
/// t = 1.5 s), in <see cref="TreeStyle.Realistic"/> (PBR bark with the packed ORM map, cut-out translucent leaves,
/// swaying shadows) or <see cref="TreeStyle.LowPoly"/>. <c>--count 1</c> turns the wind off. Self-checks at frame 3:
/// three levels per tree with contiguous ranges, a trunk capsule, the bark and leaf materials of the style.
/// </summary>
public sealed class TreeScene(HostOptions host, TreeStyle style) : TreeSceneBase(host)
{
    private Tree3D _oak = null!;
    private Tree3D _pine = null!;

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeScene), 120f, new Vector3(2f, 7.5f, 34f), new Vector3(0f, 8.5f, 0f), windy: Host.Count != 1);
        _oak = new Tree3D { Name = "Oak", Preset = "Oak Medium", Style = style, Position = new Vector3(-7f, 0f, 0f), Lod1Distance = 500f, Lod2Distance = 600f };
        _pine = new Tree3D
        {
            Name = "Pine",
            Preset = "Pine Medium",
            Style = style,
            Position = new Vector3(8f, 0f, -2f),
            RotationDegrees = new Vector3(0f, 40f, 0f),
            Lod1Distance = 500f,
            Lod2Distance = 600f,
        };
        scene.AddChild(_oak);
        scene.AddChild(_pine);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3)
            return;
        foreach (var tree in (Tree3D[])[_oak, _pine])
        {
            if (tree.LodCount != 3)
            {
                Fail($"{tree.Name} has {tree.LodCount} levels");
                continue;
            }

            if (tree.GetLodNode(0).VisibilityRangeBegin != 0f || tree.GetLodNode(0).VisibilityRangeEnd != 500f ||
                tree.GetLodNode(1).VisibilityRangeBegin != 500f || tree.GetLodNode(2).VisibilityRangeBegin != 600f ||
                tree.GetLodNode(2).VisibilityRangeEnd != 0f)
                Fail($"{tree.Name}'s level ranges are not contiguous");
            if (tree.TrunkShape is not { Radius: > 0.1f, Height: > 1f })
                Fail($"{tree.Name} has no trunk capsule");
            var lod = tree.GetLodNode(0);
            var bark = lod.GetRenderMaterial(lod.Mesh!, 0);
            var leaves = lod.GetRenderMaterial(lod.Mesh!, 1);
            var expected = style == TreeStyle.Realistic
                ? bark is FoliageMaterial3D { OrmTexture: not null, ShadingMode: ShadingMode.Pbr } && leaves is FoliageMaterial3D { AlphaCutout: true }
                : bark is StandardMaterial3D && leaves is StandardMaterial3D && lod.Mesh!.GetSurface(1).Colors.Length > 0;
            if (!expected)
                Fail($"{tree.Name} draws with {bark.GetType().Name} / {leaves.GetType().Name}");
        }
    }
}

/// <summary>
/// ADR 0172: the same Oak Medium and Pine Medium as <see cref="TreeScene"/>, with leaf-cluster cards
/// (<see cref="TreeLeafMode.Cluster"/>: baked twig atlases with normal and thickness maps, kept rounded normals,
/// coverage-preserving mips), under the same wind at frame 90; <c>--count 1</c> turns the wind off. Self-checks at frame 3:
/// the leaves draw with the cluster material and at most a quarter of the single cards.
/// </summary>
public sealed class TreeClusterScene(HostOptions host) : TreeSceneBase(host)
{
    private Tree3D _oak = null!;
    private Tree3D _pine = null!;

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeClusterScene), 120f, new Vector3(2f, 7.5f, 34f), new Vector3(0f, 8.5f, 0f), windy: Host.Count != 1);
        _oak = new Tree3D { Name = "Oak", Options = Clusters("Oak Medium"), Position = new Vector3(-7f, 0f, 0f), Lod1Distance = 500f, Lod2Distance = 600f };
        _pine = new Tree3D
        {
            Name = "Pine",
            Options = Clusters("Pine Medium"),
            Position = new Vector3(8f, 0f, -2f),
            RotationDegrees = new Vector3(0f, 40f, 0f),
            Lod1Distance = 500f,
            Lod2Distance = 600f,
        };
        scene.AddChild(_oak);
        scene.AddChild(_pine);
        Tree.ChangeScene(scene);
    }

    private static TreeOptions Clusters(string preset)
    {
        var options = TreePresets.Load(preset);
        options.LeafMode = TreeLeafMode.Cluster;
        return options;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3)
            return;
        foreach (var (tree, preset) in ((Tree3D, string)[])[(_oak, "Oak Medium"), (_pine, "Pine Medium")])
        {
            var lod = tree.GetLodNode(0);
            var leaves = lod.GetRenderMaterial(lod.Mesh!, 1);
            if (leaves is not FoliageMaterial3D { ThicknessTexture: not null, BackFace: FoliageBackFace.Keep })
                Fail($"{tree.Name}'s leaves draw with {leaves.GetType().Name}, not the cluster material");
            var single = TreeMesh.Generate(TreePresets.Load(preset), tree.Options!.Seed, TreeStyle.Realistic).Lods[0].GetSurface(1).VertexCount;
            var cards = lod.Mesh!.GetSurface(1).VertexCount;
            if (cards * 4 > single)
                Fail($"{tree.Name} has {cards / 4} cluster cards for {single / 4} single cards");
        }
    }
}

/// <summary>
/// ADR 0172: the engine's species at level 0 — Birch, Beech, Spruce and Fir Medium (procedural leaves and bark,
/// <see cref="TreeLevel.LengthProfile"/> crowns) — in a light wind at t = 1.5 s. Self-checks at frame 3: each draws with
/// its painted leaf image and bark, and the conifers are taller than wide.
/// </summary>
public sealed class TreeSpeciesScene(HostOptions host) : TreeSceneBase(host)
{
    private static readonly string[] Names = ["Birch Medium", "Beech Medium", "Spruce Medium", "Fir Medium"];
    private readonly Tree3D[] _trees = new Tree3D[Names.Length];

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeSpeciesScene), 160f, new Vector3(0f, 9f, 50f), new Vector3(0f, 9f, 0f), windy: true);
        Environment.WindStrength = 0.5f;
        for (var i = 0; i < Names.Length; i++)
        {
            _trees[i] = new Tree3D
            {
                Name = Names[i].Replace(" ", "", StringComparison.Ordinal),
                Preset = Names[i],
                Position = new Vector3(-19.5f + 13f * i, 0f, i % 2 == 0 ? 0f : -3f),
                Lod1Distance = 500f,
                Lod2Distance = 600f,
                Collision = false,
            };
            scene.AddChild(_trees[i]);
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3)
            return;
        foreach (var tree in _trees)
        {
            var lod = tree.GetLodNode(0);
            if (lod.GetRenderMaterial(lod.Mesh!, 1) is not FoliageMaterial3D { AlbedoTexture: not null })
                Fail($"{tree.Name}'s leaves have no image");
            if (lod.GetRenderMaterial(lod.Mesh!, 0) is not FoliageMaterial3D { AlbedoTexture: not null, NormalTexture: not null })
                Fail($"{tree.Name}'s bark has no maps");
        }

        foreach (var conifer in _trees[2..])
        {
            var bounds = conifer.GetLodNode(0).Mesh!.Bounds;
            if (bounds.Size.Y < 1.4f * MathF.Max(bounds.Size.X, bounds.Size.Z))
                Fail($"{conifer.Name} is not conical: {bounds.Size}");
        }
    }
}

/// <summary>
/// ADR 0172: bark detail close up — an Oak Medium's lower trunk with a root flare (buttress lobes), branch collars, moss on
/// up-facing bark and detail normals (<c>--count 1</c>: Ez Tree's plain bark, for comparison). No wind. Self-checks at
/// frame 3: the flared trunk is wider at the ground than a metre up, and the bark material has moss and detail normals.
/// </summary>
public sealed class TreeBarkScene(HostOptions host) : TreeSceneBase(host)
{
    private Tree3D _oak = null!;

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeBarkScene), 40f, new Vector3(2.6f, 1.7f, 4.2f), new Vector3(0f, 1.4f, 0f), windy: false);
        var options = TreePresets.Load("Oak Medium");
        if (Host.Count != 1)
        {
            options.RootFlare = 1.8;
            options.RootFlareHeight = 1.1;
            options.CollarScale = 1.35;
            options.BarkMoss = 0.55f;
            options.BarkDetailScale = 5f;
        }

        _oak = new Tree3D { Name = "Oak", Options = options, Lod1Distance = 500f, Lod2Distance = 600f, Collision = false };
        scene.AddChild(_oak);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3 || Host.Count == 1)
            return;
        var bark = _oak.GetLodNode(0).Mesh!.GetSurface(0);
        float Width(float y) => bark.Positions.Where(p => MathF.Abs(p.Y - y) < 0.12f).Select(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z)).DefaultIfEmpty(0f).Max();
        if (Width(0f) < Width(1.2f) * 1.4f)
            Fail($"the trunk is not flared: {Width(0f):0.00} m at the ground, {Width(1.2f):0.00} m at 1.2 m");
        if (_oak.GetLodNode(0).GetRenderMaterial(_oak.GetLodNode(0).Mesh!, 0) is not FoliageMaterial3D { MossCoverage: > 0f, DetailScale: > 0f })
            Fail("the bark has no moss or detail normals");
    }
}

/// <summary>
/// ADR 0172: the oak and pine of <see cref="TreeScene"/> with the hierarchical wind (<see cref="TreeOptions.HierarchicalWind"/>:
/// pivot streams, trunk sway, branch and twig bends, leaf flutter) and leaf clusters from level 0, in a strong wind.
/// <c>--count 1</c>: still; <c>--count 2</c>: the wind 1.25 s later (the capture frame is the test's). Self-checks at
/// frame 3: both surfaces carry the pivot streams (rigid trunk pivots, flexible branch pivots).
/// </summary>
public sealed class TreeWindScene(HostOptions host) : TreeSceneBase(host)
{
    private Tree3D _oak = null!;
    private Tree3D _pine = null!;

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeWindScene), 120f, new Vector3(2f, 7.5f, 34f), new Vector3(0f, 8.5f, 0f), windy: Host.Count != 1);
        Environment.WindStrength = Host.Count == 1 ? 0f : 2.5f;
        _oak = new Tree3D { Name = "Oak", Options = Hierarchical("Oak Medium"), Position = new Vector3(-7f, 0f, 0f), Lod1Distance = 500f, Lod2Distance = 600f };
        _pine = new Tree3D
        {
            Name = "Pine",
            Options = Hierarchical("Pine Medium"),
            Position = new Vector3(8f, 0f, -2f),
            RotationDegrees = new Vector3(0f, 40f, 0f),
            Lod1Distance = 500f,
            Lod2Distance = 600f,
        };
        scene.AddChild(_oak);
        scene.AddChild(_pine);
        Tree.ChangeScene(scene);
    }

    private static TreeOptions Hierarchical(string preset)
    {
        var options = TreePresets.Load(preset);
        options.HierarchicalWind = true;
        options.LeafMode = TreeLeafMode.Cluster;
        return options;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3)
            return;
        foreach (var tree in (Tree3D[])[_oak, _pine])
        {
            var mesh = tree.GetLodNode(0).Mesh!;
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                if (surface.Custom1.Length != surface.VertexCount || surface.Custom2.Length != surface.VertexCount)
                    Fail($"{tree.Name} surface {s} has no pivot streams");
            }

            var bark = mesh.GetSurface(0);
            if (!bark.Custom1.Any(c => c.W >= TreeGenerator.RigidStiffness) || !bark.Custom1.Any(c => c.W is > 0f and < 100f))
                Fail($"{tree.Name}'s bark lacks a rigid trunk or flexible branches");
        }
    }
}

/// <summary>
/// ADR 0172: an oak, a pine and an aspen (a <see cref="TreeScatter"/>, one tree each) seen from 62 m, drawn as their
/// finest mesh level, or (<c>--count 1</c>) as their octahedral impostors (<see cref="TreeScatter.ImpostorDistance"/>),
/// sun shadows from the impostor casters in every pass, no wind. The two captures should match: the test compares them.
/// Self-checks at frame 3: the impostor run draws only impostor batches, with impostor materials.
/// </summary>
public sealed class TreeImpostorScene(HostOptions host) : TreeSceneBase(host)
{
    private TreeScatter _scatter = null!;
    private bool Impostors => Host.Count == 1;

    protected override void LoadScene()
    {
        var scene = CreateWorld(nameof(TreeImpostorScene), 260f, new Vector3(0f, 9f, 62f), new Vector3(0f, 8.5f, 0f), windy: false);
        _scatter = new TreeScatter
        {
            Name = "Trees",
            Species =
            [
                new TreeSpecies { Preset = "Oak Medium", Seeds = [35729] },
                new TreeSpecies { Preset = "Pine Medium", Seeds = [13977] },
                new TreeSpecies { Preset = "Aspen Medium", Seeds = [18020] },
            ],
            Lod1Distance = 1000f,
            Lod2Distance = 2000f,
            ImpostorDistance = Impostors ? 1f : 0f,
            ShadowMaxLod = 3,
            Collision = false,
        };
        _scatter.SetPlacements(
        [
            new TreePlacement(new Vector3(-13f, 0f, 0f), 0f, 1f, 0),
            new TreePlacement(new Vector3(1f, 0f, -2f), 0.7f, 1f, 1),
            new TreePlacement(new Vector3(13f, 0f, 1f), 2.1f, 1f, 2),
        ]);
        scene.AddChild(_scatter);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3 || !Impostors)
            return;
        var camera = Camera.GlobalPosition;
        var drawn = 0;
        foreach (var batch in _scatter.Batches)
        {
            var bounds = (batch.CustomAabb ?? batch.Multimesh!.GetAabb()).Transform(batch.GlobalTransform.ToMatrix4x4());
            if (!batch.IsInVisibilityRange(camera, bounds))
                continue;
            drawn++;
            if (!batch.IsImpostor || batch.GetRenderMaterial(batch.Multimesh!.Mesh!, 0) is not ImpostorMaterial3D)
                Fail($"{batch.Name} draws instead of the impostor");
        }

        if (drawn != 3)
            Fail($"{drawn} impostor batches draw, not 3");
    }
}

/// <summary>
/// ADR 0158: a <see cref="TreeScatter"/> forest of oaks, pines and aspens (two seeds each) on a jittered grid:
/// <c>--count N</c> trees (default 160; a fly-through on a CPU device: at most 120, no sun shadows) over a square of <c>5.7 √N</c> m (2 000 trees:
/// 256 m). Captures see the forest
/// from its edge. With <c>--perf</c> or <c>--alloc</c> the camera flies through it at eye height on a loop of 600
/// frames (every chunk switches level on the way). Self-checks at frame 3 (and while flying, outside an allocation window):
/// each chunk's variant draws exactly one level for the camera, the batches hold every tree, and no level-2 batch casts
/// into the first sun cascade; the trees each cascade draws per level are printed on stdout.
/// <para>
/// <c>tree-forest-g8e</c> (<paramref name="foliageQuality"/>, ADR 0172) grows the same forest with G8e.5's foliage, as the
/// Forest does: leaf clusters from level 1 (with a shadow density), hierarchical wind, root flares, collars, moss and
/// detail normals, coverage mips, octahedral impostors from 60 m casting into the coarse cascades, and per-instance
/// levels cross-fading over ±4 m. Its checks are that every tree is held and that levels are chosen per instance.
/// </para>
/// </summary>
public sealed class TreeForestScene(HostOptions host, bool foliageQuality = false) : TreeSceneBase(host)
{
    private TreeScatter _scatter = null!;
    private float _half;
    private bool Flying => Host.PerfMeasuredFrames > 0 || Host.AllocationMeasuredFrames > 0;
    private int _trees;

    /// <summary>
    /// Most trees of a fly-through on a CPU device (lavapipe), which also flies without sun shadows: the software
    /// rasterizer would take minutes per run. Captures (the golden) keep the full scene.
    /// </summary>
    private const int CpuDeviceMaxTrees = 120;

    protected override void LoadScene()
    {
        _trees = Host.Count > 0 ? Host.Count : 160;
        var light = Flying && DescribeDevice(Vulkan).DeviceType == nameof(Silk.NET.Vulkan.PhysicalDeviceType.Cpu);
        if (light)
            _trees = Math.Min(_trees, CpuDeviceMaxTrees);
        var size = MathF.Round(5.725f * MathF.Sqrt(_trees));
        _half = size * 0.5f;
        var scene = CreateWorld(nameof(TreeForestScene), size + 80f, new Vector3(_half + 38f, 15f, _half * 0.5f), new Vector3(0f, 5f, 0f), windy: true);
        if (light)
            scene.GetNode<DirectionalLight3D>("Sun").CastsShadows = false;
        Environment.FogEnabled = true;
        Environment.FogDensity = 0.002f;

        _scatter = new TreeScatter
        {
            Name = "Forest",
            Species =
            [
                Species("Oak Medium", 35729, 1201),
                Species("Pine Medium", 13977, 52),
                Species("Aspen Medium", 18020, 777),
            ],
        };
        if (foliageQuality)
        {
            _scatter.ImpostorDistance = 60f;
            _scatter.ImpostorShadowDensity = 0.5f;
            _scatter.LodSelection = TreeLodSelection.PerInstance;
            _scatter.LodFadeMargin = 4f;
            _scatter.ShadowCoarseLod = 3; // the impostor
        }

        _scatter.SetPlacements(Placements(_trees, size));
        scene.AddChild(_scatter);
        Tree.ChangeScene(scene);
    }

    private TreeSpecies Species(string preset, params int[] seeds)
    {
        if (!foliageQuality)
            return new TreeSpecies { Preset = preset, Seeds = seeds };
        var options = TreePresets.Load(preset);
        options.LeafMode = TreeLeafMode.Cluster;
        options.ClusterFromLod = 1;
        options.ClusterShadowDensity = 0.6f;
        options.LeafCoverageMips = true;
        options.HierarchicalWind = true;
        options.RootFlare = 1.6;
        options.CollarScale = 1.25;
        options.BarkMoss = 0.4f;
        options.BarkDetailScale = 6f;
        return new TreeSpecies { Options = options, Seeds = seeds };
    }

    /// <summary>A jittered grid of <paramref name="count"/> trees over a <paramref name="size"/> m square centred on the origin.</summary>
    internal static TreePlacement[] Placements(int count, float size)
    {
        var columns = (int)MathF.Ceiling(MathF.Sqrt(count));
        var cell = size / columns;
        var placements = new TreePlacement[count];
        for (var i = 0; i < count; i++)
        {
            int cx = i % columns, cz = i / columns;
            float Hash(int salt) => (TreeScatter.VariantOf(new Vector3(cx * 7.31f + salt, 0f, cz * 3.17f - salt), 10007) + 0.5f) / 10007f;
            var position = new Vector3((cx + 0.15f + 0.7f * Hash(1)) * cell - size * 0.5f, 0f, (cz + 0.15f + 0.7f * Hash(2)) * cell - size * 0.5f);
            var species = Hash(3) < 0.45f ? 0 : Hash(3) < 0.8f ? 1 : 2;
            placements[i] = new TreePlacement(position, Hash(4) * MathF.Tau, 0.8f + 0.4f * Hash(5), species);
        }

        return placements;
    }

    // tree-forest-g8e: every batch shown for frames 1–3 (each per-instance level draws with its own material copy, so its
    // GPU set and pipelines are created at load, as the Forest's prewarm does), then each gets its range back.
    private readonly List<(TreeScatterBatch3D Batch, float Begin, float End)> _prewarm = [];

    private void Prewarm(ulong frame)
    {
        if (frame == 1)
        {
            foreach (var batch in _scatter.Batches)
            {
                _prewarm.Add((batch, batch.VisibilityRangeBegin, batch.VisibilityRangeEnd));
                batch.VisibilityRangeBegin = 0f;
                batch.VisibilityRangeEnd = 0f;
            }
        }
        else if (frame == 4)
        {
            foreach (var (batch, begin, end) in _prewarm)
            {
                batch.VisibilityRangeBegin = begin;
                batch.VisibilityRangeEnd = end;
            }

            _prewarm.Clear();
        }
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var frame = gameTime.FrameCount;
        if (foliageQuality && frame <= 4)
            Prewarm(frame);
        if (Flying)
        {
            // A loop at eye height through the forest, looking ahead along the path.
            var t = frame % 600 / 600f * MathF.Tau;
            var radius = _half * 0.6f;
            var position = new Vector3(radius * MathF.Cos(t), 1.8f, radius * 0.7f * MathF.Sin(t));
            var ahead = new Vector3(radius * MathF.Cos(t + 0.15f), 2.5f, radius * 0.7f * MathF.Sin(t + 0.15f));
            Camera.Position = position;
            Camera.LookAt(ahead);
        }

        // Checks allocate: before an allocation window (frames 3 and 50; 5 with the prewarm), every 100th frame of a perf run.
        if (frame == (foliageQuality ? 5u : 3u) || (Host.AllocationMeasuredFrames > 0 && frame == 50) || (Host.PerfMeasuredFrames > 0 && frame % 100 == 0))
            Check();
    }

    private void Check()
    {
        var batches = _scatter.Batches;
        if (batches.Count == 0)
        {
            Fail("the scatter built no batches");
            return;
        }

        var camera = Camera.GlobalPosition;
        var trees = 0;
        var shown = new Dictionary<(Vector2I, int, int), int>();
        foreach (var batch in batches)
        {
            var key = (batch.Chunk, batch.Species, batch.Variant);
            shown.TryAdd(key, 0);
            var bounds = (batch.CustomAabb ?? batch.Multimesh!.GetAabb()).Transform(batch.GlobalTransform.ToMatrix4x4());
            if (batch.IsInVisibilityRange(camera, bounds))
                shown[key]++;
            if (batch.Lod == 0)
                trees += batch.Multimesh!.InstanceCount;
        }

        if (foliageQuality)
        {
            // Per instance (ADR 0172): every chunk draws at least one level, and the levels choose their trees.
            foreach (var (key, count) in shown)
                if (count < 1)
                    Fail($"chunk {key.Item1} species {key.Item2} variant {key.Item3} draws no level");
            if (!batches.Any(b => b.IsImpostor))
                Fail("the scatter built no impostor level");
        }
        else
        {
            foreach (var (key, count) in shown)
                if (count != 1)
                    Fail($"chunk {key.Item1} species {key.Item2} variant {key.Item3} draws {count} levels");
        }

        if (trees != _trees)
            Fail($"the batches hold {trees} trees, not {_trees}");
        if (!foliageQuality)
            CheckCascades(camera);
    }

    // Which levels' casters each sun cascade culls in (the planner's passes of the last frame): far chunks must stay out
    // of the near cascades. Reports the counts (trees per cascade and level) on stdout.
    private void CheckCascades(Vector3 camera)
    {
        if (Servers.Render?.ExistingShadows?.Planner is not { } planner)
            return;
        Span<int> trees = stackalloc int[4 * 3];
        foreach (var pass in planner.Passes)
        {
            if (pass.Kind != ShadowPassKind.Cascade || pass.Slot >= 4)
                continue;
            foreach (var batch in _scatter.Batches)
            {
                var bounds = (batch.CustomAabb ?? batch.Multimesh!.GetAabb()).Transform(batch.GlobalTransform.ToMatrix4x4());
                if (!batch.CastShadows || !batch.IsInVisibilityRange(camera, bounds) || !pass.Frustum.Intersects(bounds))
                    continue;
                trees[pass.Slot * 3 + Math.Min(batch.Lod, 2)] += batch.Multimesh!.InstanceCount;
                if (pass.Slot == 0 && batch.Lod == 2)
                    Fail($"a level-2 batch ({batch.Name}) casts into cascade {pass.Slot}");
            }
        }

        Console.WriteLine($"[tree-forest] casters per cascade (LOD0/1/2 trees): c0 {trees[0]}/{trees[1]}/{trees[2]}, " +
                          $"c1 {trees[3]}/{trees[4]}/{trees[5]}, c2 {trees[6]}/{trees[7]}/{trees[8]}, c3 {trees[9]}/{trees[10]}/{trees[11]}");
    }
}

/// <summary>
/// ADR 0179, the coarse shadow level's hand-off: a stand of aspens and pines 26–52 m from the camera, a low sun behind them
/// throwing their shadows towards it over the ground. Levels switch at 10 and 20 m, impostors are drawn from 60 m and
/// only levels 0–1 cast (<see cref="TreeScatter.ShadowMaxLod"/> 1), so every tree is drawn at level 2 and casts only
/// through its impostor (<see cref="TreeScatter.ShadowCoarseLod"/> 3), which takes over at 20 m, where level 1 stops,
/// not at 60 m: before ADR 0179 these trees cast nothing into the cascades. <c>--count 1</c>: the reference, every level
/// casting where it is drawn (level 2's meshes); <c>--count 2</c>: the trees cast no shadow. Self-checks at frame 3: the
/// impostor batches hand off at 20 m and are in their shadow range, the level-2 batches cast nothing.
/// </summary>
public sealed class TreeShadowHandOffScene(HostOptions host) : TreeSceneBase(host)
{
    public const float HandOff = 20f;
    private TreeScatter _scatter = null!;

    protected override void LoadScene()
    {
        // Looking down at the ground the shadows fall on, the stand at the top of the frame.
        var scene = CreateWorld(nameof(TreeShadowHandOffScene), 200f, new Vector3(0f, 14f, 30f), new Vector3(0f, 0f, -8f), windy: false);
        var sun = scene.GetNode<DirectionalLight3D>("Sun");
        sun.LookAt(new Vector3(-0.15f, -0.45f, 1f)); // 24° up, from behind the stand towards the camera
        sun.ShadowMaxDistance = 80f;
        _scatter = new TreeScatter
        {
            Name = "Trees",
            Species =
            [
                new TreeSpecies { Preset = "Aspen Medium", Seeds = [18020] },
                new TreeSpecies { Preset = "Pine Medium", Seeds = [13977] },
            ],
            Lod1Distance = 10f,
            Lod2Distance = HandOff,
            ImpostorDistance = 60f,
            ImpostorFrames = 6,
            ImpostorResolution = 96,
            LodSelection = TreeLodSelection.PerInstance,
            LodFadeMargin = 0f,
            ShadowMaxLod = Host.Count == 1 ? 8 : 1,
            ShadowCoarseLod = Host.Count == 1 ? -1 : 3,
            CastShadows = Host.Count != 2,
            Collision = false,
        };
        var placements = new List<TreePlacement>();
        for (var row = 0; row < 3; row++)
        {
            for (var i = 0; i < 7; i++)
            {
                var x = (i - 3) * 6f + (row % 2) * 3f;
                placements.Add(new TreePlacement(new Vector3(x, 0f, -row * 7f), i * 1.3f + row, 0.9f + 0.05f * ((i + row) % 3), (i + row) % 2));
            }
        }

        _scatter.SetPlacements([.. placements]);
        scene.AddChild(_scatter);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != 3 || Host.Count != 0)
            return;
        var camera = Camera.GlobalPosition;
        var impostors = 0;
        foreach (var batch in _scatter.Batches)
        {
            var bounds = (batch.CustomAabb ?? batch.Multimesh!.GetAabb()).Transform(batch.GlobalTransform.ToMatrix4x4());
            if (batch.IsImpostor)
            {
                impostors++;
                if (!batch.HasShadowRange || batch.ShadowBegin != HandOff)
                    Fail($"{batch.Name} hands off at {batch.ShadowBegin} m, not {HandOff} m");
                if (batch.IsInVisibilityRange(camera, bounds) || !batch.IsInShadowRange(camera, bounds))
                    Fail($"{batch.Name}: the impostor should cast here without being drawn");
            }
            else if (batch.Lod == 2 && batch.CastShadows)
            {
                Fail($"{batch.Name}: level 2 casts (ShadowMaxLod 1)");
            }
        }

        if (impostors == 0)
            Fail("no impostor batches");
    }
}
