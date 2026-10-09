using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Water;

public sealed class WaterMaterial3DTests
{
    [Fact]
    public void ItIsABlendedMaterialThatCastsNoShadow()
    {
        var material = new WaterMaterial3D();
        var state = material.RenderState;
        Assert.Equal(AlphaMode.Blend, state.Alpha);
        Assert.False(state.CastsShadows);
        Assert.False(state.DepthWrite);
        Assert.Equal(CullMode.Back, state.EffectiveCull);
        Assert.Equal(new Vector3(0.45f, 0.09f, 0.06f), material.Absorption);
        Assert.Equal(0.06f, material.Roughness);
    }

    [Fact]
    public void EverySetterTouchesTheMaterial()
    {
        var material = new WaterMaterial3D();
        var version = material.Version;
        material.Roughness = 0.2f;
        material.FlowCycle = 2f;
        material.Absorption = Vector3.One;
        material.NormalMap = WaterTextures.Normal;
        Assert.Equal(version + 4, material.Version);
        material.Roughness = 0.2f; // unchanged: no touch
        Assert.Equal(version + 4, material.Version);
    }

    [Fact]
    public void ParametersPackIntoTheMaterialBlock()
    {
        var material = new WaterMaterial3D
        {
            NormalScaleNear = 4f,
            NormalScaleFar = 10f,
            NormalStrength = 0.5f,
            Roughness = 0.1f,
            FlowCycle = 2f,
            FlowScale = 1.5f,
            ShoreFoamDistance = 0.2f,
            FoamStrength = 0.7f,
            FoamScale = 5f,
            SoftEdgeDistance = 0.3f,
            WindDrift = 0.25f,
            ReflectionStrength = 0.8f,
            ScatterStrength = 0.4f,
            Absorption = new Vector3(1, 2, 3),
            ScatterColor = System.Drawing.Color.White,
        };
        var p = MaterialParams.From(material, MaterialParams.HasNormal);

        Assert.Equal(MaterialParams.Size, Marshal.SizeOf<MaterialParams>());
        Assert.Equal(new Vector4(1, 1, 1, 0.4f), p.Albedo);
        Assert.Equal(new Vector4(1, 2, 3, 0.8f), p.Emission);
        Assert.Equal(new Vector4(0.25f, 0.1f, 0.5f, 0.1f), p.UvTransform);
        Assert.Equal(new Vector4(2f, 1.5f, 0.2f, 0.7f), p.Params);
        Assert.Equal(new Vector4(0.2f, 0.3f, 0.25f, 0f), p.Pbr);
        Assert.Equal(MaterialParams.HasNormal, p.TextureFlags);
        Assert.Equal(MaterialParams.ShadingPbr, p.Shading);
    }

    [Fact]
    public void TheBuiltInTexturesAreDeterministicAndTile()
    {
        Assert.Equal(WaterTextures.GenerateNormalPixels(), WaterTextures.GenerateNormalPixels());
        Assert.Equal(WaterTextures.GenerateFoamPixels(), WaterTextures.GenerateFoamPixels());
        Assert.Same(WaterTextures.Normal, WaterTextures.Normal);
        Assert.Equal(TextureWrap.Repeat, WaterTextures.Normal.ImportSettings.Wrap);
        Assert.Equal(TextureWrap.Repeat, WaterTextures.Foam.ImportSettings.Wrap);

        foreach (var pixels in new[] { WaterTextures.GenerateNormalPixels(), WaterTextures.GenerateFoamPixels() })
        {
            // The step across the wrap is no larger than the largest step inside the tile, in both directions.
            int inside = 0, across = 0;
            const int n = WaterTextures.Size;
            for (var a = 0; a < n; a++)
                for (var c = 0; c < 3; c++)
                {
                    for (var b = 0; b < n - 1; b++)
                    {
                        inside = Math.Max(inside, Math.Abs(pixels[(a * n + b) * 4 + c] - pixels[(a * n + b + 1) * 4 + c]));
                        inside = Math.Max(inside, Math.Abs(pixels[(b * n + a) * 4 + c] - pixels[((b + 1) * n + a) * 4 + c]));
                    }

                    across = Math.Max(across, Math.Abs(pixels[(a * n + n - 1) * 4 + c] - pixels[(a * n) * 4 + c]));
                    across = Math.Max(across, Math.Abs(pixels[((n - 1) * n + a) * 4 + c] - pixels[a * 4 + c]));
                }

            Assert.InRange(across, 0, inside);
        }

        // The normals point up: z is the largest component everywhere.
        var normal = WaterTextures.GenerateNormalPixels();
        for (var i = 0; i < normal.Length; i += 4)
            Assert.True(normal[i + 2] >= 160, $"texel {i / 4} has z {normal[i + 2]}");
    }
}
