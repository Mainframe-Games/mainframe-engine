using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering.Meshes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0151: the second vertex stream (<see cref="MeshSurface.Colors"/>/<see cref="MeshSurface.Custom0"/>), its
/// pipeline layout, and <see cref="FoliageMaterial3D"/>'s state and parameters (no GPU).
/// </summary>
public sealed class VertexStreamAndFoliageTests
{
    private static readonly RenderPass ScenePass = new(0x1234);

    private static MeshSurface Triangle() =>
        new([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2]);

    [Fact]
    public void TheStreamIsTwentyBytesAndTheMeshVertexStaysThirtyTwo()
    {
        Assert.Equal(MeshVertexExt.Size, Unsafe.SizeOf<MeshVertexExt>());
        Assert.Equal(20, MeshVertexExt.Size);
        Assert.Equal(32, Unsafe.SizeOf<MeshVertex>());
        Assert.Equal((uint)MeshVertexExt.Size, VertexLayouts.MeshInstancedExtBindings[2].Stride);
        Assert.Equal(2u, VertexLayouts.MeshInstancedExtBindings[2].Binding);
        Assert.Equal(Format.R8G8B8A8Unorm, VertexLayouts.ColorAttribute.Format);
        Assert.Equal(4u, VertexLayouts.Custom0Attribute.Offset);
    }

    [Fact]
    public void ColoursPackAsRgba8WithRedInTheLowestByte()
    {
        Assert.Equal(0xFFFFFFFFu, MeshVertexExt.PackColor(Vector4.One));
        Assert.Equal(0xFF0000FFu, MeshVertexExt.PackColor(new Vector4(1, 0, 0, 1)));
        Assert.Equal(0x80000000u | 0x40u << 8, MeshVertexExt.PackColor(new Vector4(0, 64 / 255f, 0, 128 / 255f)));
        Assert.Equal(0xFF0000FFu, MeshVertexExt.PackColor(new Vector4(2, -1, float.NaN, 5))); // clamped
    }

