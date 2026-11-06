using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// G8d.15, <c>forest-mini</c>: the Forest showcase's frame in miniature, built in code from engine assets only (no
/// <c>Examples/Forest</c> content). A 32 × 32 m Realistic <see cref="Terrain3D"/> (a shallow bowl with a rock outcrop)
/// drawn with a <see cref="TerrainSplatMaterial3D"/> of three generated 256² layers (grass, dirt along the stream, rock on
/// the steep ground), six fixed-seed Ez Tree trees (three Pine Medium, three Aspen Medium) with G8e.5's foliage (leaf
/// clusters, hierarchical wind, per-instance levels, the farthest as octahedral impostors), terrain grass on the grass
/// layer, a short carved <see cref="River3D"/>, a terrain-following <see cref="LightProbeVolume"/> baked at load, the
/// physical sky behind a low morning sun with staggered PCSS cascades and contact shadows, height fog, volumetric fog,
/// histogram auto exposure and glow, under TAAU at 0.75 (set by the host's scene table). Frame N is N / 60 s, so a
/// capture has a fixed wind phase. Self-checks at frame 10: the terrain under each tree, the stream carved and answering
/// water queries, grass drawn, the probes baked, the physical sky's LUTs. With <c>--alloc</c> the camera walks a loop
/// round the glade, one lap per <see cref="LapFrames"/> frames; on a CPU device (lavapipe) that walk runs without sun
/// shadows, as <c>tree-forest</c>'s fly-through does (the software rasterizer would take minutes per run).
/// </summary>
public sealed class ForestMiniScene(HostOptions host) : RenderTestGame(host)
{
    public const float Size = 32f;
    public const uint CheckFrame = 10;
    public const uint LapFrames = 150;
    private const int TextureSize = 256;
    private static readonly Vector3 Origin = new(-Size / 2f, 0f, -Size / 2f);

    /// <summary>Terrain-local tree positions (x, z) and species (0 pine, 1 aspen), away from the stream.</summary>
    private static readonly (float X, float Z, int Species, float Scale)[] Trees =
    [
        (5f, 5f, 0, 1.0f),
        (10.5f, 2.5f, 0, 0.85f),
        (26.5f, 9f, 0, 0.95f),
        (21f, 3.5f, 1, 1.0f),
        (4f, 15f, 1, 0.9f),
        (24f, 17.5f, 1, 0.8f),
    ];

    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private River3D _river = null!;
    private TreeScatter _scatter = null!;
    private LightProbeVolume _probes = null!;
    private WorldEnvironment _environment = null!;
    private bool Walk => Host.AllocationMeasuredFrames > 0;

    // The stream's centreline (terrain-local x at z): from the far edge (high) towards the camera (low).
    private static float StreamX(float z) => 15f + 3f * MathF.Sin(z * 0.17f + 0.4f);

    // A shallow bowl falling towards +z, the stream's valley, a rock outcrop at the back right, a little noise.
    internal static float Ground(float x, float z)
    {
        var across = x - StreamX(z);
        var rim = MathF.Max(MathF.Abs(x - 16f), MathF.Abs(z - 16f));
        var h = 1.2f + 0.05f * (Size - z)
                + 2.2f * SmoothStep(11f, 16f, rim)
                - 0.9f * MathF.Exp(-across * across / 7f)
                + 0.008f * across * across
                + 0.2f * MathF.Sin(x * 0.8f) * MathF.Cos(z * 0.6f);
        var outcrop = (x - 29f) * (x - 29f) + (z - 3f) * (z - 3f);
        return h + 4.5f / (1f + MathF.Exp((MathF.Sqrt(outcrop) - 6f) / 0.7f));
    }

    private static void Weights(TerrainGrid grid, float[] bed, float x, float z, Span<float> w)
    {
        var slope = 1f - grid.SmoothNormalAt(bed, x, z).Y;
        var rock = SmoothStep(0.16f, 0.26f, slope);
        var across = MathF.Abs(x - StreamX(z));
        var dirt = SmoothStep(3.2f, 1.8f, across) * (1f - rock);
        w[0] = MathF.Max(1f - rock - dirt, 0f) + 0.02f; // grass
        w[1] = dirt;
        w[2] = rock;
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(ForestMiniScene) };
        _camera = new Camera3D { Name = "Camera", Far = 300f, Fov = 60f };
        scene.AddChild(_camera);

