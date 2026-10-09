using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0151, vertex colours: a subdivided panel with a hue gradient, a box with a colour per corner, a white panel with
/// custom0 only (it must look like the plain white panel next to it) and a semi-transparent-coloured cutout panel.
/// </summary>
public sealed class VertexColorsScene(HostOptions host) : MeshSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        var white = new StandardMaterial3D { Specular = 0.2f };

        scene.AddChild(new MeshInstance3D
        {
            Name = "Gradient",
            Mesh = Panel(8, 4, (u, v) => Hue(u, v), custom: null),
            MaterialOverride = white,
            Position = new Vector3(-1.4f, 0.1f, 0),
        });

        // Box: each vertex coloured by its corner (r = x, g = y, b = z of the unit cube).
        var box = new BoxMesh { Size = new Vector3(1.2f) };
        var source = box.GetSurface(0);
        var colors = new Vector4[source.VertexCount];
        for (var i = 0; i < colors.Length; i++)
        {
            var p = source.Positions[i] / 1.2f + new Vector3(0.5f);
            colors[i] = new Vector4(p, 1f);
        }

        var coloured = new ArrayMesh();
        coloured.AddSurface(new MeshSurface(source.Positions, source.Normals, source.UVs, source.Indices) { Colors = colors });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Mesh = coloured,
            MaterialOverride = white,
            Position = new Vector3(1.2f, 0.6f, 0.3f),
            RotationDegrees = new Vector3(0, 35, 0),
        });

        // Custom0 only: the colour reads white, so the left panel must match the plain right one.
        scene.AddChild(new MeshInstance3D
        {
            Name = "Custom0Only",
            Mesh = Panel(1, 1, null, custom: new Vector4(0.3f, 1f, 0.5f, 0.7f), width: 0.8f, height: 0.6f),
            MaterialOverride = white,
            Position = new Vector3(-0.9f, 1.9f, -1f),
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Plain",
            Mesh = new QuadMesh { Size = new Vector2(0.8f, 0.6f) },
            MaterialOverride = white,
            Position = new Vector3(0.5f, 2.2f, -1f), // the quad is centred: x 0.1 .. 0.9, y 1.9 .. 2.5
        });

        // Vertex alpha cuts out (albedo alpha × vertex alpha), unlit.
        scene.AddChild(new MeshInstance3D
        {
            Name = "AlphaCut",
            Mesh = Panel(6, 1, (u, _) => new Vector4(1f, 0.8f, 0.2f, u), custom: null, width: 1.2f, height: 0.4f),
            MaterialOverride = new StandardMaterial3D { Transparency = AlphaMode.Cutout, AlphaCutoff = 0.5f, ShadingMode = ShadingMode.Unshaded, DoubleSided = true },
            Position = new Vector3(1.5f, 1.9f, -1f),
        });
    }

    private static Vector4 Hue(float u, float v)
    {
        var h = u * 6f;
        var c = new Vector3(MathF.Abs(h - 3f) - 1f, 2f - MathF.Abs(h - 2f), 2f - MathF.Abs(h - 4f));
        return new Vector4(Vector3.Clamp(c, Vector3.Zero, Vector3.One) * (0.4f + 0.6f * v), 1f);
    }

    /// <summary>A panel in the XY plane facing +Z (origin bottom-left), subdivided, with optional colours and custom0.</summary>
    internal static ArrayMesh Panel(int columns, int rows, Func<float, float, Vector4>? color, Vector4? custom, float width = 1.6f,
        float height = 1.2f)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Vector4>();
        var indices = new List<int>();
        for (var y = 0; y <= rows; y++)
        {
            for (var x = 0; x <= columns; x++)
            {
                float u = x / (float)columns, v = y / (float)rows;
                positions.Add(new Vector3(u * width, v * height, 0f));
                uvs.Add(new Vector2(u, 1f - v));
                if (color is not null)
                    colors.Add(color(u, v));
            }
        }

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var a = y * (columns + 1) + x;
                indices.AddRange([a, a + 1, a + columns + 2, a, a + columns + 2, a + columns + 1]);
            }
        }

        var normals = Enumerable.Repeat(Vector3.UnitZ, positions.Count).ToArray();
        var surface = new MeshSurface([.. positions], normals, [.. uvs], [.. indices]) { Colors = [.. colors] };
        if (custom is { } c)
            surface.Custom0 = Enumerable.Repeat(c, positions.Count).ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        return mesh;
    }
}

/// <summary>
/// ADR 0151, <see cref="MultiMesh"/>: <see cref="HostOptions.Count"/> boxes (default 1000) in one
/// <see cref="MultiMeshInstance3D"/>, a second multimesh drawing only 3 of its 10 instances, and two spheres with
/// visibility ranges (one beyond its end: hidden, in the main pass and the shadows). Frame 3 picks an instance (the node
/// comes back); frame 10 checks the counters: one draw per multimesh, one shadow draw each per pass, and nothing
/// allocated in steady frames (the allocation gate runs this scene with 50,000 instances). A
/// <see cref="Texture2DArray"/> with mipmaps is uploaded too (validation covers the 2D-array upload).
/// </summary>
public sealed class MultiMeshScene(HostOptions host) : MeshSceneBase(host)
{
    private const uint PickFrame = 3, CheckFrame = 10;

