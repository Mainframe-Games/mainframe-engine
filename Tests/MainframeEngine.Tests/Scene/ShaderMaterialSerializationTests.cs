using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// ADR 0205: a spatial <see cref="ShaderMaterial"/> in a <c>.mres</c> (its shader as a reference, its parameters — numbers,
/// vectors, arrays, a texture — as <see cref="ShaderMaterial.ShaderParameters"/>), and <see cref="MultiMesh"/> custom data.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class ShaderMaterialSerializationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-shadermaterial-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public ShaderMaterialSerializationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly float[] Weights = [1f, 2f, 3f];

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    [Fact]
    public void SpatialShaderMaterialsRoundTripThroughMres()
    {
        Directory.CreateDirectory(ContentPath("Shaders"));
        File.WriteAllText(ContentPath("Shaders/dissolve.gdshader"), """
            shader_type spatial;
            uniform float progress = 0.5;
            uniform vec4 tint : source_color;
            uniform float weights[3];
            uniform vec2 offsets[2];
            uniform bool glow;
            uniform sampler2D noise;
            void fragment() { ALPHA = progress; ALPHA_SCISSOR_THRESHOLD = 0.5; }
            """);
        MeshSerializationTests.WritePng(ContentPath("Textures/noise.png"), 2, 2);
        AssetDatabase.Current.Scan();

        var shader = ResourceLoader.Load<Shader>("Assets/Shaders/dissolve.gdshader"); // no SPIR-V here: a warning only
        Assert.NotNull(shader.Spatial);
        var noise = ResourceLoader.Load<Texture2D>("Assets/Textures/noise.png");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("progress", 0.25f);
        material.SetShaderParameter("tint", new Vector4(1f, 0.5f, 0.25f, 1f));
        material.SetShaderParameter("weights", (float[])Weights.Clone());
        material.SetShaderParameter("offsets", new[] { new Vector2(1, 2), new Vector2(3, 4) });
        material.SetShaderParameter("glow", true);
        material.SetShaderParameter("noise", noise);
        var uid = ResourceSaver.Save(material, ContentPath("Materials/dissolve.mres"));
        var json = File.ReadAllText(ContentPath("Materials/dissolve.mres"));
        Assert.Contains("\"ShaderParameters\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"Assets/Shaders/dissolve.gdshader\"", json, StringComparison.Ordinal);

        ResourceLoader.ClearCache();
        var reloaded = ResourceLoader.Load<ShaderMaterial>(uid);
        Assert.NotNull(reloaded.Shader?.Spatial);
        Assert.Equal(AlphaMode.Cutout, reloaded.RenderState.Alpha);
        Assert.Equal(0.25f, reloaded.GetShaderParameter("progress"));
        Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), reloaded.GetShaderParameter("tint"));
        Assert.Equal(Weights, reloaded.GetShaderParameter("weights"));
        Assert.Equal(new[] { new Vector2(1, 2), new Vector2(3, 4) }, reloaded.GetShaderParameter("offsets"));
        Assert.Equal(true, reloaded.GetShaderParameter("glow"));
        var texture = Assert.IsType<Texture2D>(reloaded.GetShaderParameter("noise"));
        Assert.Equal("Assets/Textures/noise.png", texture.ResourcePath);

        // The values reach the uniform block as the shader lays it out.
        var block = new byte[reloaded.Shader!.UniformBlockSize];
        reloaded.Shader.WriteUniformBlock(reloaded.Parameters, block);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block);
        var weights = reloaded.Shader.Uniforms.Single(u => u.Name == "weights");
        Assert.Equal(2f, floats[(weights.Offset + weights.Stride) / 4]);
    }

    [Fact]
    public void ShaderParametersReplaceEveryValueAndListThemByName()
    {
        var material = new ShaderMaterial();
        material.SetShaderParameter("b", 2f);
        material.SetShaderParameter("a", 1f);
        Assert.Equal(["a", "b"], material.ShaderParameters.Select(p => p.Name));
        var version = material.Version;
        material.ShaderParameters = [new ShaderParameter("c", new Vector3(1, 2, 3))];
        Assert.Null(material.GetShaderParameter("a"));
        Assert.Equal(new Vector3(1, 2, 3), material.GetShaderParameter("c"));
        Assert.NotEqual(version, material.Version);
    }

    [Fact]
    public void MultiMeshCustomDataIsKeptPerInstanceAndSaved()
    {
        var multimesh = new MultiMesh { InstanceCount = 3 };
        Assert.Throws<InvalidOperationException>(() => multimesh.SetInstanceCustomData(0, Vector4.One));
        Assert.Equal(Vector4.Zero, multimesh.GetInstanceCustomData(1));
        multimesh.UseCustomData = true;
        var version = multimesh.Version;
        multimesh.SetInstanceCustomData(2, new Vector4(1, 2, 3, 4));
        Assert.NotEqual(version, multimesh.Version);
        Assert.Equal(new Vector4(1, 2, 3, 4), multimesh.GetInstanceCustomData(2));
        Assert.Equal(3, multimesh.CustomData.Length);

        var uid = ResourceSaver.Save(multimesh, ContentPath("stalks.mres"));
        ResourceLoader.ClearCache();
        var reloaded = ResourceLoader.Load<MultiMesh>(uid);
        Assert.True(reloaded.UseCustomData);
        Assert.Equal(new Vector4(1, 2, 3, 4), reloaded.GetInstanceCustomData(2));

        reloaded.InstanceCount = 5; // resizing clears, like the transforms
        Assert.Equal(5, reloaded.CustomData.Length);
        Assert.Equal(Vector4.Zero, reloaded.GetInstanceCustomData(2));
        reloaded.UseCustomData = false;
        Assert.Empty(reloaded.CustomData);
    }
}
