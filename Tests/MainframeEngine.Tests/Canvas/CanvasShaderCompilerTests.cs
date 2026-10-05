using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Canvas;

/// <summary>Godot canvas_item shaders → GLSL (ADR 0113): uniform layout, defaults, hints, render modes, value packing.</summary>
public sealed class CanvasShaderCompilerTests
{
    private const string Fog = """
        shader_type canvas_item;
        // comments are dropped
        uniform sampler2D fog : filter_linear, repeat_disable;
        uniform float dim : hint_range(0.0, 1.0) = 0.45;
        uniform vec3 seen[4];
        uniform int seen_count = 0;
        uniform vec2 world_min = vec2(0.0);
        uniform vec2 world_size = vec2(1.0);
        uniform vec4 colour : source_color = vec4(0.0, 0.9, 1.0, 0.55);
        void fragment() { COLOR = texture(fog, UV) * dim; }
        """;

    [Fact]
    public void UniformsGetStd140OffsetsInDeclarationOrderAndSamplersBindings()
    {
        var p = CanvasShaderCompiler.Translate(Fog);
        var u = p.Uniforms.ToDictionary(x => x.Name);
        Assert.Equal(0, u["dim"].Offset);
        Assert.Equal(16, u["seen"].Offset);       // arrays align to 16
        Assert.Equal(16, u["seen"].Stride);       // vec3 elements stride 16
        Assert.Equal(80, u["seen_count"].Offset);
        Assert.Equal(88, u["world_min"].Offset);  // vec2 aligns to 8
        Assert.Equal(96, u["world_size"].Offset);
        Assert.Equal(112, u["colour"].Offset);    // vec4 aligns to 16
        Assert.Equal(128, p.UniformBlockSize);
        Assert.True(u["fog"].IsSampler);
        Assert.Equal(1, u["fog"].Binding);
        Assert.Equal(CanvasTextureFilter.Linear, u["fog"].Filter);
        Assert.Equal(CanvasTextureRepeat.Disabled, u["fog"].Repeat);
        Assert.True(u["colour"].SourceColor);
        Assert.Equal([1f, 1f], u["world_size"].Default!);
        Assert.Equal([0f, 0.9f, 1f, 0.55f], u["colour"].Default!);
        Assert.False(p.HasVertexFunction);
        Assert.Contains("layout(set = 1, binding = 1) uniform sampler2D fog;", p.FragmentGlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("comments are dropped", p.FragmentGlsl, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderModesVaryingsAndStagesAreTranslated()
    {
        var p = CanvasShaderCompiler.Translate("""
            shader_type canvas_item;
            render_mode blend_add, unshaded;
            varying vec2 v_world;
            float twice(float x) { return x * 2.0; }
            void vertex() { v_world = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy; }
            void fragment() { COLOR.a = twice(v_world.x); }
            """);
        Assert.Equal(CanvasBlendMode.Add, p.BlendMode);
        Assert.True(p.Unshaded);
        Assert.True(p.HasVertexFunction);
        Assert.Contains("layout(location = 2) out vec2 v_world;", p.VertexGlsl, StringComparison.Ordinal);
        Assert.Contains("layout(location = 2) in vec2 v_world;", p.FragmentGlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("void fragment()", p.VertexGlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("void vertex()", p.FragmentGlsl, StringComparison.Ordinal);
        Assert.Contains("float twice(float x)", p.VertexGlsl, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedShadersAreRejected()
    {
        Assert.Throws<FormatException>(() => CanvasShaderCompiler.Translate("shader_type spatial;"));
        Assert.Throws<FormatException>(() => CanvasShaderCompiler.Translate("shader_type canvas_item; void light() { }"));
        Assert.Throws<FormatException>(() => CanvasShaderCompiler.Translate("shader_type canvas_item; uniform samplerCube c;"));
    }

    [Fact]
    public void MaterialValuesPackIntoTheBlockOverDefaults()
    {
        var program = CanvasShaderCompiler.Translate(Fog);
        var shader = Shader.FromProgram(program);
        var block = new byte[program.UniformBlockSize];
        var parameters = new Dictionary<string, object>
        {
            ["seen"] = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) },
            ["seen_count"] = 2,
        };
        shader.WriteUniformBlock(parameters, block);
        var floats = MemoryMarshal.Cast<byte, float>(block);
        var ints = MemoryMarshal.Cast<byte, int>(block);
        Assert.Equal(0.45f, floats[0]);                    // dim default
        Assert.Equal([1f, 2f, 3f], floats[4..7].ToArray());   // seen[0]
        Assert.Equal([4f, 5f, 6f], floats[8..11].ToArray());  // seen[1] at stride 16
        Assert.Equal(2, ints[20]);                         // seen_count is an int
        Assert.Equal(1f, floats[24]);                      // world_size default
        Assert.Equal(0.55f, floats[31]);                   // colour.a default
    }
}