    private MultiMeshInstance3D _boxes = null!;
    private MultiMeshInstance3D _partial = null!;
    private Texture2DArrayGpu? _array;
    private Task<PickResult>? _pick;
    private bool _checked;

    protected override Vector3 CameraPosition => new(0, 14f, 24f);
    protected override Vector3 CameraTarget => new(0, 0, -2f);

    private int Count => Host.Count > 0 ? Host.Count : 1000;

    protected override void Build(Node3D scene)
    {
        var side = (int)MathF.Ceiling(MathF.Sqrt(Count));
        var spacing = 12f / side;
        var transforms = new Transform3D[Count];
        for (var i = 0; i < Count; i++)
        {
            int x = i % side, z = i / side;
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (i * 7 % 90) * MathF.PI / 180f);
            transforms[i] = Transform3D.FromTrs(new Vector3((x - side / 2f) * spacing, 0.2f + 0.15f * MathF.Sin(i * 0.37f), (z - side / 2f) * spacing),
                rotation, new Vector3(0.35f));
        }

        var multimesh = new MultiMesh
        {
            Mesh = new BoxMesh { Size = Vector3.One, Material = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 220, 160, 90) } },
            InstanceCount = Count,
        };
        multimesh.SetTransforms(transforms);
        _boxes = new MultiMeshInstance3D { Name = "Boxes", Multimesh = multimesh };
        scene.AddChild(_boxes);

        // Ten spheres, three drawn (VisibleInstanceCount), lifted by the node's own transform.
        var spheres = new MultiMesh { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f }, InstanceCount = 10, VisibleInstanceCount = 3 };
        for (var i = 0; i < 10; i++)
            spheres.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(-3f + i * 1.5f, 0f, 0f)));
        _partial = new MultiMeshInstance3D
        {
            Name = "Spheres",
            Multimesh = spheres,
            Position = new Vector3(0, 2.5f, 5f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 160, 230) },
        };
        scene.AddChild(_partial);

        // Visibility ranges: the camera is ~25 m away. The red sphere ends at 10 m (hidden), the green starts at 10 m.
        scene.AddChild(new MeshInstance3D
        {
            Name = "Hidden",
            Mesh = new SphereMesh { Radius = 1f, Height = 2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.Red },
            Position = new Vector3(4f, 3.5f, 4f),
            VisibilityRangeEnd = 10f,
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Shown",
            Mesh = new SphereMesh { Radius = 1f, Height = 2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 60, 200, 80) },
            Position = new Vector3(-4f, 3.5f, 4f),
            VisibilityRangeBegin = 10f,
            VisibilityRangeEnd = 1000f,
        });

        // A 2D-array texture with mips: 3 layers of 16×16.
        var layers = new byte[3][];
        for (var l = 0; l < 3; l++)
        {
            layers[l] = new byte[16 * 16 * 4];
            for (var p = 0; p < 16 * 16; p++)
                layers[l][p * 4 + l] = layers[l][p * 4 + 3] = 255;
        }

        _array = new Texture2DArrayGpu(Vulkan, Texture2DArray.FromImages(16, 16, layers), TextureColorSpace.Srgb);
        if (!_array.Update() || _array.Gpu is not { Image.ArrayLayers: 3 } gpu || gpu.Image.MipLevels < 2)
            Fail("the texture array was not uploaded with 3 layers and mips");
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var render = Servers.Render!;
        if (gameTime.FrameCount == PickFrame && Count >= 1)
        {
            // The first instance's centre: whole multimesh picks as its node.
            var first = Vector3.Transform(_boxes.Multimesh!.GetInstanceTransform(0).Origin, _boxes.ModelMatrix);
            if (Project(first) is { } pixel)
                _pick = render.PickAsync((int)pixel.X, (int)pixel.Y);
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        _checked = true;
        var stats = render.MeshStats;
        // Floor, the two multimeshes and both spheres are considered; the hidden one is out of range.
        if (stats.Instances != 5)
            Fail($"expected 5 mesh instances (floor, 2 multimeshes, 2 spheres), got {stats.Instances}");
        if (stats.OutOfRange != 1)
            Fail($"expected 1 instance outside its visibility range, got {stats.OutOfRange}");
        if (stats.MultiMeshInstances != Count + 3)
            Fail($"expected {Count + 3} multimesh instances drawn, got {stats.MultiMeshInstances}");
        if (stats.DrawCalls != 4)
            Fail($"expected 4 draws (floor, boxes, spheres, sphere), got {stats.DrawCalls}");
        var passes = render.ExistingShadows?.RenderedPasses ?? 0;
        if (passes is < 1 or > ShadowSystem.MaxCascades)
            Fail($"expected 1–{ShadowSystem.MaxCascades} cascade passes for one sun, got {passes}");
        if (stats.ShadowDrawCalls > 4 * passes)
            Fail($"shadow casters took {stats.ShadowDrawCalls} draws in {passes} passes (expected ≤ 4 per pass)");
        if (_pick is null || !_pick.IsCompletedSuccessfully)
            Fail($"the multimesh pick did not complete ({_pick?.Status})");
        else if (!ReferenceEquals(_pick.Result.Node, _boxes))
            Fail($"picking an instance returned {_pick.Result.Node?.Name ?? "nothing"} (id {_pick.Result.ObjectId}), expected Boxes");
    }

    protected override void DisposeScene()
    {
        _array?.Dispose();
        if (!_checked)
            Fail($"the multimesh self-check did not run (frame {CheckFrame})");
    }
}