    [Fact]
    public void MissingStreamsWriteWhiteAndZero()
    {
        var surface = Triangle();
        Assert.False(surface.HasVertexStreams);

        surface.Custom0 = [new Vector4(1, 2, 3, 4), Vector4.Zero, new Vector4(0.5f)];
        Assert.True(surface.HasVertexStreams);
        var streams = new MeshVertexExt[3];
        surface.WriteVertexStreams(streams);
        Assert.All(streams, s => Assert.Equal(MeshVertexExt.White, s.Color));
        Assert.Equal(new Vector4(1, 2, 3, 4), streams[0].Custom0);

        surface.Custom0 = [];
        surface.Colors = [new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1), new Vector4(0, 0, 1, 0)];
        surface.WriteVertexStreams(streams);
        Assert.Equal(0xFF0000FFu, streams[0].Color);
        Assert.Equal(0x00FF0000u, streams[2].Color);
        Assert.All(streams, s => Assert.Equal(Vector4.Zero, s.Custom0));
    }

    [Fact]
    public void StreamsMustMatchThePositions()
    {
        var surface = Triangle();
        var version = surface.Version;
        surface.Colors = [Vector4.One];
        Assert.True(surface.Version > version);
        Assert.Throws<InvalidDataException>(surface.Validate);
        surface.Colors = [];
        surface.Custom0 = [Vector4.One, Vector4.One];
        Assert.Throws<InvalidDataException>(surface.Validate);
        surface.Custom0 = [Vector4.One, Vector4.One, Vector4.One];
        surface.Validate();
    }

    [Fact]
    public void StreamSurfacesAndFoliageUseTheExtLayoutAndIdsNever()
    {
        var standard = new StandardMaterial3D().RenderState;
        Assert.Equal(VertexLayoutId.MeshInstanced, PipelineKey.ForMaterial(ShaderSetId.MeshLit, standard, false, ScenePass).VertexLayout);
        var lit = PipelineKey.ForMaterial(ShaderSetId.MeshLit, standard, false, ScenePass, streams: true);
        Assert.Equal(VertexLayoutId.MeshInstancedExt, lit.VertexLayout);
        Assert.NotEqual(PipelineKey.ForMaterial(ShaderSetId.MeshLit, standard, false, ScenePass), lit);
        Assert.Equal(VertexLayoutId.MeshInstanced,
            PipelineKey.ForMaterial(ShaderSetId.MeshObjectId, standard, false, ScenePass, streams: true).VertexLayout);
        Assert.Equal(VertexLayoutId.MeshInstanced,
            PipelineKey.ForMaterial(ShaderSetId.MeshOutline, standard, false, ScenePass, streams: true).VertexLayout);

        var foliage = new FoliageMaterial3D().RenderState;
        Assert.Equal(VertexLayoutId.MeshInstancedExt, PipelineKey.ForMaterial(ShaderSetId.MeshFoliage, foliage, false, ScenePass).VertexLayout);
    }

    [Fact]
    public void FoliageCutsOutAndIsDoubleSidedUnlessCulled()
    {
        var material = new FoliageMaterial3D();
        Assert.Equal(new MaterialRenderState(AlphaMode.Cutout, CullMode.Back, true), material.RenderState);
        Assert.Equal(CullMode.Disabled, material.RenderState.EffectiveCull);
        Assert.True(material.RenderState.CastsShadows);

        material.BackFace = FoliageBackFace.Cull;
        material.AlphaCutout = false;
        Assert.Equal(new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), material.RenderState);
        material.BackFace = FoliageBackFace.Keep;
        Assert.Equal(CullMode.Disabled, material.RenderState.EffectiveCull);
    }

    [Fact]
    public void EveryFoliagePropertyBumpsTheVersion()
    {
        var material = new FoliageMaterial3D();
        var texture = Texture2D.FromPixels(1, 1, [255, 255, 255, 255]);
        Action[] changes =
        [
            () => material.AlbedoColor = Color.Green,
            () => material.AlbedoTexture = texture,
            () => material.NormalTexture = texture,
            () => material.NormalScale = 2f,
            () => material.AlphaCutout = false,
            () => material.AlphaCutoff = 0.3f,
            () => material.BackFace = FoliageBackFace.Keep,
            () => material.Translucency = 0.9f,
            () => material.ShadingMode = ShadingMode.Unshaded,
            () => material.Roughness = 0.2f,
            () => material.WindStrength = 2f,
            () => material.WindBranchBend = 0f,
        ];
        foreach (var change in changes)
        {
            var version = material.Version;
            change();
            Assert.Equal(version + 1, material.Version);
            change(); // same value: no change
            Assert.Equal(version + 1, material.Version);
        }
    }

    [Fact]
    public void FoliageParamsPackIntoTheStandardBlock()
    {
        var material = new FoliageMaterial3D
        {
            AlbedoColor = Color.FromArgb(255, 255, 128, 0),
            Translucency = 0.7f,
            WindStrength = 1.5f,
            WindBranchBend = 0.25f,
            AlphaCutoff = 0.4f,
            NormalScale = 2f,
            BackFace = FoliageBackFace.Keep,
        };
        var p = MaterialParams.From(material, MaterialParams.HasAlbedo);
        Assert.Equal(ColorSpace.SrgbToLinear(128 / 255f), p.Albedo.Y, 5);
        Assert.Equal(new Vector4(0.7f, 1.5f, 0.25f, 0f), p.Emission);
        Assert.Equal(0.4f, p.Params.Z);
        Assert.Equal(2f, p.Params.W);
        Assert.Equal(MaterialParams.HasAlbedo, p.TextureFlags);
        Assert.Equal(2u, p.DoubleSided); // back face mode + 1
        Assert.Equal(MaterialParams.ShadingBlinnPhong, p.Shading);
        material.ShadingMode = ShadingMode.Pbr;
        Assert.Equal(MaterialParams.ShadingPbr, MaterialParams.From(material, 0).Shading);
        material.ShadingMode = ShadingMode.BlinnPhong;

        material.AlphaCutout = false;
        Assert.Equal(0f, MaterialParams.From(material, 0).Params.Z); // the casters' alpha test never discards

        // Rougher is a weaker, wider highlight.
        var (smoothSpecular, smoothShininess) = FoliageMaterial3D.BlinnFromRoughness(0.2f);
        var (roughSpecular, roughShininess) = FoliageMaterial3D.BlinnFromRoughness(0.9f);
        Assert.True(smoothSpecular > roughSpecular && smoothShininess > roughShininess);
        Assert.InRange(FoliageMaterial3D.BlinnFromRoughness(0f).Shininess, 1f, 1024f);
    }
}
