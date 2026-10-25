using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// Godot spatial shaders → Slang (ADR 0205): parsing, the built-in mapping per pass, render modes and the alpha mode Godot
/// infers, uniform layout and hints, error messages with the file and line, and the renderer-side keys and layouts (no GPU,
/// no slangc: the render tests compile and draw them).
/// </summary>
public sealed class SpatialShaderCompilerTests
{
    private const string Ground = """
        shader_type spatial;
        // the ground: world-space albedo
        uniform vec4 palette[2] : source_color;
        uniform float scale = 2.0;
        uniform sampler2D biome : filter_nearest, repeat_disable;
        uniform sampler2D detail_normal : hint_normal;
        uniform sampler2D dirt : source_color, hint_default_black, repeat_enable;
        const float K = 0.5;
        float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
        void fragment() {
            vec2 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
            ALBEDO = mix(palette[0].rgb, palette[1].rgb, hash(floor(world * scale))) * K;
        }
        """;

    [Fact]
    public void UniformsGetStd140OffsetsAndSamplersBindingsWithHints()
    {
        var p = SpatialShaderCompiler.Translate(Ground, "ground.gdshader");
        var u = p.Uniforms.ToDictionary(x => x.Name);
        Assert.Equal(0, u["palette"].Offset);
        Assert.Equal(16, u["palette"].Stride);
        Assert.Equal(32, u["scale"].Offset);
        Assert.Equal(48, p.UniformBlockSize);
        Assert.Equal(3, p.SamplerCount);
        Assert.Equal((1, CanvasTextureFilter.Nearest, CanvasTextureRepeat.Disabled), (u["biome"].Binding, u["biome"].Filter, u["biome"].Repeat));
        Assert.Equal((2, ShaderTextureHint.Normal), (u["detail_normal"].Binding, u["detail_normal"].TextureHint));
        Assert.Equal((3, ShaderTextureHint.Black, CanvasTextureRepeat.Enabled), (u["dirt"].Binding, u["dirt"].TextureHint, u["dirt"].Repeat));
        Assert.True(u["dirt"].SourceColor);

        // Colour, depth and ID passes: the material is set 2; the shadow casters' is set 1 (set 0 is the light matrix).
        var color = p.FragmentSource(SpatialPass.Color);
        Assert.Contains("[[vk::binding(0, 2)]] cbuffer MaterialUniforms", color, StringComparison.Ordinal);
        Assert.Contains("    vec4 palette[2];", color, StringComparison.Ordinal);
        Assert.Contains("[[vk::binding(1, 2)]] Sampler2D biome;", color, StringComparison.Ordinal);
        Assert.Contains("[[vk::binding(3, 1)]] Sampler2D dirt;", p.FragmentSource(SpatialPass.Shadow), StringComparison.Ordinal);
        Assert.Contains("[[vk::binding(0, 1)]] cbuffer MaterialUniforms", p.VertexSource(SpatialPass.ShadowPoint), StringComparison.Ordinal);
        Assert.Contains("static const float K = 0.5;", color, StringComparison.Ordinal); // GLSL's global const
        Assert.DoesNotContain("the ground: world-space albedo", color, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsAreOpaqueBackCulledLitAndTheStateFollowsRenderModes()
    {
        var p = SpatialShaderCompiler.Translate(Ground);
        Assert.Equal(AlphaMode.Opaque, p.Alpha);
        Assert.Equal(CullMode.Back, p.Cull);
        Assert.Equal(new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), p.RenderState);
        Assert.True(p.RenderState.DepthWrite);
        Assert.False(p.HasVertexFunction);
        Assert.True(p.HasFragmentFunction);
        Assert.Contains("shadeLightsPbr(", p.FragmentSource(SpatialPass.Color), StringComparison.Ordinal);
        Assert.Contains("applyFog(", p.FragmentSource(SpatialPass.Color), StringComparison.Ordinal);

        var modes = SpatialShaderCompiler.Translate("""
            shader_type spatial;
            render_mode blend_add, cull_disabled, depth_draw_always, depth_test_disabled, unshaded, fog_disabled, world_vertex_coords;
            void fragment() { ALBEDO = vec3(1.0); }
            """);
        Assert.Equal(AlphaMode.Blend, modes.Alpha); // blend_add blends whatever ALPHA does
        Assert.Equal(SpatialBlendMode.Add, modes.BlendMode);
        Assert.Equal(CullMode.Disabled, modes.Cull);
        Assert.Equal(SpatialDepthDraw.Always, modes.DepthDraw);
        Assert.True(modes.RenderState.DepthWrite); // depth_draw_always: even blended
        Assert.True(modes.RenderState.NoDepthTest);
        Assert.True(modes.RenderState.IsTransparent);
        var fragment = modes.FragmentSource(SpatialPass.Color);
        Assert.DoesNotContain("shadeLights", fragment, StringComparison.Ordinal); // unshaded
        Assert.DoesNotContain("applyFog(gd_color", fragment, StringComparison.Ordinal);
        Assert.Contains("gd_geometry_normal = -gd_geometry_normal", fragment, StringComparison.Ordinal); // cull_disabled side check
        Assert.Contains("VERTEX = mul(float4(VERTEX, 1.0), model).xyz;", modes.VertexSource(SpatialPass.Color), StringComparison.Ordinal);

        var never = SpatialShaderCompiler.Translate("shader_type spatial;\nrender_mode depth_draw_never, specular_blinn, cull_front;");
        Assert.False(never.RenderState.DepthWrite);
        Assert.Equal(CullMode.Front, never.Cull);
        Assert.True(never.BlinnPhong);
        Assert.Contains("shadeLightsBlinnPhong(", never.FragmentSource(SpatialPass.Color), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ALBEDO = vec3(1.0);", AlphaMode.Opaque)]
    [InlineData("ALPHA = 0.5;", AlphaMode.Blend)]
    [InlineData("ALPHA *= 0.5;", AlphaMode.Blend)]
    [InlineData("if (ALPHA == 1.0) { ALBEDO = vec3(0.0); }", AlphaMode.Opaque)] // a comparison is not a write
    [InlineData("ALPHA = 0.3; ALPHA_SCISSOR_THRESHOLD = 0.5;", AlphaMode.Cutout)]
    public void TheAlphaModeFollowsGodotsRule(string fragment, AlphaMode expected)
    {
        var p = SpatialShaderCompiler.Translate($"shader_type spatial;\nvoid fragment() {{ {fragment} }}");
        Assert.Equal(expected, p.Alpha);
        Assert.Equal(expected == AlphaMode.Blend, p.RenderState.IsTransparent);
        Assert.Equal(expected != AlphaMode.Blend, p.RenderState.CastsShadows);
    }

    [Fact]
    public void EveryPassRunsTheUserFunctionsWithItsBuiltInSources()
    {
        var p = SpatialShaderCompiler.Translate("""
            shader_type spatial;
            uniform float sway = 0.4;
            varying vec3 v_world;
            varying flat int v_id;
            varying mat3 v_basis;
            void vertex() {
                v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                VERTEX.x += sin(TIME + INSTANCE_CUSTOM.x) * sway * (1.0 - UV.y);
                v_id = INSTANCE_ID;
                v_basis = mat3(MODEL_MATRIX);
            }
            void fragment() { ALBEDO = v_world * float(v_id) + v_basis[0]; ALPHA = 0.5; ALPHA_SCISSOR_THRESHOLD = 0.25; }
            """, "sway.gdshader");
        Assert.Equal(AlphaMode.Cutout, p.Alpha);
        foreach (var pass in SpatialShaderProgram.Passes)
        {
            var vertex = p.VertexSource(pass);
            var fragment = p.FragmentSource(pass);
            Assert.Contains("    vertex();\n", vertex, StringComparison.Ordinal);
            Assert.Contains("    fragment();\n", fragment, StringComparison.Ordinal);
            Assert.Contains("ALPHA < ALPHA_SCISSOR_THRESHOLD", fragment, StringComparison.Ordinal); // casters and picking cut out too
            Assert.Contains("INSTANCE_CUSTOM = input.inInstanceCustom;", vertex, StringComparison.Ordinal);
            // Varyings after the template's locations; a mat3 takes three.
            Assert.Contains("[[vk::location(12)]] vec3 v_world;", vertex, StringComparison.Ordinal);
            Assert.Contains("[[vk::location(13)]] nointerpolation int v_id;", vertex, StringComparison.Ordinal);
            Assert.Contains("[[vk::location(14)]] mat3 v_basis;", vertex, StringComparison.Ordinal);
            // The user code keeps its file and lines.
            Assert.Contains("#line 6 \"sway.gdshader\"", vertex, StringComparison.Ordinal);
            Assert.Contains("#line 12 \"sway.gdshader\"", fragment, StringComparison.Ordinal);
        }

        // The prepass runs vertex() a frame ago too (last frame's model matrix and time) for the velocity.
        var depth = p.VertexSource(SpatialPass.Depth);
        Assert.Contains("inPrevModel0", depth, StringComparison.Ordinal);
        Assert.Contains("frame.temporal.x", depth, StringComparison.Ordinal);
        Assert.Contains("velocityFromClip(", p.FragmentSource(SpatialPass.Depth), StringComparison.Ordinal);
        // Casters: the light matrix (set 0) and the pushed time and camera; the point pass writes the light distance.
        Assert.Contains("gd_lvp.lightViewProj", p.VertexSource(SpatialPass.Shadow), StringComparison.Ordinal);
        Assert.Contains("TIME = gd_caster.time.x;", p.VertexSource(SpatialPass.Shadow), StringComparison.Ordinal);
        Assert.Contains("TIME = input.gd_time;", p.FragmentSource(SpatialPass.Shadow), StringComparison.Ordinal);
        Assert.Contains("gd_caster.lightPosRange", p.FragmentSource(SpatialPass.ShadowPoint), StringComparison.Ordinal);
        Assert.DoesNotContain("frame.", p.VertexSource(SpatialPass.Shadow).Replace("frame.slang", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("return input.gd_object_id;", p.FragmentSource(SpatialPass.ObjectId), StringComparison.Ordinal);
        // The colour pass reads the frame: VIEW_MATRIX, INV_VIEW_MATRIX and TIME.
        Assert.Contains("INV_VIEW_MATRIX = spatial_inv_view(frame.invViewRotation, frame.cameraPosition.xyz);", p.FragmentSource(SpatialPass.Color), StringComparison.Ordinal);
        Assert.Contains("TIME = frame.clip.z;", p.VertexSource(SpatialPass.Color), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVertexStageWritesOnlyTheInputsTheFragmentStageKept()
    {
        var p = SpatialShaderCompiler.Translate("""
            shader_type spatial;
            varying float v_a;
            varying float v_b;
            void vertex() { v_a = 1.0; v_b = 2.0; }
            void fragment() { ALBEDO = vec3(v_a); }
            """);
        var all = p.VertexSource(SpatialPass.Color);
        Assert.Contains("o.v_b = v_b;", all, StringComparison.Ordinal);
        Assert.Contains("o.gd_uv = UV;", all, StringComparison.Ordinal);
        var kept = p.VertexSource(SpatialPass.Color, new HashSet<int> { 0, 1, 12 });
        Assert.Contains("o.v_a = v_a;", kept, StringComparison.Ordinal);
        Assert.Contains("o.gd_world = gd_position;", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("o.v_b", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("o.gd_uv", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("[[vk::location(13)]] float v_b", kept, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shader_type spatial;\nrender_mode blend_mul;", "s.gdshader(2): render_mode 'blend_mul' is not supported.")]
    [InlineData("shader_type spatial;\n\nvoid light() { DIFFUSE_LIGHT = vec3(1.0); }", "s.gdshader(3): light() is not supported yet")]
    [InlineData("shader_type spatial;\ninstance uniform vec4 tint;", "s.gdshader(2): instance uniforms are not supported yet.")]
    [InlineData("shader_type spatial;\n#define X 1\nvoid fragment() {}", "s.gdshader(2): preprocessor directives are not supported")]
    [InlineData("shader_type spatial;\nuniform float4 x;", "s.gdshader(2): uniform type 'float4' is not supported.")]
    [InlineData("shader_type spatial;\nuniform sampler2D t[2];", "s.gdshader(2): sampler2D arrays are not supported")]
    [InlineData("shader_type canvas_item;", "s.gdshader: only shader_type spatial is supported here (found 'canvas_item').")]
    public void ErrorsNameTheFileAndLine(string source, string expected)
    {
        var e = Assert.Throws<FormatException>(() => SpatialShaderCompiler.Translate(source, "s.gdshader"));
        Assert.StartsWith(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreSamplersThanTheBudgetAreAnErrorAtTheExtraOne()
    {
        var source = "shader_type spatial;\n" + string.Concat(Enumerable.Range(0, SpatialShaderCompiler.MaxSamplers + 1).Select(i => $"uniform sampler2D t{i};\n"));
        var e = Assert.Throws<FormatException>(() => SpatialShaderCompiler.Translate(source, "many.gdshader"));
        Assert.StartsWith($"many.gdshader({SpatialShaderCompiler.MaxSamplers + 2}): more than {SpatialShaderCompiler.MaxSamplers} sampler2D", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderTypeIsDetectedThroughComments()
    {
        Assert.True(SpatialShaderCompiler.IsSpatial("// shader_type canvas_item;\nshader_type spatial;"));
        Assert.False(SpatialShaderCompiler.IsSpatial("shader_type canvas_item;"));
        Assert.Equal("particles", SpatialShaderCompiler.ShaderTypeOf("/* x */ shader_type particles;"));
    }

    [Fact]
    public void SpatialUniformBlocksLinearizeSourceColorsAndCanvasOnesDoNot()
    {
        const string body = "uniform vec4 tint : source_color = vec4(0.5, 0.5, 0.5, 0.5);\nuniform vec3 plain = vec3(0.5);\n";
        var spatial = Shader.FromProgram(SpatialShaderCompiler.Translate("shader_type spatial;\n" + body));
        var canvas = Shader.FromProgram(CanvasShaderCompiler.Translate("shader_type canvas_item;\n" + body));
        var block = new byte[spatial.UniformBlockSize];
        spatial.WriteUniformBlock(new Dictionary<string, object>(), block);
        var f = MemoryMarshal.Cast<byte, float>(block);
        Assert.Equal(ColorSpace.SrgbToLinear(0.5f), f[0], 5);
        Assert.Equal(0.5f, f[3]); // alpha stays
        Assert.Equal(0.5f, f[4]); // not a colour
        canvas.WriteUniformBlock(new Dictionary<string, object> { ["tint"] = new Vector4(0.25f) }, block);
        Assert.Equal(0.25f, f[0]);
    }

    [Fact]
    public void ShaderMaterialTakesTheSpatialShadersRenderState()
    {
        var shader = Shader.FromProgram(SpatialShaderCompiler.Translate("shader_type spatial;\nrender_mode cull_disabled;\nvoid fragment() { ALPHA = 0.5; }"));
        var material = new ShaderMaterial { Shader = shader };
        Assert.Equal(AlphaMode.Blend, material.RenderState.Alpha);
        Assert.Equal(CullMode.Disabled, material.RenderState.EffectiveCull);
        Assert.Equal(default, new ShaderMaterial().RenderState);
    }

    [Fact]
    public void SpatialPassesHaveTheirOwnSpirvFiles()
    {
        var shader = Shader.FromProgram(SpatialShaderCompiler.Translate("shader_type spatial;"), "/x/ground.gdshader");
        Assert.Equal("/x/ground.gdshader.vert.spv", shader.SpatialSpvPath(SpatialPass.Color, vertex: true));
        Assert.Equal("/x/ground.gdshader.depth.frag.spv", shader.SpatialSpvPath(SpatialPass.Depth, vertex: false));
        Assert.Equal("/x/ground.gdshader.shadow-point.vert.spv", shader.SpatialSpvPath(SpatialPass.ShadowPoint, vertex: true));
        Assert.Equal(10, CanvasShaderBuild.SpatialSpvFiles("/x/ground.gdshader").Distinct().Count());
        Assert.True(SpirvInvariance.Applies("/x/ground.gdshader.vert.spv")); // the colour and prepass positions meet exactly
        Assert.True(SpirvInvariance.Applies("/x/ground.gdshader.depth.vert.spv"));
        Assert.False(SpirvInvariance.Applies("/x/ground.gdshader.shadow.vert.spv"));
    }

    [Fact]
    public void TheLockHashFollowsTheTranslation()
    {
        var a = CanvasShaderBuild.Hash(SpatialShaderCompiler.Translate(Ground));
        Assert.Equal(a, CanvasShaderBuild.Hash(SpatialShaderCompiler.Translate(Ground)));
        Assert.NotEqual(a, CanvasShaderBuild.Hash(SpatialShaderCompiler.Translate(Ground.Replace("K = 0.5", "K = 0.6", StringComparison.Ordinal))));
    }

    [Fact]
    public void UserShaderPipelinesKeyByTheirShader()
    {
        var state = new MaterialRenderState(AlphaMode.Cutout, CullMode.Back, false);
        var a = PipelineKey.ForMaterial(ShaderSetId.MeshSpatial, state, false, default, userShader: 1);
        var b = PipelineKey.ForMaterial(ShaderSetId.MeshSpatial, state, false, default, userShader: 2);
        Assert.NotEqual(a, b);
        Assert.Equal(VertexLayoutId.MeshInstancedExt, a.VertexLayout); // spatial shaders always read the second stream
        Assert.True(PipelineKey.ForMaterial(ShaderSetId.MeshDepthSpatial, state, false, default, userShader: 1).IsDepthPrepass);
        var blend = new MaterialRenderState(AlphaMode.Blend, CullMode.Back, false);
        var id = PipelineKey.ForMaterial(ShaderSetId.MeshObjectIdSpatial, blend, false, default, userShader: 1);
        Assert.Equal((AlphaMode.Opaque, true), (id.Alpha, id.DepthWrite)); // picking never blends
    }

    [Fact]
    public void InstanceDataCarriesInstanceCustomAndIdInItsPadding()
    {
        Assert.Equal(MeshInstanceData.Size, Marshal.SizeOf<MeshInstanceData>());
        Assert.Equal(68, (int)Marshal.OffsetOf<MeshInstanceData>(nameof(MeshInstanceData.CustomX)));
        Assert.Equal(76, (int)Marshal.OffsetOf<MeshInstanceData>(nameof(MeshInstanceData.InstanceIndex)));
        var data = new MeshInstanceData(Matrix4x4.Identity, 7, new Vector4(0.5f, 1f, -2f, 3f), 42);
        Assert.Equal(((Half)0.5f, (Half)(-2f), 42u), (data.CustomX, data.CustomZ, data.InstanceIndex));
        var custom = VertexLayouts.SpatialAttributes.Single(a => a.Location == 16);
        Assert.Equal((1u, Format.R16G16B16A16Sfloat, 68u), (custom.Binding, custom.Format, custom.Offset));
        Assert.Equal([0, 3, 17], VertexLayouts.Select(VertexLayouts.SpatialAttributes, new HashSet<int> { 0, 3, 17 }).Select(a => (int)a.Location));
    }
}