/// <summary>
/// ADR 0151, <see cref="FoliageMaterial3D"/>: a 6 × 4 grid of cut-out leaf cards (one <see cref="MultiMesh"/>; the leaf
/// carries <c>Custom0</c> with y = 1, a wind weight rising to the tip and AO) and a bark column (a foliage material on a
/// mesh without the stream: it reads white and zero and must not move), under a sun whose shadows sway with the leaves.
/// The wind is fixed; time is the sum of the fixed 60 Hz deltas, so frame N is t = N / 60 s. <c>--count 1</c> turns the
/// wind off (the still reference).
/// </summary>
public sealed class FoliageWindScene(HostOptions host) : MeshSceneBase(host)
{
    protected override Vector3 CameraPosition => new(0.4f, 1.6f, 4.6f);
    protected override Vector3 CameraTarget => new(0, 1.0f, 0);

    protected override void Build(Node3D scene)
    {
        var environment = scene.GetNode<WorldEnvironment>("Environment");
        environment.WindDirection = new Vector3(1f, 0f, 0.3f);
        environment.WindStrength = Host.Count == 1 ? 0f : 1.5f;
        environment.WindFrequency = 0.5f;
        environment.WindTurbulence = 0.4f;

        // Leaf texture: a green ellipse, transparent outside (cut out at 0.5), with a lighter midrib.
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                var inside = u * u / 0.45f + v * v < 1f;
                var rib = MathF.Abs(u) < 0.06f;
                var i = (y * size + x) * 4;
                pixels[i] = (byte)(rib ? 150 : 70);
                pixels[i + 1] = (byte)(rib ? 200 : 150);
                pixels[i + 2] = (byte)(rib ? 90 : 50);
                pixels[i + 3] = (byte)(inside ? 255 : 0);
            }
        }

        var leafTexture = Texture2D.FromPixels(size, size, pixels,
            new TextureImportSettings { Mipmaps = true, Wrap = TextureWrap.Clamp });

        // One leaf card, 0.5 × 0.7 m, its base at the origin; custom0: weight 0.3 at the base to 1 at the tip, a leaf
        // (y = 1), a phase, AO 0.6 at the base to 1 at the tip. UV v = 0 at the tip (the flutter grows towards it).
        var leaf = VertexColorsScene.Panel(1, 2, (_, _) => Vector4.One, custom: null, width: 0.5f, height: 0.7f);
        var surface = leaf.GetSurface(0);
        var custom = new Vector4[surface.VertexCount];
        for (var i = 0; i < custom.Length; i++)
        {
            var t = surface.Positions[i].Y / 0.7f;
            custom[i] = new Vector4(0.3f + 0.7f * t, 1f, 0.37f, 0.6f + 0.4f * t);
            surface.Positions[i].X -= 0.25f;
        }

        surface.Custom0 = custom;
        surface.Colors = [];
        surface.NotifyChanged();

        var multimesh = new MultiMesh { Mesh = leaf, InstanceCount = 24 };
        for (var i = 0; i < 24; i++)
        {
            int column = i % 6, row = i / 6;
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (column * 23 + row * 41) % 70 * MathF.PI / 180f - 0.6f);
            multimesh.SetInstanceTransform(i, Transform3D.FromTrs(new Vector3(-1.6f + column * 0.62f, 0.15f + row * 0.62f, -0.3f * (row % 2)),
                yaw, Vector3.One));
        }

        scene.AddChild(new MultiMeshInstance3D
        {
            Name = "Leaves",
            Multimesh = multimesh,
            MaterialOverride = new FoliageMaterial3D
            {
                AlbedoTexture = leafTexture,
                Translucency = 0.6f,
                Roughness = 0.6f,
            },
        });

        // A point light behind the leaves: its cube shadows run the point foliage casters (the wind at push offset 0).
        scene.AddChild(new OmniLight3D
        {
            Name = "Lamp",
            Position = new Vector3(-0.4f, 1.4f, -1.3f),
            Color = new Vector3(1f, 0.85f, 0.6f),
            Energy = 1.2f,
            Range = 5f,
            CastsShadows = true,
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "Bark",
            Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.18f, Height = 2.6f },
            Position = new Vector3(2.3f, 1.3f, -0.4f),
            MaterialOverride = new FoliageMaterial3D
            {
                AlbedoColor = Color.FromArgb(255, 110, 80, 55),
                AlphaCutout = false,
                BackFace = FoliageBackFace.Cull,
                Translucency = 0f,
            },
        });
    }
}
