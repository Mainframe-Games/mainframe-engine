using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// Slang surface shaders (ADR 0237) without slangc: what mf-shaders reads from the text (the kind, the entry functions'
/// parameters, literal defaults), the layout read from slangc's reflection (fixtures captured from the pinned slangc in
/// Rendering/Fixtures), the generated entries, the layout file and the renderer-side keys. SurfaceShaderBuildTests run
/// slangc; the render tests compile and draw the committed shaders.
/// </summary>
public sealed class SurfaceShaderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Rendering", "Fixtures", name));

    // A fixture's reflection with an edit, as a different shader would give.
    private static string Edit(string fixture, Action<JsonNode> edit)
    {
        var json = JsonNode.Parse(Fixture(fixture))!;
        edit(json);
        return json.ToJsonString();
    }

    private static JsonArray MaterialFields(JsonNode json) => json["parameters"]![0]!["type"]!["elementType"]!["fields"]!.AsArray();

    private static (SurfaceShaderSource Source, SurfaceShaderLayout Layout) Load(string name, ICollection<string>? warnings = null)
    {
        var source = SurfaceShaderSource.Parse(Fixture(name + ".slang"), name + ".slang");
        return (source, SlangReflection.Read(Fixture(name + ".reflection.json"), source, warnings));
    }

    [Theory]
    [InlineData("import mainframe.spatial;\nvoid fragment(inout SurfaceFragment s) {}", SurfaceShaderKind.Spatial)]
    [InlineData("// a comment\n/* another */ import  mainframe . canvas ;", SurfaceShaderKind.Canvas)]
    [InlineData("module noise;\nimport mainframe.spatial;", null)] // a helper module, not a shader
    [InlineData("import noise;\nimport mainframe.spatial;", null)] // the import must come first
    [InlineData("shader_type spatial;", null)]
    public void TheFirstStatementSaysWhetherAndWhatKindOfShaderAFileIs(string source, SurfaceShaderKind? expected)
    {
        Assert.Equal(expected, SurfaceShaderSource.KindOf(source));
        Assert.Equal(expected is not null, SurfaceShaderSource.IsSurfaceShader(source));
    }

    [Fact]
    public void AFileThatIsNotASurfaceShaderIsAnErrorAtLineOne()
    {
        var e = Assert.Throws<FormatException>(() => SurfaceShaderSource.Parse("shader_type spatial;", "old.slang"));
        Assert.StartsWith("old.slang(1): a surface shader starts with `import mainframe.spatial;` or `import mainframe.canvas;`", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryFunctionsTakeTheirOptionalParametersInEitherOrder()
    {
        var s = SurfaceShaderSource.Parse(Fixture("kitchen_sink.slang"), "kitchen_sink.slang");
        Assert.Equal(new SurfaceShaderSource.EntryParameters(Material: true, Varyings: true, VaryingsFirst: false), s.Vertex);
        Assert.Equal(new SurfaceShaderSource.EntryParameters(Material: true, Varyings: true, VaryingsFirst: true), s.Fragment);
        Assert.True(s.HasMaterial);
        Assert.True(s.HasVaryings);

        var minimal = SurfaceShaderSource.Parse("import mainframe.spatial;\nvoid fragment(inout SurfaceFragment s)\n{\n}\n", "m.slang");
        Assert.Null(minimal.Vertex);
        Assert.Equal(new SurfaceShaderSource.EntryParameters(false, false, false), minimal.Fragment);
        Assert.False(minimal.HasMaterial);
        Assert.False(minimal.HasVaryings);

        var twice = "import mainframe.spatial;\nvoid fragment(inout SurfaceFragment s) {}\n\nvoid fragment(inout SurfaceFragment s) {}";
        Assert.StartsWith("t.slang(4): fragment() is declared more than once.", Assert.Throws<FormatException>(() => SurfaceShaderSource.Parse(twice, "t.slang")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsAreTheLiteralInitializersOfMaterial()
    {
        var s = SurfaceShaderSource.Parse(Fixture("kitchen_sink.slang"), "kitchen_sink.slang");
        Assert.Equal([2f], s.Defaults["scale"]);
        Assert.Equal([1f], s.Defaults["flag"]);
        Assert.Equal([-3f], s.Defaults["count"]);
        Assert.Equal([7f], s.Defaults["mask"]);
        Assert.Equal([0.5f, -1f], s.Defaults["offset"]);
        Assert.Equal([1f], s.Defaults["cells"]); // int3(1): broadcast when the layout is read
        Assert.False(s.Defaults.ContainsKey("palette"));
    }

    [Theory]
    [InlineData("float x = 0.5 * 2.0;", "a default must be a literal")]
    [InlineData("float x = PI;", "a default must be a literal")]
    [InlineData("float4 x = float4(sin(1.0));", "a default must be a literal")]
    [InlineData("float x[2] = { 1.0, 2.0 };", "array parameters can't have defaults yet")]
    public void ADefaultThatIsNotALiteralIsAnErrorAtItsLine(string field, string expected)
    {
        var source = $"import mainframe.spatial;\nstruct Material\n{{\n    [color]\n    {field}\n}};\n";
        var e = Assert.Throws<FormatException>(() => SurfaceShaderSource.Parse(source, "d.slang"));
        Assert.StartsWith("d.slang(4): Material.x: " + expected, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", new[] { 1f })]
    [InlineData("-2.5e-1f", new[] { -0.25f })]
    [InlineData("float3(0.4, .8, 3)", new[] { 0.4f, 0.8f, 3f })]
    [InlineData("float2x2(1, 0, 0, 1)", new[] { 1f, 0f, 0f, 1f })]
    [InlineData("false", new[] { 0f })]
    [InlineData("uint2(4u, 5u)", new[] { 4f, 5f })]
    public void LiteralDefaultsFlattenToNumbers(string literal, float[] expected) =>
        Assert.Equal(expected, SurfaceShaderSource.ParseLiteral(literal, "x"));

    [Fact]
    public void ReflectionGivesStd140OffsetsTextureBindingsAndAttributes()
    {
        var (_, layout) = Load("kitchen_sink");
        var u = layout.Uniforms.ToDictionary(x => x.Name);
        Assert.Equal((ShaderUniformType.Vec4, 2, 0, 16, true), (u["palette"].Type, u["palette"].ArrayLength, u["palette"].Offset, u["palette"].Stride, u["palette"].SourceColor));
        Assert.Equal((32, 4), (u["scale"].Offset, u["scale"].Size));
        Assert.Equal([2f], u["scale"].Default!);
        Assert.Equal((ShaderUniformType.Bool, 36), (u["flag"].Type, u["flag"].Offset));
        Assert.Equal((ShaderUniformType.Int, 40), (u["count"].Type, u["count"].Offset));
        Assert.Equal([-3f], u["count"].Default!);
        Assert.Equal((ShaderUniformType.UInt, 44), (u["mask"].Type, u["mask"].Offset));
        Assert.Equal((ShaderUniformType.Vec2, 48), (u["offset"].Type, u["offset"].Offset));
        Assert.Equal((ShaderUniformType.IVec3, 64), (u["cells"].Type, u["cells"].Offset));
        Assert.Equal([1f, 1f, 1f], u["cells"].Default!); // int3(1): broadcast
        Assert.Equal((ShaderUniformType.Mat3, 80, 48), (u["basis"].Type, u["basis"].Offset, u["basis"].Size));
        Assert.Equal((ShaderUniformType.Mat4, 128), (u["transform"].Type, u["transform"].Offset));
        Assert.Equal(192, layout.UniformBlockSize);
        Assert.Equal((1, CanvasTextureFilter.Nearest, CanvasTextureRepeat.Disabled), (u["biome"].Binding, u["biome"].Filter, u["biome"].Repeat));
        Assert.Equal((2, ShaderTextureHint.Normal), (u["detail"].Binding, u["detail"].TextureHint));
        Assert.Equal((3, ShaderTextureHint.Black, CanvasTextureRepeat.Enabled, CanvasTextureFilter.Linear, true),
            (u["dirt"].Binding, u["dirt"].TextureHint, u["dirt"].Repeat, u["dirt"].Filter, u["dirt"].SourceColor));
        Assert.All(layout.Uniforms.Where(x => !x.IsSampler), x => Assert.Equal(-1, x.Binding));
    }

    [Fact]
    public void RenderModesAreTheAttributesOnMaterial()
    {
        var (_, layout) = Load("kitchen_sink");
        var p = layout.ToSpatial("kitchen_sink.slang");
        Assert.Equal(CullMode.Disabled, p.Cull);
        Assert.Equal(SpatialBlendMode.Add, p.BlendMode);
        Assert.Equal(AlphaMode.Blend, p.Alpha); // a blend mode other than Mix blends
        Assert.Equal(SpatialDepthDraw.Always, p.DepthDraw);
        Assert.True(p.RenderState.DepthWrite); // DepthDraw.Always: even blended
        Assert.True(p.RenderState.NoDepthTest);
        Assert.True(p.RenderState.IsTransparent);
        Assert.True(p.Unshaded && p.FogDisabled && p.WorldVertexCoords && p.BlinnPhong);
        Assert.True(p.HasVertexFunction && p.HasFragmentFunction);

        var canvas = Load("wave_canvas").Layout.ToCanvas();
        Assert.Equal((CanvasBlendMode.Mul, true, false), (canvas.BlendMode, canvas.LightOnly, canvas.Unshaded));
        Assert.Equal(1, canvas.SamplerCount);

        var plain = Load("textures_only").Layout.ToSpatial("t.slang");
        Assert.Equal(new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), plain.RenderState);
        Assert.False(plain.HasVertexFunction);
    }

    [Fact]
    public void AnExplicitAlphaModeWinsAndAnUnusedAlphaWriteWarns()
    {
        var json = Fixture("textures_only.reflection.json");
        var cutout = Edit("textures_only.reflection.json",
            j => j["parameters"]![0]!["type"]!["elementType"]!["userAttribs"] = JsonNode.Parse("[{\"name\": \"alpha\", \"arguments\": [1]}]"));
        var source = SurfaceShaderSource.Parse(Fixture("textures_only.slang").Replace("s.albedo =", "s.alpha = 0.5; s.albedo =", StringComparison.Ordinal), "t.slang");
        Assert.Equal(AlphaMode.Cutout, SlangReflection.Read(cutout, source).Modes.Alpha);

        var warnings = new List<string>();
        Assert.Equal(AlphaMode.Opaque, SlangReflection.Read(json, source, warnings).Modes.Alpha);
        Assert.Contains(warnings, w => w.StartsWith("t.slang: fragment() writes alpha, but the material is opaque", StringComparison.Ordinal));
    }

    [Fact]
    public void AMaterialOfTexturesOnlyStillBindsThemFromOne()
    {
        var (source, layout) = Load("textures_only");
        Assert.Equal([1, 2], layout.Uniforms.Select(u => u.Binding));
        Assert.Equal(1, layout.MaterialBinding); // the entries declare it there, so slangc puts the textures at 1 and 2
        Assert.Contains("[[vk::binding(1, 2)]] ConstantBuffer<Material> mf_material;",
            SurfaceShaderCompiler.SpatialFragment(source, layout, "textures_only.slang", SpatialPass.Color), StringComparison.Ordinal);
        Assert.Equal(0, Load("kitchen_sink").Layout.MaterialBinding);
    }

    [Fact]
    public void AGlobalParameterIsAnError()
    {
        var e = Assert.Throws<FormatException>(() => Load("global_parameter"));
        Assert.Equal("global_parameter.slang: 'strength' is a global shader parameter; material parameters go in `struct Material` (ADR 0237).", e.Message);
    }

    [Fact]
    public void MoreTexturesThanTheBudgetAreAnErrorAtTheExtraOne()
    {
        var source = SurfaceShaderSource.Parse(Fixture("kitchen_sink.slang"), "k.slang");
        // The kitchen sink's three textures plus two more: dirt2 is the fifth.
        var json = Edit("kitchen_sink.reflection.json", j =>
        {
            var fields = MaterialFields(j);
            var dirt = fields.Single(f => (string?)f!["name"] == "dirt")!;
            for (var i = 1; i <= 2; i++)
            {
                var copy = dirt.DeepClone();
                copy["name"] = $"dirt{i}";
                copy["binding"]!["index"] = 3 + i;
                fields.Add(copy);
            }
        });
        var e = Assert.Throws<FormatException>(() => SlangReflection.Read(json, source));
        Assert.StartsWith($"k.slang: Material.dirt2: more than {SpatialShaderProgram.MaxSamplers} textures", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scale", "scalarType", "float16", "the scalar type float16 is not supported")]
    [InlineData("offset", "kind", "struct", "struct parameters are not supported")]
    public void AnUnsupportedParameterTypeNamesTheField(string field, string property, string value, string expected)
    {
        var source = SurfaceShaderSource.Parse(Fixture("kitchen_sink.slang"), "k.slang");
        var json = Edit("kitchen_sink.reflection.json", j => MaterialFields(j).Single(f => (string?)f!["name"] == field)!["type"]![property] = value);
        var e = Assert.Throws<FormatException>(() => SlangReflection.Read(json, source));
        Assert.StartsWith($"k.slang: Material.{field}: {expected}", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchedDefaultIsAnError()
    {
        var source = SurfaceShaderSource.Parse(Fixture("kitchen_sink.slang").Replace("float2(0.5, -1.0)", "float2(0.5, -1.0, 2.0)", StringComparison.Ordinal), "k.slang");
        var e = Assert.Throws<FormatException>(() => SlangReflection.Read(Fixture("kitchen_sink.reflection.json"), source));
        Assert.Equal("k.slang: Material.offset: the default has 3 values; its type takes 2 (or 1).", e.Message);
    }

    [Fact]
    public void VaryingsFollowTheTemplatesLocationsWithIntegersFlat()
    {
        var (source, layout) = Load("kitchen_sink");
        Assert.Equal(
            [new("world", "float3", 12, 1, false), new("id", "int", 13, 1, true), new("basis", "float3x3", 14, 3, false), new SurfaceVarying("k", "uint", 17, 1, true)],
            layout.Varyings);
        Assert.Equal([new SurfaceVarying("world", "float2", 2, 1, false)], Load("wave_canvas").Layout.Varyings);

        // The vertex stage writes only what the fragment stage kept.
        var all = SurfaceShaderCompiler.SpatialVertex(source, layout, "kitchen_sink.slang", SpatialPass.Color);
        Assert.Contains("[[vk::location(13)]] nointerpolation int mf_v_id;", all, StringComparison.Ordinal);
        Assert.Contains("o.mf_v_k = mf_vary.k;", all, StringComparison.Ordinal);
        Assert.Contains("o.mf_uv = v.uv;", all, StringComparison.Ordinal);
        var kept = SurfaceShaderCompiler.SpatialVertex(source, layout, "kitchen_sink.slang", SpatialPass.Color, new HashSet<int> { 0, 1, 12, 14 });
        Assert.Contains("o.mf_v_world = mf_vary.world;", kept, StringComparison.Ordinal);
        Assert.Contains("o.mf_v_basis = mf_vary.basis;", kept, StringComparison.Ordinal);
        Assert.Contains("o.mf_world = mf_position;", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("mf_v_id", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("o.mf_uv", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPassImportsTheShaderAndCallsItsFunctionsWithTheirParameters()
    {
        var (source, layout) = Load("kitchen_sink");
        foreach (var pass in SpatialShaderProgram.Passes)
        {
            var vertex = SurfaceShaderCompiler.SpatialVertex(source, layout, "kitchen_sink.slang", pass);
            var fragment = SurfaceShaderCompiler.SpatialFragment(source, layout, "kitchen_sink.slang", pass);
            foreach (var stage in new[] { vertex, fragment })
            {
                Assert.Contains("import mainframe.surface.spatial;\nimport \"kitchen_sink.slang\";\n", stage, StringComparison.Ordinal);
                Assert.DoesNotContain("#line", stage, StringComparison.Ordinal);
            }

            Assert.Contains("    vertex(v, mf_material, mf_vary);\n", vertex, StringComparison.Ordinal);
            Assert.Contains("    fragment(s, input.mf_varyings, mf_material);\n", fragment, StringComparison.Ordinal);
            Assert.Contains("s.alpha < s.alphaScissorThreshold", fragment, StringComparison.Ordinal); // casters and picking cut out too
            Assert.Contains("v.instanceCustom = input.inInstanceCustom;", vertex, StringComparison.Ordinal);
            Assert.Contains("v.vertex = mul(float4(v.vertex, 1.0), model).xyz;", vertex, StringComparison.Ordinal); // worldVertexCoords
        }

        // Colour, depth and ID passes: the material is set 2; the shadow casters' is set 1 (set 0 is the light matrix).
        Assert.Contains("[[vk::binding(0, 2)]] ConstantBuffer<Material> mf_material;", SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.Color), StringComparison.Ordinal);
        Assert.Contains("[[vk::binding(0, 1)]] ConstantBuffer<Material> mf_material;", SurfaceShaderCompiler.SpatialVertex(source, layout, "k", SpatialPass.ShadowPoint), StringComparison.Ordinal);
        // The prepass runs vertex() a frame ago too (last frame's model matrix and time) for the velocity.
        var depth = SurfaceShaderCompiler.SpatialVertex(source, layout, "k", SpatialPass.Depth);
        Assert.Contains("inPrevModel0", depth, StringComparison.Ordinal);
        Assert.Contains("pv.time = frame.temporal.y > 0.5 ? frame.temporal.x : frame.clip.z;", depth, StringComparison.Ordinal);
        Assert.Contains("    vertex(pv, mf_material, mf_previous_vary);\n", depth, StringComparison.Ordinal);
        Assert.Contains("velocityFromClip(", SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.Depth), StringComparison.Ordinal);
        // Casters: the light matrix (set 0) and the pushed time and camera; the point pass writes the light distance.
        var shadow = SurfaceShaderCompiler.SpatialVertex(source, layout, "k", SpatialPass.Shadow);
        Assert.Contains("mf_lvp.lightViewProj", shadow, StringComparison.Ordinal);
        Assert.Contains("v.time = mf_caster.time.x;", shadow, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bframe\.", shadow);
        Assert.Contains("s.time = input.mf_time;", SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.Shadow), StringComparison.Ordinal);
        Assert.Contains("mf_caster.lightPosRange", SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.ShadowPoint), StringComparison.Ordinal);
        Assert.Contains("return input.mf_object_id;", SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.ObjectId), StringComparison.Ordinal);
        // The colour pass: unshaded and noFog leave out the lighting and the fog, Cull.Disabled flips back faces' normal.
        var color = SurfaceShaderCompiler.SpatialFragment(source, layout, "k", SpatialPass.Color);
        Assert.DoesNotContain("shadeLights", color, StringComparison.Ordinal);
        Assert.DoesNotContain("applyFog(", color, StringComparison.Ordinal);
        Assert.Contains("mf_geometry_normal = -mf_geometry_normal", color, StringComparison.Ordinal);
        Assert.Contains("s.invViewMatrix = spatial_inv_view(frame.invViewRotation, frame.cameraPosition.xyz);", color, StringComparison.Ordinal);
    }

    [Fact]
    public void TheColourPassLightsWithTheChosenModelAndOnlyBendsNormalsForANormalMap()
    {
        var source = SurfaceShaderSource.Parse(Fixture("textures_only.slang"), "t.slang");
        var layout = Load("textures_only").Layout;
        var pbr = SurfaceShaderCompiler.SpatialFragment(source, layout, "t.slang", SpatialPass.Color);
        Assert.Contains("shadeLightsPbr(", pbr, StringComparison.Ordinal);
        Assert.Contains("applyFog(", pbr, StringComparison.Ordinal);
        Assert.DoesNotContain("spatial_normal_map(", pbr, StringComparison.Ordinal);
        Assert.Contains("float3 mf_light_position = input.mf_world;", pbr, StringComparison.Ordinal);

        var mapped = SurfaceShaderSource.Parse(Fixture("textures_only.slang").Replace("s.albedo =", "s.normalMap = float3(0.5); s.lightVertex = s.vertex; s.albedo =", StringComparison.Ordinal), "t.slang");
        var blinn = layout with { Modes = layout.Modes with { BlinnPhong = true } };
        var fragment = SurfaceShaderCompiler.SpatialFragment(mapped, blinn, "t.slang", SpatialPass.Color);
        Assert.Contains("shadeLightsBlinnPhong(", fragment, StringComparison.Ordinal);
        Assert.Contains("spatial_normal_map(mf_n, s.normalMap", fragment, StringComparison.Ordinal);
        Assert.Contains("mul(float4(s.lightVertex, 1.0), s.invViewMatrix)", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasStagesImportTheShaderAndKeepTheCanvasPipeline()
    {
        var (source, layout) = Load("wave_canvas");
        var vertex = SurfaceShaderCompiler.CanvasVertex(source, layout, "wave_canvas.slang");
        var fragment = SurfaceShaderCompiler.CanvasFragment(source, layout, "wave_canvas.slang");
        Assert.Contains("import mainframe.surface.canvas;\nimport \"wave_canvas.slang\";\n", vertex, StringComparison.Ordinal);
        Assert.Contains("    vertex(v, mf_vary);\n", vertex, StringComparison.Ordinal);
        Assert.Contains("[[vk::location(2)]] float2 mf_v_world;", vertex, StringComparison.Ordinal);
        Assert.Contains("o.position = mul(float4(mf_pixel, 0.0, 1.0), v.screenMatrix);", vertex, StringComparison.Ordinal);
        Assert.Contains("[[vk::binding(0, 1)]] ConstantBuffer<Material> mf_material;", fragment, StringComparison.Ordinal);
        Assert.Contains("    fragment(s, mf_material, input.mf_varyings);\n", fragment, StringComparison.Ordinal);
        Assert.Contains("canvas_apply_lights(s.color, mf_base, s.vertex);", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("o.colorInterp", SurfaceShaderCompiler.CanvasVertex(source, layout, "w", new HashSet<int> { 0, 2 }), StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeDeclaresOnlyTheMaterialAndTheVaryings()
    {
        var (source, _) = Load("kitchen_sink");
        var probe = SurfaceShaderCompiler.Probe(source, "kitchen_sink.slang");
        Assert.Contains("[[vk::binding(0, 2)]] ConstantBuffer<Material> mf_material;", probe, StringComparison.Ordinal);
        Assert.Contains("float4 main([[vk::location(12)]] Varyings mf_varyings) : SV_Target0", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("#include", probe, StringComparison.Ordinal);
        var bare = SurfaceShaderCompiler.Probe(SurfaceShaderSource.Parse("import mainframe.canvas;", "c.slang"), "c.slang");
        Assert.Contains("float4 main() : SV_Target0", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("mf_material", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLayoutFileRoundTrips()
    {
        foreach (var name in new[] { "kitchen_sink", "wave_canvas", "textures_only" })
        {
            var layout = Load(name).Layout;
            var text = layout.Write();
            var read = SurfaceShaderLayout.Read(text, name);
            Assert.Equal(text, read.Write());
            Assert.Equal(layout.Uniforms.Select(u => (u.Name, u.Type, u.Offset, u.Binding, u.Default?.Length)), read.Uniforms.Select(u => (u.Name, u.Type, u.Offset, u.Binding, u.Default?.Length)));
            Assert.Equal(layout.Modes, read.Modes);
            Assert.Equal(layout.Varyings, read.Varyings);
        }

        Assert.StartsWith("x.slang: the layout file is not readable", Assert.Throws<FormatException>(() => SurfaceShaderLayout.Read("{}", "x.slang")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockHashFollowsTheShaderAndItsLayout()
    {
        var text = Fixture("kitchen_sink.slang");
        var (source, layout) = Load("kitchen_sink");
        var a = SurfaceShaderBuild.Hash(text, source, layout);
        Assert.Equal(a, SurfaceShaderBuild.Hash(text.ReplaceLineEndings("\r\n"), source, layout)); // a Windows checkout
        Assert.NotEqual(a, SurfaceShaderBuild.Hash(text.Replace("0.5", "0.6", StringComparison.Ordinal), source, layout));
        Assert.NotEqual(a, SurfaceShaderBuild.Hash(text, source, layout with { Modes = layout.Modes with { Cull = CullMode.Back } }));
    }

    [Fact]
    public void DepfilesListThePrerequisites() =>
        Assert.Equal(["/a/x.slang", "/b c/y.slang", "/z.slang"], SurfaceShaderBuild.ReadDepfile("out.spv: /a/x.slang /b\\ c/y.slang \\\n /z.slang\n"));

    [Fact]
    public void SpatialUniformBlocksLinearizeColoursAndCanvasOnesDoNot()
    {
        ShaderUniform[] uniforms =
        [
            new("tint", ShaderUniformType.Vec4, 0, 0, 16, 16, [0.5f, 0.5f, 0.5f, 0.5f], true, -1, default, default),
            new("plain", ShaderUniformType.Vec3, 0, 16, 12, 12, [0.5f, 0.5f, 0.5f], false, -1, default, default),
        ];
        var spatial = Shader.FromProgram(new SpatialShaderProgram("s", uniforms, 32, false, true, AlphaMode.Opaque, CullMode.Back, SpatialBlendMode.Mix,
            SpatialDepthDraw.Opaque, false, false, false, false, false));
        var canvas = Shader.FromProgram(new CanvasShaderProgram(uniforms, 32, false, true, CanvasBlendMode.Mix, false, false));
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
        var layout = Load("textures_only").Layout;
        var shader = Shader.FromProgram((layout with { Modes = layout.Modes with { Alpha = AlphaMode.Blend, Cull = CullMode.Disabled } }).ToSpatial("t.slang"));
        var material = new ShaderMaterial { Shader = shader };
        Assert.Equal(AlphaMode.Blend, material.RenderState.Alpha);
        Assert.Equal(CullMode.Disabled, material.RenderState.EffectiveCull);
        Assert.Equal(default, new ShaderMaterial().RenderState);
    }

    [Fact]
    public void SpatialPassesHaveTheirOwnSpirvFiles()
    {
        var shader = Shader.FromProgram(Load("textures_only").Layout.ToSpatial("ground.slang"), "/x/ground.slang");
        Assert.Equal("/x/ground.slang.vert.spv", shader.SpatialSpvPath(SpatialPass.Color, vertex: true));
        Assert.Equal("/x/ground.slang.depth.frag.spv", shader.SpatialSpvPath(SpatialPass.Depth, vertex: false));
        Assert.Equal("/x/ground.slang.shadow-point.vert.spv", shader.SpatialSpvPath(SpatialPass.ShadowPoint, vertex: true));
        Assert.Equal(10, SurfaceShaderBuild.SpvFiles("/x/ground.slang", SurfaceShaderKind.Spatial).Distinct().Count());
        Assert.Equal(["/x/w.slang.vert.spv", "/x/w.slang.frag.spv"], SurfaceShaderBuild.SpvFiles("/x/w.slang", SurfaceShaderKind.Canvas));
        Assert.True(SpirvInvariance.Applies("/x/ground.slang.vert.spv")); // the colour and prepass positions meet exactly
        Assert.True(SpirvInvariance.Applies("/x/ground.slang.depth.vert.spv"));
        Assert.False(SpirvInvariance.Applies("/x/ground.slang.shadow.vert.spv"));
        Assert.False(SpirvInvariance.Applies("/x/Mesh.vk.vert.spv"));
    }

    [Theory]
    [InlineData("ground.slang.vert.spv", true)]
    [InlineData("ground.slang.shadow-point.frag.spv", true)]
    [InlineData("ground.slang.spvlock", true)]
    [InlineData("ground.slang.layout.json", true)]
    [InlineData("ground.slang", false)]
    [InlineData("noise.slang", false)]
    [InlineData("layout.json", false)]
    public void ShaderSidecarsAreBuildOutputsNotAssets(string fileName, bool expected) =>
        Assert.Equal(expected, AssetDatabase.IsBuildOutput(fileName));

    [Theory]
    [InlineData("old.mfshader")]
    [InlineData("older.gdshader")]
    public void GodotSyntaxShadersNoLongerLoad(string fileName)
    {
        var dir = Directory.CreateTempSubdirectory("mf-godot-shader-");
        try
        {
            var path = Path.Combine(dir.FullName, fileName);
            File.WriteAllText(path, "shader_type spatial;\nvoid fragment() { ALBEDO = vec3(1.0); }\n");
            var e = Assert.Throws<FormatException>(() => Shader.Load(path));
            Assert.Contains("Godot-syntax shaders were removed (ADR 0237); port it to a Slang surface shader", e.Message, StringComparison.Ordinal);
            Assert.Contains(fileName.EndsWith(".mfshader", StringComparison.Ordinal) ? ".mfshader" : ".gdshader", new ShaderImporter().Extensions);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AShaderWithoutALayoutLoadsEmptyAndDrawsTheDefaultMaterial()
    {
        var dir = Directory.CreateTempSubdirectory("mf-unbuilt-shader-");
        try
        {
            var path = Path.Combine(dir.FullName, "unbuilt.slang");
            File.WriteAllText(path, Fixture("textures_only.slang"));
            var shader = Shader.Load(path);
            Assert.NotNull(shader.Spatial);
            Assert.Empty(shader.Uniforms);
            Assert.Null(shader.SpvBasePath); // no SPIR-V: the mesh renderer warns and draws the default material
            Assert.Throws<FormatException>(() => Shader.Load(Path.Combine(AppContext.BaseDirectory, "Rendering", "Fixtures", "kitchen_sink.reflection.json")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
