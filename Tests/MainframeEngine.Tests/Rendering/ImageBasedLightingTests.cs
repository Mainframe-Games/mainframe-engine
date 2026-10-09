using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0150: the split-sum BRDF table, the sky capture's face cameras and the sky-lighting flags (no GPU).</summary>
public sealed class ImageBasedLightingTests
{
    [Fact]
    public void BrdfLutMatchesANumericalIntegralOfTheShadersLobe()
    {
        // Reference: ∫ D·Vis·N·L (F0 = 1 → A + B) and ∫ D·Vis·N·L·(1 − V·H)^5 (→ B) over the hemisphere by quadrature,
        // with lights.slang's GGX and Smith height-correlated visibility.
        foreach (var (nDotV, roughness) in new[] { (0.5f, 0.5f), (0.9f, 0.5f), (0.25f, 1f), (0.9f, 1f), (0.7f, 0.3f) })
        {
            var (sum, bias) = Reference(nDotV, roughness);
            var lut = BrdfLut.Integrate(nDotV, roughness, 4096);
            Assert.InRange(lut.X + lut.Y, sum - 0.01f, sum + 0.01f);
            Assert.InRange(lut.Y, bias - 0.01f, bias + 0.01f);
        }
    }

    [Fact]
    public void BrdfLutSpotChecks()
    {
        // A smooth surface seen head-on reflects F0: scale 1, bias 0.
        var mirror = BrdfLut.Integrate(1f, 0f, 1024);
        Assert.InRange(mirror.X, 0.97f, 1.01f);
        Assert.InRange(mirror.Y, 0f, 0.01f);

        // Grazing angles push energy into the bias (Fresnel), rough surfaces lose energy (single scattering).
        Assert.True(BrdfLut.Sample(0.05f, 0.2f).Y > BrdfLut.Sample(0.95f, 0.2f).Y);
        Assert.True(BrdfLut.Sample(0.9f, 1f).X < BrdfLut.Sample(0.9f, 0.2f).X);

        // Every entry is a valid reflectance.
        foreach (var ab in BrdfLut.Table)
        {
            Assert.InRange(ab.X, 0f, 1.01f);
            Assert.InRange(ab.Y, 0f, 1f);
            Assert.True(ab.X + ab.Y <= 1.01f, $"A + B = {ab.X + ab.Y}");
        }

        Assert.Equal(BrdfLut.Size * BrdfLut.Size, BrdfLut.Table.Length);
        Assert.Equal(BrdfLut.Size * BrdfLut.Size * 4, BrdfLut.ToHalfPixels().Length); // R16G16_SFLOAT
    }