        // Sun first: the physical sky follows it. A low morning sun from behind the trees, towards the camera's right.
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            Color = new Vector3(1f, 0.93f, 0.82f),
            Energy = 2.3f,
            ShadowCascades = 4,
            ShadowResolution = 1024,
            ShadowMaxDistance = 60f,
            ShadowSplitLambda = 0.8f,
            ShadowCacheMode = ShadowCacheMode.Staggered,
            LightAngularDistance = 0.5f,
            ContactShadows = true,
            ContactShadowLength = 0.4f,
        };
        sun.LookAt(Vector3.Normalize(new Vector3(0.62f, -0.38f, 0.69f)));
        if (Walk && DescribeDevice(Vulkan).DeviceType == nameof(Silk.NET.Vulkan.PhysicalDeviceType.Cpu))
            sun.CastsShadows = false;
        scene.AddChild(sun);

        _environment = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Physical, Turbidity = 6f, MieCoefficient = 0.005f, GroundColor = new Vector3(0.28f, 0.27f, 0.2f) },
            AmbientSource = AmbientSource.Sky,
            ReflectedLightSource = ReflectedLightSource.Sky,
            AmbientEnergy = 1.4f,
            WindDirection = new Vector3(-1f, 0f, 0.25f),
            WindStrength = 0.6f,
            WindFrequency = 0.45f,
            WindTurbulence = 0.4f,
            FogEnabled = true,
            FogLightColor = new Vector3(0.78f, 0.8f, 0.77f),
            FogDensity = 0.004f,
            FogHeight = 6f,
            FogHeightDensity = 0.05f,
            FogSunScatter = 0.06f,
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.012f,
            VolumetricFogAlbedo = new Vector3(1f, 1f, 0.95f),
            VolumetricFogAnisotropy = 0.65f,
            VolumetricFogLength = 40f,
            VolumetricFogSkyAffect = 0.15f,
            VolumetricFogAmbientInject = 0.08f,
            VolumetricFogNoiseScale = 8f,
            VolumetricFogNoiseStrength = 0.5f,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.Agx,
                AutoExposureEnabled = true,
                AutoExposureMode = AutoExposureMode.Histogram,
                AutoExposureScale = 0.16f,
                AutoExposureSpeed = 3f, // converged by the capture frame
                AutoExposureMinLuminance = 0.03f,
                AutoExposureHighlightProtection = true,
                AutoExposureHighlightWhite = 0.8f,
                GlowEnabled = true,
                GlowHdrThreshold = 4f,
                GlowHdrLuminanceCap = 3f,
            },
        };
        scene.AddChild(_environment);

        var data = TerrainData.Create(TerrainProfile.Realistic, (int)Size, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(Ground);
        var bed = data.BedHeights.ToArray();
        var grid = data.Grid;
        data.SetWeightsFrom((x, z, w) => Weights(grid, bed, x, z, w));
        data.FoliageTypes =
        [
            new FoliageType
            {
                ResourceName = "Grass",
                Mesh = GrassMesh.Clump(blades: 14, height: 0.4f, width: 0.05f, bend: 0.45f, seed: 1),
                Material = GrassMesh.CreateMaterial(),
                Density = 5f,
                LayerMask = 1, // the grass layer
                ScaleMin = 0.7f,
                ScaleMax = 1.3f,
                AlignToNormal = 0.4f,
                CullDistance = 30f,
                ThinBand = 12f,
                Subdivisions = 2,
            },
        ];
        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = Origin,
            Data = data,
            Material = new TerrainSplatMaterial3D
            {
                LayerTextureSize = TextureSize,
                DetailDistance = 30f,
                FarDistance = 60f,
                Layers =
                [
                    Layer("Grass", "grass", LayerTextures.Kind.Grass, 2.5f, 0.25f),
                    Layer("Dirt", "dirt", LayerTextures.Kind.Dirt, 2f, 0.15f),
                    Layer("Rock", "rock", LayerTextures.Kind.Rock, 5f, 0.2f),
                ],
            },
        };
        scene.AddChild(_terrain);

        // The stream: from the far edge towards the camera, fitted to the valley, then carved.
        var curve = new Curve3D();
        float[] zs = [0.5f, 8f, 16f, 24f, 31.5f];
        for (var i = 0; i < zs.Length; i++)
        {
            var p = Local(zs[i]);
            var tangent = (Local(zs[Math.Min(i + 1, zs.Length - 1)]) - Local(zs[Math.Max(i - 1, 0)])) / (i is 0 || i == zs.Length - 1 ? 3f : 6f);
            curve.AddPoint(p, -tangent, tangent);
            curve.SetPointWidth(i, 2f + 0.3f * MathF.Sin(i * 1.3f));
            curve.SetPointDepth(i, 0.4f);
        }

        _river = new River3D { Name = "Stream", Curve = curve };
        scene.AddChild(_river);

        // Six trees, G8e.5's foliage (as the Forest grows them): levels per tree, impostors from 30 m casting into the cascades.
        _scatter = new TreeScatter
        {
            Name = "Trees",
            ChunkSize = 8f,
            Lod1Distance = 16f,
            Lod2Distance = 26f,
            ImpostorDistance = 30f,
            ImpostorShadowDensity = 0.5f,
            LodSelection = TreeLodSelection.PerInstance,
            LodFadeMargin = 3f,
            ShadowCoarseLod = 3, // the impostor
            Species = [Species("Pine Medium", 13977), Species("Aspen Medium", 18020)],
        };
        scene.AddChild(_scatter);

        _probes = new LightProbeVolume
        {
            Name = "Lighting",
            Position = new Vector3(0f, 0f, 0f),
            Size = new Vector3(Size, 24f, Size),
            Layout = ProbeLayout.TerrainFollowing,
            Terrain = "../Terrain",
            ProbeSpacing = new Vector3(4f),
            LayerHeights = [0.5f, 2f, 5f, 10f, 18f],
            RaysPerProbe = 96,
            Bounces = 1,
            Energy = 4f,
            SkyOcclusion = 0.15f,
            OcclusionTint = System.Drawing.Color.FromArgb(255, 200, 225, 170),
        };
        scene.AddChild(_probes);

        Tree.ChangeScene(scene);
        if (!_river.FitToTerrain(0.3f))
            Fail("the stream found no terrain to fit to");
        if (!_river.Carve())
            Fail("the stream could not carve the terrain");

        // The trees on the carved ground.
        var placements = new TreePlacement[Trees.Length];
        for (var i = 0; i < Trees.Length; i++)
        {
            var (x, z, species, scale) = Trees[i];
            var world = new Vector3(x, 0f, z) + Origin;
            world.Y = _terrain.HeightAt(world.X, world.Z) - 0.05f; // sunk a little: the root flares meet the slope
            placements[i] = new TreePlacement(world, i * 1.37f, scale, species);
        }

        _scatter.SetPlacements(placements);

        var bake = _probes.Bake();
        Log.Info($"forest-mini probes baked: {bake.Probes} probes, {bake.Rays / 1e6:0.0}M rays, {bake.Elapsed.TotalMilliseconds:0} ms");
        PlaceCamera(0);

        static Vector3 Local(float z) => new Vector3(StreamX(z), 0f, z) + Origin;
    }

    private static TreeSpecies Species(string preset, int seed)
    {
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
        return new TreeSpecies { Options = options, Seeds = [seed] };
    }

    private static TerrainLayer Layer(string name, string tag, LayerTextures.Kind kind, float tiling, float contrast)
    {
        var (albedo, normal, orm) = LayerTextures.Generate(kind, TextureSize);
        return new TerrainLayer
        {
            Name = name,
            Tag = tag,
            Albedo = albedo,
            Normal = normal,
            Orm = orm,
            TilingMeters = tiling,
            HeightBlendContrast = contrast,
        };
    }

    private void PlaceCamera(uint frame)
    {
        if (Walk)
        {
            // A loop round the glade at eye height, looking across it: every tree changes level and the grass tiles thin.
            var angle = frame % LapFrames / (float)LapFrames * MathF.Tau;
            var eye = new Vector3(11f * MathF.Sin(angle), 0f, 11f * MathF.Cos(angle));
            _camera.Position = eye with { Y = _terrain.HeightAt(eye.X, eye.Z) + 1.7f };
            _camera.LookAt(new Vector3(-2f * MathF.Sin(angle), _terrain.HeightAt(0f, 0f) + 3f, -2f * MathF.Cos(angle)));
            return;
        }

        // From the near bank, looking up the stream into the glade and the low sun's haze.
        var position = new Vector3(21f, 0f, 30f) + Origin;
        _camera.Position = position with { Y = _terrain.HeightAt(position.X, position.Z) + 2.2f };
        var target = new Vector3(11f, 0f, 6f) + Origin;
        _camera.LookAt(target with { Y = _terrain.HeightAt(target.X, target.Z) + 4.5f });
    }

    // Per-instance levels draw with their own material copies, prepared on their first draw (as the Forest's prewarm
    // does): every batch is shown for frames 1–3, then each gets its range back.
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
        if (frame <= 4)
            Prewarm(frame);
        if (Walk)
            PlaceCamera((uint)frame);
        if (frame == CheckFrame)
            Check();
    }

    private void Check()
    {
        // The terrain under each tree.
        for (var i = 0; i < _scatter.PlacementCount; i++)
        {
            var p = _scatter.GetPlacement(i).Position;
            if (!_terrain.Contains(p.X, p.Z))
                Fail($"tree {i} at {p} is off the terrain");
            else if (MathF.Abs(_terrain.HeightAt(p.X, p.Z) - 0.05f - p.Y) > 0.01f)
                Fail($"tree {i} stands at {p.Y:0.00} m, the ground under it is at {_terrain.HeightAt(p.X, p.Z):0.00} m");
            if (_terrain.WaterDepthAt(p.X, p.Z) > 0f || Tree.Root.World3D.Water.WaterDepthAt(p) > 0f)
                Fail($"tree {i} stands in water");
        }

        if (_scatter.Batches.Count == 0)
            Fail("the scatter built no batches");
        else if (!_scatter.Batches.Any(b => b.IsImpostor))
            Fail("the scatter built no impostor level");

        // The stream: carved, registered as water, drawn with the water material.
        var water = Tree.Root.World3D.Water;
        var stream = new Vector3(StreamX(16f), 0f, 16f) + Origin;
        if (string.IsNullOrEmpty(_river.CarveId))
            Fail("the stream has no carve record");
        if (!(water.WaterDepthAt(stream) > 0.2f))
            Fail($"the stream's centre has {water.WaterDepthAt(stream):0.00} m of water");
        if (!(_terrain.HeightAt(stream.X, stream.Z) < water.SurfaceHeightAt(stream) - 0.2f))
            Fail("the stream's bed is not carved below its surface");
        if (_river.Ribbon?.MaterialOverride is not WaterMaterial3D)
            Fail("the stream does not draw with WaterMaterial3D");

        // The splat layers: grass in the glade, dirt on the stream's bank, rock on the outcrop.
        ExpectTag(new Vector2(8f, 24f), "grass", "the glade");
        ExpectTag(new Vector2(StreamX(10f) + 2.4f, 10f), "dirt", "the stream's bank");
        ExpectTag(new Vector2(29f - 6f, 3f), "rock", "the outcrop's face");

        if (_terrain.Foliage is not { } foliage || foliage.DrawnInstances == 0)
            Fail("no grass was drawn");
        if (_probes.Data is not { HasData: true })
            Fail("the probe volume has no bake");
        if (_environment.SkyEnvironment?.Luts is null)
            Fail("the physical sky has no LUTs");
    }

    private void ExpectTag(Vector2 local, string tag, string what)
    {
        var world = local + new Vector2(Origin.X, Origin.Z);
        var actual = _terrain.SurfaceTagAt(world.X, world.Y, ignoreWater: true);
        if (actual != tag)
            Fail($"{what}: SurfaceTagAt is '{actual}', expected '{tag}'");
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    protected override void DisposeScene()
    {
    }
}
