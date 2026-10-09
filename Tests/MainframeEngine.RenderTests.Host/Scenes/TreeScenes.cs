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
/// ADR 0158: a <see cref="TreeScatter"/> forest of oaks, pines and aspens (two seeds each) on a jittered grid:
/// <c>--count N</c> trees (default 160; a fly-through on a CPU device: at most 120, no sun shadows) over a square of <c>5.7 √N</c> m (2 000 trees:
/// 256 m). Captures see the forest
/// from its edge. With <c>--perf</c> or <c>--alloc</c> the camera flies through it at eye height on a loop of 600
/// frames (every chunk switches level on the way). Self-checks at frame 3 (and while flying, outside an allocation window):
/// each chunk's variant draws exactly one level for the camera, the batches hold every tree, and no level-2 batch casts
/// into the first sun cascade; the trees each cascade draws per level are printed on stdout.
/// </summary>
public sealed class TreeForestScene(HostOptions host) : TreeSceneBase(host)
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
                new TreeSpecies { Preset = "Oak Medium", Seeds = [35729, 1201] },
                new TreeSpecies { Preset = "Pine Medium", Seeds = [13977, 52] },
                new TreeSpecies { Preset = "Aspen Medium", Seeds = [18020, 777] },
            ],
        };
        _scatter.SetPlacements(Placements(_trees, size));
        scene.AddChild(_scatter);
        Tree.ChangeScene(scene);
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

    protected override void UpdateScene(in GameTime gameTime)
    {
        var frame = gameTime.FrameCount;
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

        // Checks allocate: before an allocation window (frames 3 and 50), every 100th frame of a perf run.
        if (frame == 3 || (Host.AllocationMeasuredFrames > 0 && frame == 50) || (Host.PerfMeasuredFrames > 0 && frame % 100 == 0))
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

        foreach (var (key, count) in shown)
            if (count != 1)
                Fail($"chunk {key.Item1} species {key.Item2} variant {key.Item3} draws {count} levels");
        if (trees != _trees)
            Fail($"the batches hold {trees} trees, not {_trees}");
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