    [Fact]
    public void BrdfLutTexelCentresHoldTheirValues()
    {
        // uv = (N·V, roughness) hits texel (i, j) at its centre: the GPU's bilinear lookup reads the entry itself.
        const int i = 37, j = 12;
        var centre = BrdfLut.Sample((i + 0.5f) / BrdfLut.Size, (j + 0.5f) / BrdfLut.Size);
        Assert.Equal(BrdfLut.Table[j * BrdfLut.Size + i], centre);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CaptureCamerasLookAlongTheirCubeFace(int face)
    {
        // sky.slang's skyRay for pixel (u, v) of the face (flipped viewport: NDC y up) must be the direction Vulkan's
        // cube addressing maps to (face, u, v).
        var frame = FrameData.From(SkyRadiance.FaceView(face), SkyRadiance.FaceProjection, Vector3.Zero, new Extent2D(128, 128), 0f, 1f);
        foreach (var (u, v) in new[] { (0.5f, 0.5f), (0.1f, 0.2f), (0.9f, 0.3f), (0.25f, 0.85f) })
        {
            var ndc = new Vector4(u * 2f - 1f, 1f - v * 2f, 1f, 1f);
            var view = Vector4.Transform(ndc, frame.InverseProjection);
            var dir = Vector3.Normalize(Vector3.TransformNormal(new Vector3(view.X, view.Y, view.Z) / view.W, frame.InverseViewRotation));
            var (gotFace, gotU, gotV) = CubeAddress(dir);
            Assert.Equal(face, gotFace);
            Assert.Equal(u, gotU, 3);
            Assert.Equal(v, gotV, 3);
        }
    }

    [Fact]
    public void FragmentStageStaysWithinTheBindingBudget()
    {
        // MoltenVK / Android baseline: ≤ 16 samplers and ≤ 16 sampled images per stage (rendering-features.md).
        const int materialImages = 4, materialSamplers = 1;
        var images = ShadowSystem.SamplerCount + materialImages + FrameContext.EnvironmentBindings;
        var samplers = ShadowSystem.SamplerCount + materialSamplers + FrameContext.EnvironmentBindings;
        Assert.Equal(13, images);
        Assert.Equal(10, samplers);
        Assert.True(images <= 16 && samplers <= 16);
        Assert.Equal(6u, SkyRadiance.RadianceMips); // environment.slang: kRadianceMaxMip = 5

        // TerrainSplatMaterial3D's own set 2 (ADR 0156): three layer arrays + two weight maps, two samplers; 3 sets.
        var terrainImages = ShadowSystem.SamplerCount + MeshRenderer.SplatSetImages + FrameContext.EnvironmentBindings;
        var terrainSamplers = ShadowSystem.SamplerCount + MeshRenderer.SplatSetSamplers + FrameContext.EnvironmentBindings;
        Assert.Equal(14, terrainImages);
        Assert.Equal(11, terrainSamplers);
        Assert.True(terrainImages <= 16 && terrainSamplers <= 16);
    }

    [Fact]
    public void LightsUboCarriesAmbientEnergyAndEnvironmentFlags()
    {
        var lights = new LightEnvironment
        {
            AmbientColor = new Vector3(1f, 1f, 1f),
            AmbientEnergy = 0.5f,
            EnvironmentFlags = LightEnvironment.EnvironmentSkyDiffuse | LightEnvironment.EnvironmentSkySpecular,
        };
        var bytes = new byte[LightEnvironment.UboSize];
        lights.WriteUbo(bytes, new Vector3(1, 2, 3));
        var f = MemoryMarshal.Cast<byte, float>(bytes);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), new Vector4(f[0], f[1], f[2], f[3])); // colour × energy, energy
        Assert.Equal(new Vector4(1, 2, 3, 3), new Vector4(f[4], f[5], f[6], f[7]));             // flags as a float
    }

    [Fact]
    public void DefaultLightsUboKeepsTheOldHeader()
    {
        var bytes = new byte[LightEnvironment.UboSize];
        new LightEnvironment().WriteUbo(bytes, Vector3.Zero);
        var f = MemoryMarshal.Cast<byte, float>(bytes);
        Assert.Equal(ColorSpace.SrgbToLinear(LightEnvironment.DefaultAmbientColor), new Vector3(f[0], f[1], f[2]));
        Assert.Equal(1f, f[3]);
        Assert.Equal(0f, f[7]);
    }

    [Fact]
    public void EnvironmentFlagsFollowTheSourcesAndTheBake()
    {
        var maps = new EnvironmentMaps(new ImageView(1), new ImageView(2));
        var environment = new WorldEnvironment();
        Assert.Equal(AmbientSource.Color, environment.AmbientSource); // defaults keep the ambient colour
        Assert.Equal(ReflectedLightSource.Background, environment.ReflectedLightSource);
        Assert.Equal(1f, environment.AmbientEnergy);

        Assert.Equal(0, environment.EnvironmentFlags(null)); // not baked: ambient colour, uniform reflections
        Assert.Equal(LightEnvironment.EnvironmentSkySpecular, environment.EnvironmentFlags(maps));

        environment.AmbientSource = AmbientSource.Sky;
        Assert.Equal(LightEnvironment.EnvironmentSkySpecular | LightEnvironment.EnvironmentSkyDiffuse, environment.EnvironmentFlags(maps));
        Assert.Equal(0, environment.EnvironmentFlags(null));

        environment.ReflectedLightSource = ReflectedLightSource.Disabled;
        Assert.Equal(LightEnvironment.EnvironmentNoSpecular | LightEnvironment.EnvironmentSkyDiffuse, environment.EnvironmentFlags(maps));
        Assert.Equal(LightEnvironment.EnvironmentNoSpecular, environment.EnvironmentFlags(null));

        Assert.Null(environment.SkyLightingMaps); // nothing captured without a renderer
        Assert.NotEqual(maps.Id, new EnvironmentMaps(default, default).Id);
        Assert.NotEqual(0, maps.Id); // 0 is the fallback's id in FrameContext
        environment.Free();
    }

    // Vulkan spec, "Cube Map Face Selection": major axis → face, then (sc, tc) / |ma| → (u, v).
    private static (int Face, float U, float V) CubeAddress(Vector3 r)
    {
        var a = Vector3.Abs(r);
        int face;
        float sc, tc, ma;
        if (a.X >= a.Y && a.X >= a.Z)
        {
            face = r.X > 0 ? 0 : 1;
            sc = r.X > 0 ? -r.Z : r.Z;
            tc = -r.Y;
            ma = a.X;
        }
        else if (a.Y >= a.Z)
        {
            face = r.Y > 0 ? 2 : 3;
            sc = r.X;
            tc = r.Y > 0 ? r.Z : -r.Z;
            ma = a.Y;
        }
        else
        {
            face = r.Z > 0 ? 4 : 5;
            sc = r.Z > 0 ? r.X : -r.X;
            tc = -r.Y;
            ma = a.Z;
        }

        return (face, (sc / ma + 1f) * 0.5f, (tc / ma + 1f) * 0.5f);
    }

    // (A + B, B) by midpoint quadrature over the light direction's hemisphere (N = +Z).
    private static (float Sum, float Bias) Reference(float nDotV, float roughness)
    {
        var alpha = Math.Max(roughness, 0.045f);
        alpha *= alpha;
        var a2 = alpha * alpha;
        var v = new Vector3(MathF.Sqrt(1f - nDotV * nDotV), 0f, nDotV);
        const int thetaSteps = 512, phiSteps = 1024;
        double sum = 0, bias = 0;
        var dTheta = MathF.PI / 2f / thetaSteps;
        var dPhi = 2f * MathF.PI / phiSteps;
        for (var t = 0; t < thetaSteps; t++)
        {
            var theta = (t + 0.5f) * dTheta;
            var (sinT, cosT) = MathF.SinCos(theta);
            for (var p = 0; p < phiSteps; p++)
            {
                var phi = (p + 0.5f) * dPhi;
                var l = new Vector3(MathF.Cos(phi) * sinT, MathF.Sin(phi) * sinT, cosT);
                var h = Vector3.Normalize(l + v);
                var nDotH = h.Z;
                var d = nDotH * nDotH * (a2 - 1f) + 1f;
                var distribution = a2 / (MathF.PI * d * d);
                var f = distribution * BrdfLut.Visibility(nDotV, cosT, a2) * cosT * sinT * dTheta * dPhi;
                sum += f;
                bias += f * MathF.Pow(1f - Math.Clamp(Vector3.Dot(v, h), 0f, 1f), 5f);
            }
        }

        return ((float)sum, (float)bias);
    }
}
