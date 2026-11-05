using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>G6.5: the Decal node, the per-view selection (frustum, fades, nearest MaxDecals), instance masks, the UBO entry and the atlas packer.</summary>
public sealed class DecalTests
{
    private static readonly Texture2D Pixel = Texture2D.FromPixels(1, 1, [255, 255, 255, 255]);

    private static (Node3D Root, List<Decal> Decals) World(params Decal[] decals)
    {
        var root = new Node3D { Name = "Root" };
        foreach (var decal in decals)
            root.AddChild(decal);
        return (root, [.. decals]);
    }

    private static Frustum LookingDownZ() =>
        new(Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY) *
            Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 1000f));

    [Fact]
    public void DefaultsFollowGodot()
    {
        var decal = new Decal();
        Assert.Equal(new Vector3(2, 2, 2), decal.Size);
        Assert.Equal(1f, decal.AlbedoMix);
        Assert.Equal(0.3f, decal.UpperFade);
        Assert.Equal(0.3f, decal.LowerFade);
        Assert.Equal(0f, decal.NormalFade);
        Assert.Equal(1f, decal.EmissionEnergy);
        Assert.False(decal.DistanceFadeEnabled);
        Assert.Equal(40f, decal.DistanceFadeBegin);
        Assert.Equal(10f, decal.DistanceFadeLength);
        Assert.Equal(32, ShaderLimits.MaxDecals);
    }

    [Fact]
    public void DistanceFadeRampsOverItsLength()
    {
        var decal = new Decal { DistanceFadeEnabled = true, DistanceFadeBegin = 10f, DistanceFadeLength = 4f };
        Assert.Equal(1f, decal.DistanceFadeAt(5f));
        Assert.Equal(0.5f, decal.DistanceFadeAt(12f), 5);
        Assert.Equal(0f, decal.DistanceFadeAt(20f));
        Assert.Equal(1f, new Decal().DistanceFadeAt(1e6f));
    }

    [Fact]
    public void WorldBoundsFollowSizeAndTransform()
    {
        var decal = new Decal { Size = new Vector3(2, 1, 4), Position = new Vector3(10, 0, 0) };
        var (root, _) = World(decal);
        try
        {
            var bounds = decal.WorldBounds;
            Assert.Equal(new Vector3(9, -0.5f, -2), bounds.Min);
            Assert.Equal(new Vector3(11, 0.5f, 2), bounds.Max);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void SelectionSkipsDecalsWithoutTexturesOutsideTheFrustumOrFadedOut()
    {
        var visible = new Decal { Name = "Visible", TextureAlbedo = Pixel };
        var bare = new Decal { Name = "Bare" };
        var behind = new Decal { Name = "Behind", TextureAlbedo = Pixel, Position = new Vector3(0, 0, 30) };
        var faded = new Decal { Name = "Faded", TextureAlbedo = Pixel, DistanceFadeEnabled = true, DistanceFadeBegin = 1f, DistanceFadeLength = 1f };
        var hidden = new Decal { Name = "Hidden", TextureNormal = Pixel, Visible = false };
        var (root, decals) = World(visible, bare, behind, faded, hidden);
        try
        {
            var selection = new DecalSelection();
            selection.Select(decals, LookingDownZ(), new Vector3(0, 0, 10), null);
            Assert.Equal(1, selection.Count);
            Assert.Same(visible, selection.Selected[0]);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void MoreThanTheLimitKeepsTheNearest()
    {
        // 40 decals along -Z; the camera at z = 10 looks down -Z: the 32 nearest are z = 0 .. -31.
        var all = Enumerable.Range(0, 40).Select(i => new Decal { Name = $"D{i}", TextureAlbedo = Pixel, Size = Vector3.One, Position = new Vector3(0, 0, -i) }).ToArray();
        var (root, decals) = World([.. all.Reverse()]);
        try
        {
            var selection = new DecalSelection();
            selection.Select(decals, LookingDownZ(), new Vector3(0, 0, 10), null);
            Assert.Equal(ShaderLimits.MaxDecals, selection.Count);
            var kept = selection.Selected.ToArray().Select(d => int.Parse(d.Name[1..], System.Globalization.CultureInfo.InvariantCulture)).Order();
            Assert.Equal(Enumerable.Range(0, ShaderLimits.MaxDecals), kept);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void MasksNameTheDecalsAnInstanceTouches()
    {
        var left = new Decal { Name = "Left", TextureAlbedo = Pixel, Position = new Vector3(-3, 0, 0) };
        var right = new Decal { Name = "Right", TextureAlbedo = Pixel, Position = new Vector3(3, 0, 0) };
        var (root, decals) = World(left, right);
        try
        {
            var selection = new DecalSelection();
            selection.Select(decals, LookingDownZ(), new Vector3(0, 0, 10), null);
            Assert.Equal(0b01u, selection.MaskFor(new Aabb(new Vector3(-4.5f, -1, -1), new Vector3(-3.5f, 1, 1))));
            Assert.Equal(0b10u, selection.MaskFor(new Aabb(new Vector3(3.5f, -1, -1), new Vector3(4.5f, 1, 1))));
            Assert.Equal(0b11u, selection.MaskFor(new Aabb(new Vector3(-10, -1, -1), new Vector3(10, 1, 1))));
            Assert.Equal(0u, selection.MaskFor(new Aabb(new Vector3(-1, -1, -1), new Vector3(1, 1, 1))));
            Assert.Equal(0u, selection.MaskFor(new Aabb(new Vector3(-4, 5, -1), new Vector3(-3, 6, 1))));
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void PackedEntryMapsTheBoxToTheUnitCube()
    {
        Assert.Equal(DecalGpu.Size, Marshal.SizeOf<DecalGpu>());
        Assert.Equal(16 + 32 * 192, DecalSelection.UboSize);

        var decal = new Decal
        {
            TextureAlbedo = Pixel,
            Size = new Vector3(4, 2, 2),
            Position = new Vector3(1, 2, 3),
            RotationDegrees = new Vector3(0, 90, 0),
            Modulate = System.Drawing.Color.FromArgb(128, 255, 0, 0),
            AlbedoMix = 0.5f,
        };
        var (root, decals) = World(decal);
        try
        {
            var selection = new DecalSelection();
            selection.Select(decals, new Frustum(Matrix4x4.CreateOrthographic(100, 100, -100, 100)), Vector3.Zero, null);
            var gpu = selection.Pack(0);
            static Vector3 Local(in DecalGpu g, Vector3 p) => new(
                Vector3.Dot(new Vector3(g.ToLocalX.X, g.ToLocalX.Y, g.ToLocalX.Z), p) + g.ToLocalX.W,
                Vector3.Dot(new Vector3(g.ToLocalY.X, g.ToLocalY.Y, g.ToLocalY.Z), p) + g.ToLocalY.W,
                Vector3.Dot(new Vector3(g.ToLocalZ.X, g.ToLocalZ.Y, g.ToLocalZ.Z), p) + g.ToLocalZ.W);

            AssertNear(Vector3.Zero, Local(gpu, new Vector3(1, 2, 3)));
            // Rotated 90° about Y: the box's +X (4 m wide) points along world -Z.
            AssertNear(new Vector3(0.5f, 0, 0), Local(gpu, new Vector3(1, 2, 1)));
            AssertNear(new Vector3(0, 0.5f, 0), Local(gpu, new Vector3(1, 3, 3)));
            AssertNear(new Vector3(0, 0, -1), new Vector3(gpu.AxisX.X, gpu.AxisX.Y, gpu.AxisX.Z));
            AssertNear(Vector3.UnitY, new Vector3(gpu.AxisY.X, gpu.AxisY.Y, gpu.AxisY.Z));
            Assert.Equal(new Vector4(1f, 0f, 0f, 128f / 255f), gpu.Modulate);
            Assert.Equal(0.5f, gpu.Fade.X);
            Assert.Equal(1f, gpu.Extra.Y); // no distance fade
            Assert.Equal(Vector4.Zero, gpu.AlbedoRect); // no atlas: no tile
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void PackerPlacesTilesWithoutOverlapAndGrowsTheAtlas()
    {
        (int, int)[] sizes = [(64, 64), (128, 32), (16, 200), (64, 64)];
        var layout = DecalAtlasPacker.Pack(sizes);
        Assert.Equal(256, layout.Size);
        Assert.Equal(1, layout.Downscale);
        for (var i = 0; i < sizes.Length; i++)
        {
            var (x, y, w, h) = layout.Tiles[i];
            Assert.Equal(sizes[i], (w, h));
            Assert.True(x >= DecalAtlasPacker.Padding && y >= DecalAtlasPacker.Padding);
            Assert.True(x + w + DecalAtlasPacker.Padding <= layout.Size && y + h + DecalAtlasPacker.Padding <= layout.Size);
            for (var j = 0; j < i; j++)
            {
                var (x2, y2, w2, h2) = layout.Tiles[j];
                var apart = x + w + DecalAtlasPacker.Padding <= x2 - DecalAtlasPacker.Padding || x2 + w2 + DecalAtlasPacker.Padding <= x - DecalAtlasPacker.Padding ||
                            y + h + DecalAtlasPacker.Padding <= y2 - DecalAtlasPacker.Padding || y2 + h2 + DecalAtlasPacker.Padding <= y - DecalAtlasPacker.Padding;
                Assert.True(apart, $"tiles {i} and {j} overlap");
            }
        }

        Assert.Equal(512, DecalAtlasPacker.Pack([(300, 300)]).Size);
        // Too big for the largest atlas: everything is halved until it fits.
        var halved = DecalAtlasPacker.Pack([(600, 600), (600, 600)], maxSize: 1024);
        Assert.Equal(2, halved.Downscale);
        Assert.Equal((300, 300), (halved.Tiles[0].Width, halved.Tiles[0].Height));
    }

    [Fact]
    public void ComposeExtendsEdgesIntoThePaddingAndBoxDownscales()
    {
        byte[] red = [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255];
        var layout = DecalAtlasPacker.Pack([(2, 2)]);
        var atlas = DecalAtlasPacker.Compose(layout, [(red, 2, 2)]);
        var (x, y, _, _) = layout.Tiles[0];
        int At(int px, int py) => atlas[((py * layout.Size) + px) * 4];
        Assert.Equal(255, At(x, y));
        Assert.Equal(255, At(x - DecalAtlasPacker.Padding, y - DecalAtlasPacker.Padding)); // padding takes the edge
        Assert.Equal(0, At(x + 2 + DecalAtlasPacker.Padding, y)); // past the padding: empty

        // A 2 × 1 source at half size averages its two texels.
        byte[] pair = [200, 0, 0, 255, 100, 0, 0, 255];
        var half = new DecalAtlasPacker.Layout(256, [(8, 8, 1, 1)], 2);
        Assert.Equal(150, DecalAtlasPacker.Compose(half, [(pair, 2, 1)])[(8 * 256 + 8) * 4]);
        var rect = DecalAtlasPacker.UvRect(half, 0);
        Assert.Equal(new Vector4(8f / 256, 8f / 256, 1f / 256, 1f / 256), rect);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
}
