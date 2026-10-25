using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>
/// The passes a spatial shader is compiled for (ADR 0205): one vertex + fragment SPIR-V pair each. Every pass runs the
/// shader's <c>vertex()</c> and <c>fragment()</c>, so displacement, <c>discard</c> and the alpha scissor agree everywhere.
/// </summary>
public enum SpatialPass : byte
{
    /// <summary>The scene pass: the shader's surface lit by the engine (or unshaded), fogged.</summary>
    Color,

    /// <summary>The depth prepass (ADR 0163): depth and the velocity, <c>vertex()</c> run for this frame and the last.</summary>
    Depth,

    /// <summary>Directional and spot shadow maps (set 0 = the light matrix, set 1 = the material).</summary>
    Shadow,

    /// <summary>Point-light shadow cubes: the linear distance to the light.</summary>
    ShadowPoint,

    /// <summary>The object-ID picking pass: the instance's object id.</summary>
    ObjectId,
}

/// <summary>A spatial shader's <c>render_mode blend_*</c>.</summary>
public enum SpatialBlendMode : byte
{
    Mix,
    Add,

    /// <summary><c>blend_premul_alpha</c>: the shader's colour is already multiplied by its alpha.</summary>
    PremultAlpha,
}

/// <summary>A spatial shader's <c>render_mode depth_draw_*</c>.</summary>
public enum SpatialDepthDraw : byte
{
    /// <summary><c>depth_draw_opaque</c> (the default): opaque and cut-out surfaces write depth, blended ones do not.</summary>
    Opaque,

    /// <summary><c>depth_draw_always</c>: blended surfaces write depth too.</summary>
    Always,

    /// <summary><c>depth_draw_never</c>: nothing writes depth (the surface is not in the depth prepass).</summary>
    Never,
}

/// <summary>
/// A translated <c>shader_type spatial</c> shader (ADR 0205): the material interface (uniforms, render modes, the alpha
/// mode Godot infers from <c>ALPHA</c> and <c>ALPHA_SCISSOR_THRESHOLD</c>) and the Slang of every <see cref="SpatialPass"/>.
/// </summary>
public sealed class SpatialShaderProgram
{
    private readonly SpatialShaderCompiler.Parsed _parsed;

    internal SpatialShaderProgram(SpatialShaderCompiler.Parsed parsed)
    {
        _parsed = parsed;
    }

    /// <summary>The file name errors and <c>#line</c> directives name.</summary>
    public string Name => _parsed.Name;

    /// <summary>The uniforms: non-samplers in one std140 block (binding 0), samplers at bindings 1…<see cref="SamplerCount"/>.</summary>
    public IReadOnlyList<ShaderUniform> Uniforms => _parsed.Uniforms;

    public int UniformBlockSize => _parsed.BlockSize;

    public int SamplerCount => _parsed.SamplerCount;

    public bool HasVertexFunction => _parsed.Vertex is not null;

    public bool HasFragmentFunction => _parsed.Fragment is not null;

    /// <summary>
    /// Godot's rule: <see cref="AlphaMode.Cutout"/> when the fragment code uses <c>ALPHA_SCISSOR_THRESHOLD</c>,
    /// <see cref="AlphaMode.Blend"/> when it writes <c>ALPHA</c> (or the blend mode is not <c>blend_mix</c>), else opaque.
    /// </summary>
    public AlphaMode Alpha => _parsed.Alpha;

    public CullMode Cull => _parsed.Cull;

    public SpatialBlendMode BlendMode => _parsed.Blend;

    public SpatialDepthDraw DepthDraw => _parsed.DepthDraw;

    /// <summary><c>depth_test_disabled</c>.</summary>
    public bool DepthTestDisabled => _parsed.DepthTestDisabled;

    /// <summary><c>unshaded</c>: ALBEDO + EMISSION, no lights.</summary>
    public bool Unshaded => _parsed.Unshaded;

    /// <summary><c>specular_blinn</c> / <c>specular_phong</c> (Godot 3's names): the engine's Blinn-Phong instead of PBR.</summary>
    public bool BlinnPhong => _parsed.BlinnPhong;

    /// <summary><c>world_vertex_coords</c>: <c>vertex()</c> sees VERTEX and NORMAL in world space.</summary>
    public bool WorldVertexCoords => _parsed.WorldVertexCoords;

    /// <summary><c>fog_disabled</c>.</summary>
    public bool FogDisabled => _parsed.FogDisabled;

    /// <summary>The pipeline state the shader's render modes ask for (part of the pipeline key).</summary>
    public MaterialRenderState RenderState =>
        new(Alpha, Cull, DoubleSided: false, NoDepthTest: DepthTestDisabled, DepthDraw: DepthDraw switch
        {
            SpatialDepthDraw.Always => DepthDrawMode.Always,
            SpatialDepthDraw.Never => DepthDrawMode.Never,
            _ => DepthDrawMode.Default,
        });

    /// <summary>
    /// The vertex stage of <paramref name="pass"/>. <paramref name="fragmentInputs"/>: the input locations the compiled
    /// fragment stage kept (<see cref="SpirvInputs"/>), or null for all; the vertex stage then writes only those (Slang
    /// drops unread fragment inputs, and an unread vertex output is a validation warning).
    /// </summary>
    public string VertexSource(SpatialPass pass, IReadOnlySet<int>? fragmentInputs = null) =>
        SpatialShaderCompiler.WriteVertex(_parsed, pass, fragmentInputs);

    /// <summary>The fragment stage of <paramref name="pass"/>.</summary>
    public string FragmentSource(SpatialPass pass) => SpatialShaderCompiler.WriteFragment(_parsed, pass);

    /// <summary>
    /// What a pass's SPIR-V files add to the <c>.gdshader</c> path: <c>x.gdshader.vert.spv</c> for the colour pass,
    /// <c>x.gdshader.depth.vert.spv</c>, <c>.shadow</c>, <c>.shadow-point</c> and <c>.id</c> for the others.
    /// </summary>
    public static string PassSuffix(SpatialPass pass) => pass switch
    {
        SpatialPass.Color => "",
        SpatialPass.Depth => ".depth",
        SpatialPass.Shadow => ".shadow",
        SpatialPass.ShadowPoint => ".shadow-point",
        _ => ".id",
    };

    /// <summary>Every pass, in build order.</summary>
    public static readonly SpatialPass[] Passes = [SpatialPass.Color, SpatialPass.Depth, SpatialPass.Shadow, SpatialPass.ShadowPoint, SpatialPass.ObjectId];
}

/// <summary>
/// Translates Godot 4.7 <c>shader_type spatial</c> shaders to Slang for the mesh renderer (ADR 0205), the 3D counterpart of
/// <see cref="CanvasShaderCompiler"/>: the shader keeps Godot's language (uniforms with hints and defaults, varyings,
/// <c>render_mode</c>, constants, structs and helper functions, <c>vertex()</c> and <c>fragment()</c>) and is wrapped in a
/// template per <see cref="SpatialPass"/>, compiled with <c>-allow-glsl</c>. Godot's built-ins are globals the template
/// fills before and reads after the user functions; the colour pass lights the result with the engine's lights and
/// shadows (PBR, or Blinn-Phong with <c>specular_blinn</c>). Uniforms: one std140 block at binding 0 of the material set
/// (set 2; set 1 in the shadow casters) and up to <see cref="MaxSamplers"/> samplers at bindings 1…n. <c>#line</c>
/// directives make slangc's errors name the <c>.gdshader</c> and its line; the translator's own errors do too.
/// No <c>light()</c>, instance or global uniforms, or preprocessor yet.
/// </summary>
public static partial class SpatialShaderCompiler
{
    /// <summary>
    /// Samplers a spatial shader may declare: the mesh pipelines' fragment stage already uses 12 of MoltenVK's 16 samplers
    /// per stage (the frame's sky lighting, ambient occlusion and probes, the shadow maps).
    /// </summary>
    public const int MaxSamplers = 4;

    // Fixed varying locations of the template (a stage writes only what the next reads; Slang drops the rest).
    internal const int LocWorld = 0, LocNormal = 1, LocUv = 2, LocColor = 3, LocTime = 4, LocModel = 5, LocCurrent = 9, LocPrevious = 10,
        LocObjectId = 11, LocFirstVarying = 12, MaxLocations = 32;

    internal sealed record Chunk(string Code, int Line);

    internal sealed record Varying(string Type, string Name, bool Flat, int Location);

    internal sealed class Parsed
    {
        public required string Name;
        public required List<ShaderUniform> Uniforms;
        public int BlockSize;
        public int SamplerCount;
        public required List<Varying> Varyings;
        public required List<Chunk> Globals;
        public required List<Chunk> Helpers;
        public Chunk? Vertex;
        public Chunk? Fragment;
        public AlphaMode Alpha;
        public CullMode Cull;
        public SpatialBlendMode Blend;
        public SpatialDepthDraw DepthDraw;
        public bool DepthTestDisabled, Unshaded, BlinnPhong, WorldVertexCoords, FogDisabled, UsesNormalMap, UsesLightVertex;
    }

    /// <summary>True when <paramref name="source"/> declares <c>shader_type spatial</c>.</summary>
    public static bool IsSpatial(string source) => ShaderTypeOf(source) == "spatial";

    /// <summary>The shader's <c>shader_type</c>, or null.</summary>
    public static string? ShaderTypeOf(string source)
    {
        var m = ShaderTypeRegex().Match(CanvasShaderCompiler.StripComments(source ?? ""));
        return m.Success ? m.Groups["type"].Value : null;
    }

    /// <summary>Translates <paramref name="source"/> (the <c>.gdshader</c> text) named <paramref name="name"/> in errors.</summary>
    /// <exception cref="FormatException">Not a spatial shader, or a construct the translator does not handle (with the line).</exception>
    public static SpatialShaderProgram Translate(string source, string name = "shader")
    {
        ArgumentNullException.ThrowIfNull(source);
        var code = CanvasShaderCompiler.StripComments(source);
        var lineStarts = LineStarts(code);
        string? shaderType = null;
        var parsedUniforms = new List<CanvasShaderCompiler.ParsedUniform>();
        var uniformLines = new List<int>();
        var varyings = new List<(string Type, string Name, bool Flat, int Line)>();
        var globals = new List<Chunk>();
        var helpers = new List<Chunk>();
        Chunk? vertexFn = null, fragmentFn = null;
        var blend = SpatialBlendMode.Mix;
        var cull = CullMode.Back;
        var depthDraw = SpatialDepthDraw.Opaque;
        bool depthTestDisabled = false, unshaded = false, blinn = false, worldCoords = false, fogDisabled = false;

        foreach (var (statement, offset) in SplitTopLevel(code))
        {
            var line = LineOf(lineStarts, offset);
            var at = $"{name}({line.ToString(CultureInfo.InvariantCulture)})";
            var s = statement;
            if (s.StartsWith('#'))
                throw new FormatException($"{at}: preprocessor directives are not supported ('{CanvasShaderCompiler.Head(s)}').");
            if (s.StartsWith("shader_type", StringComparison.Ordinal))
            {
                shaderType = s["shader_type".Length..].Trim();
                continue;
            }

            if (s.StartsWith("render_mode", StringComparison.Ordinal))
            {
                foreach (var mode in s["render_mode".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    switch (mode)
                    {
                        case "blend_mix": blend = SpatialBlendMode.Mix; break;
                        case "blend_add": blend = SpatialBlendMode.Add; break;
                        case "blend_premul_alpha": blend = SpatialBlendMode.PremultAlpha; break;
                        case "cull_back": cull = CullMode.Back; break;
                        case "cull_front": cull = CullMode.Front; break;
                        case "cull_disabled": cull = CullMode.Disabled; break;
                        case "depth_draw_opaque": depthDraw = SpatialDepthDraw.Opaque; break;
                        case "depth_draw_always": depthDraw = SpatialDepthDraw.Always; break;
                        case "depth_draw_never": depthDraw = SpatialDepthDraw.Never; break;
                        case "depth_test_disabled": depthTestDisabled = true; break;
                        case "unshaded": unshaded = true; break;
                        case "world_vertex_coords": worldCoords = true; break;
                        case "fog_disabled": fogDisabled = true; break;
                        case "specular_blinn" or "specular_phong": blinn = true; break;
                        // The engine's PBR is Lambert + Schlick-GGX, Godot's defaults.
                        case "diffuse_lambert" or "diffuse_burley" or "specular_schlick_ggx": break;
                        default: throw new FormatException($"{at}: render_mode '{mode}' is not supported.");
                    }
                }

                continue;
            }

            if (s.StartsWith("group_uniforms", StringComparison.Ordinal))
                continue; // inspector grouping only
            if (s.StartsWith("instance uniform", StringComparison.Ordinal) || s.StartsWith("global uniform", StringComparison.Ordinal))
                throw new FormatException($"{at}: {s[..s.IndexOf(' ', StringComparison.Ordinal)]} uniforms are not supported yet.");
            if (s.StartsWith("uniform ", StringComparison.Ordinal))
            {
                parsedUniforms.Add(CanvasShaderCompiler.ParseUniform(s, at));
                uniformLines.Add(line);
                continue;
            }

            if (s.StartsWith("varying ", StringComparison.Ordinal))
            {
                var m = VaryingRegex().Match(s);
                if (!m.Success)
                    throw new FormatException($"{at}: cannot read varying '{s}'.");
                var type = m.Groups["type"].Value;
                varyings.Add((type, m.Groups["name"].Value, m.Groups["flat"].Success || type.StartsWith('i') || type.StartsWith('u') || type == "bool", line));
                continue;
            }

            if (s.StartsWith("struct ", StringComparison.Ordinal))
            {
                globals.Add(new Chunk(s + ";", line));
                continue;
            }

            if (s.EndsWith('}'))
            {
                var fm = CanvasShaderCompiler.FunctionRegex().Match(s);
                if (!fm.Success)
                    throw new FormatException($"{at}: cannot read the top-level block '{CanvasShaderCompiler.Head(s)}'.");
                switch (fm.Groups["name"].Value)
                {
                    case "vertex": vertexFn = new Chunk(s, line); break;
                    case "fragment": fragmentFn = new Chunk(s, line); break;
                    case "light": throw new FormatException($"{at}: light() is not supported yet (the engine lights the surface).");
                    default: helpers.Add(new Chunk(s, line)); break;
                }

                continue;
            }

            // Constants and other global declarations pass through; GLSL's global const is Slang's static const.
            globals.Add(new Chunk((s.StartsWith("const ", StringComparison.Ordinal) ? "static " : "") + s + ";", line));
        }

        if (shaderType != "spatial")
            throw new FormatException($"{name}: only shader_type spatial is supported here (found '{shaderType ?? "none"}').");

        var samplers = parsedUniforms.Count(u => u.Type == ShaderUniformType.Sampler2D);
        if (samplers > MaxSamplers)
        {
            var extra = parsedUniforms.Select((u, i) => (u, i)).Where(p => p.u.Type == ShaderUniformType.Sampler2D).ElementAt(MaxSamplers).i;
            throw new FormatException($"{name}({uniformLines[extra].ToString(CultureInfo.InvariantCulture)}): more than {MaxSamplers} sampler2D uniforms (the mesh pipelines' sampler budget).");
        }

        var laidOut = CanvasShaderCompiler.Layout(parsedUniforms, out var blockSize);
        var placed = new List<Varying>(varyings.Count);
        var location = LocFirstVarying;
        foreach (var v in varyings)
        {
            var count = v.Type switch { "mat2" => 2, "mat3" => 3, "mat4" => 4, _ => 1 };
            if (location + count > MaxLocations)
                throw new FormatException($"{name}({v.Line.ToString(CultureInfo.InvariantCulture)}): too many varyings (at most {MaxLocations - LocFirstVarying} locations).");
            placed.Add(new Varying(v.Type, v.Name, v.Flat, location));
            location += count;
        }

        // The surface code: fragment() and the helpers it may call (they write the same globals).
        var surface = string.Join('\n', helpers.Select(h => h.Code).Append(fragmentFn?.Code ?? ""));
        var alpha = surface.Contains("ALPHA_SCISSOR_THRESHOLD", StringComparison.Ordinal) ? AlphaMode.Cutout
            : AlphaWriteRegex().IsMatch(surface) || blend != SpatialBlendMode.Mix ? AlphaMode.Blend
            : AlphaMode.Opaque;
        return new SpatialShaderProgram(new Parsed
        {
            Name = name,
            Uniforms = laidOut,
            BlockSize = blockSize,
            SamplerCount = samplers,
            Varyings = placed,
            Globals = globals,
            Helpers = helpers,
            Vertex = vertexFn,
            Fragment = fragmentFn,
            Alpha = alpha,
            Cull = cull,
            Blend = blend,
            DepthDraw = depthDraw,
            DepthTestDisabled = depthTestDisabled,
            Unshaded = unshaded,
            BlinnPhong = blinn,
            WorldVertexCoords = worldCoords,
            FogDisabled = fogDisabled,
            UsesNormalMap = WordRegex("NORMAL_MAP").IsMatch(surface),
            UsesLightVertex = WordRegex("LIGHT_VERTEX").IsMatch(surface),
        });
    }

    // ── Templates ──────────────────────────────────────────────────────────────────────────────────────────────

    private static bool UsesFrame(SpatialPass pass) => pass is SpatialPass.Color or SpatialPass.Depth or SpatialPass.ObjectId;

    private static bool IsCaster(SpatialPass pass) => pass is SpatialPass.Shadow or SpatialPass.ShadowPoint;

    // The material set: 2 in the mesh pipelines, 1 in the shadow casters (set 0 is the light matrix there).
    private static int MaterialSet(SpatialPass pass) => IsCaster(pass) ? 1 : 2;

    // Godot's built-ins: globals of every stage (a helper may name any of them; unused ones vanish).
    private const string BuiltIns = """
        static vec3 VERTEX;
        static vec3 NORMAL;
        static vec3 TANGENT;
        static vec3 BINORMAL;
        static vec2 UV;
        static vec2 UV2;
        static vec4 COLOR;
        static float POINT_SIZE;
        static int INSTANCE_ID;
        static vec4 INSTANCE_CUSTOM;
        static int VERTEX_ID;
        static vec4 CUSTOM0;
        static vec4 CUSTOM1;
        static vec4 CUSTOM2;
        static vec4 CUSTOM3;
        static mat4 MODEL_MATRIX;
        static mat3 MODEL_NORMAL_MATRIX;
        static mat4 MODELVIEW_MATRIX;
        static mat3 MODELVIEW_NORMAL_MATRIX;
        static mat4 VIEW_MATRIX;
        static mat4 INV_VIEW_MATRIX;
        static mat4 MAIN_CAM_INV_VIEW_MATRIX;
        static mat4 PROJECTION_MATRIX;
        static mat4 INV_PROJECTION_MATRIX;
        static vec3 NODE_POSITION_WORLD;
        static vec3 NODE_POSITION_VIEW;
        static vec3 CAMERA_POSITION_WORLD;
        static vec3 CAMERA_DIRECTION_WORLD;
        static vec2 VIEWPORT_SIZE;
        static float TIME;
        static bool OUTPUT_IS_SRGB;
        static vec4 FRAGCOORD;
        static bool FRONT_FACING;
        static vec3 VIEW;
        static vec2 SCREEN_UV;
        static vec2 POINT_COORD;
        static vec3 ALBEDO;
        static float ALPHA;
        static float ALPHA_SCISSOR_THRESHOLD;
        static float ALPHA_HASH_SCALE;
        static float ALPHA_ANTIALIASING_EDGE;
        static vec2 ALPHA_TEXTURE_COORDINATE;
        static float METALLIC;
        static float SPECULAR;
        static float ROUGHNESS;
        static vec3 EMISSION;
        static float AO;
        static float AO_LIGHT_AFFECT;
        static vec3 NORMAL_MAP;
        static float NORMAL_MAP_DEPTH;
        static vec3 LIGHT_VERTEX;
        static float RIM;
        static float RIM_TINT;
        static float CLEARCOAT;
        static float CLEARCOAT_ROUGHNESS;
        static float ANISOTROPY;
        static vec2 ANISOTROPY_FLOW;
        static float SSS_STRENGTH;
        static vec4 SSS_TRANSMITTANCE_COLOR;
        static float SSS_TRANSMITTANCE_DEPTH;
        static float SSS_TRANSMITTANCE_BOOST;
        static vec3 BACKLIGHT;
        static vec4 FOG;
        static vec4 RADIANCE;
        static vec4 IRRADIANCE;

        """;

    // The pass's built-in sources: the frame (set 0) in the colour, depth and ID passes; the caster push block otherwise,
    // where "view space" is world space (VIEW_MATRIX = identity), so VERTEX and NORMAL mean the same in both.
    private static void AppendViewBuiltIns(Source o, SpatialPass pass, bool vertexStage)
    {
        o.Append("void gd_view_builtins()\n{\n");
        if (UsesFrame(pass))
        {
            o.Append("""
                    VIEW_MATRIX = frame.view;
                    INV_VIEW_MATRIX = spatial_inv_view(frame.invViewRotation, frame.cameraPosition.xyz);
                    MAIN_CAM_INV_VIEW_MATRIX = INV_VIEW_MATRIX;
                    PROJECTION_MATRIX = frame.projection;
                    INV_PROJECTION_MATRIX = frame.invProjection;
                    CAMERA_POSITION_WORLD = frame.cameraPosition.xyz;
                    CAMERA_DIRECTION_WORLD = -frame.invViewRotation[2].xyz;
                    VIEWPORT_SIZE = frame.viewport.xy;
                    TIME = frame.clip.z;

                """);
        }
        else
        {
            var push = vertexStage || pass == SpatialPass.ShadowPoint;
            o.Append("""
                    VIEW_MATRIX = float4x4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0);
                    INV_VIEW_MATRIX = VIEW_MATRIX;
                    INV_PROJECTION_MATRIX = VIEW_MATRIX;
                    CAMERA_DIRECTION_WORLD = float3(0.0, 0.0, -1.0);
                    VIEWPORT_SIZE = float2(1.0);

                """);
            o.Append(push
                ? "    CAMERA_POSITION_WORLD = gd_caster.time.yzw;\n    MAIN_CAM_INV_VIEW_MATRIX = float4x4(VIEW_MATRIX[0], VIEW_MATRIX[1], VIEW_MATRIX[2], float4(CAMERA_POSITION_WORLD, 1.0));\n    TIME = gd_caster.time.x;\n"
                : "    CAMERA_POSITION_WORLD = float3(0.0);\n    MAIN_CAM_INV_VIEW_MATRIX = VIEW_MATRIX;\n");
            o.Append(vertexStage ? "    PROJECTION_MATRIX = gd_lvp.lightViewProj;\n" : "    PROJECTION_MATRIX = VIEW_MATRIX;\n");
        }

        o.Append("    OUTPUT_IS_SRGB = false;\n}\n");
    }

    private static void AppendCommon(Source o, Parsed p, SpatialPass pass, bool vertexStage)
    {
        o.Append($"// Translated from the Godot spatial shader {p.Name} ({pass} pass) by SpatialShaderCompiler. Do not edit: edit the .gdshader.\n");
        if (UsesFrame(pass))
            o.Append("#include \"frame.slang\"\n");
        if (pass == SpatialPass.Color && !vertexStage)
            o.Append("#include \"shadows.slang\"\n#include \"lights.slang\"\n#include \"fog.slang\"\n");
        o.Append("#include \"spatial.slang\"\n");
        if (IsCaster(pass) && (vertexStage || pass == SpatialPass.ShadowPoint))
        {
            // MeshRenderer pushes the foliage casters' block (FoliageCasterWind: wind, time + camera, pass) at offset 0;
            // point passes the light at 64 (ShadowSystem). The light matrix is set 0 (a dynamic offset per pass).
            o.Append("struct GdCasterPush\n{\n    float4 wind;\n    float4 windParams;\n    float4 time;\n    float4 pass;\n");
            if (pass == SpatialPass.ShadowPoint)
                o.Append("    float4 lightPosRange;\n");
            o.Append("};\n[[vk::push_constant]] ConstantBuffer<GdCasterPush> gd_caster;\n");
        }

        if (IsCaster(pass) && vertexStage)
            o.Append("struct GdLightVP\n{\n    float4x4 lightViewProj;\n};\n[[vk::binding(0, 0)]] ConstantBuffer<GdLightVP> gd_lvp;\n");

        // AlphaMode (0 opaque, 1 cutout, 2 blend): the colour, depth and ID pipelines specialise it (a prepassed cutout
        // runs as opaque: its EQUAL depth test replaces the discard); the casters are built for the shader's own mode.
        if (!vertexStage)
        {
            o.Append(IsCaster(pass)
                ? string.Create(CultureInfo.InvariantCulture, $"static const int gd_alpha_mode = {(int)p.Alpha};\n")
                : "[vk::constant_id(0)] const int gd_alpha_mode = 0;\n");
        }

        var set = MaterialSet(pass);
        if (p.Uniforms.Any(u => !u.IsSampler))
        {
            o.Append($"[[vk::binding(0, {set})]] cbuffer MaterialUniforms\n{{\n");
            foreach (var u in p.Uniforms.Where(u => !u.IsSampler))
                o.Append("    ").Append(CanvasShaderCompiler.ShaderTypeName(u.Type)).Append(' ').Append(u.Name)
                    .Append(u.ArrayLength > 0 ? string.Create(CultureInfo.InvariantCulture, $"[{u.ArrayLength}]") : "").Append(";\n");
            o.Append("};\n");
        }

        foreach (var u in p.Uniforms.Where(u => u.IsSampler))
            o.Append(CultureInfo.InvariantCulture, $"[[vk::binding({u.Binding}, {set})]] Sampler2D {u.Name};\n");
        o.Append("#define TAU 6.2831853071795864769252867666\n#define E 2.7182818284590452353602874714\n");
        o.Append(BuiltIns);
        foreach (var v in p.Varyings)
            o.Append("static ").Append(v.Type).Append(' ').Append(v.Name).Append(";\n");
        foreach (var g in p.Globals)
            o.User(g, p.Name);
        foreach (var h in p.Helpers)
            o.User(h, p.Name);
    }

    // The interface fields (vertex outputs / fragment inputs) of a pass, by location.
    private static IEnumerable<(int Location, string Field)> Interface(Parsed p, SpatialPass pass)
    {
        yield return (LocWorld, "float3 gd_world");
        yield return (LocNormal, "float3 gd_normal");
        yield return (LocUv, "float2 gd_uv");
        yield return (LocColor, "float4 gd_color");
        if (pass == SpatialPass.Shadow)
            yield return (LocTime, "nointerpolation float gd_time");
        for (var r = 0; r < 4; r++)
            yield return (LocModel + r, string.Create(CultureInfo.InvariantCulture, $"nointerpolation float4 gd_model{r}"));
        if (pass == SpatialPass.Depth)
        {
            yield return (LocCurrent, "float4 gd_current");
            yield return (LocPrevious, "float4 gd_previous");
        }

        if (pass == SpatialPass.ObjectId)
            yield return (LocObjectId, "nointerpolation uint gd_object_id");
        foreach (var v in p.Varyings)
            yield return (v.Location, $"{(v.Flat ? "nointerpolation " : "")}{v.Type} {v.Name}");
    }

    internal static string WriteVertex(Parsed p, SpatialPass pass, IReadOnlySet<int>? fragmentInputs)
    {
        var o = new Source();
        AppendCommon(o, p, pass, vertexStage: true);
        AppendViewBuiltIns(o, pass, vertexStage: true);
        if (p.Vertex is { } vertexFn)
            o.User(vertexFn, p.Name);
        bool Written(int location) => fragmentInputs is null || fragmentInputs.Contains(location);

        o.Append("""
            struct VsIn
            {
                [[vk::location(0)]] float3 inPosition;
                [[vk::location(1)]] float3 inNormal;
                [[vk::location(2)]] float2 inUV;
                // System.Numerics row-vector matrix, uploaded as-is: its rows are the rows of the Slang matrix.
                [[vk::location(3)]] float4 inModel0;
                [[vk::location(4)]] float4 inModel1;
                [[vk::location(5)]] float4 inModel2;
                [[vk::location(6)]] float4 inModel3;
                [[vk::location(7)]] uint inObjectId;
                [[vk::location(8)]] float4 inColor;
                [[vk::location(9)]] float4 inCustom0;
                [[vk::location(10)]] float4 inPrevModel0;
                [[vk::location(11)]] float4 inPrevModel1;
                [[vk::location(12)]] float4 inPrevModel2;
                [[vk::location(13)]] float4 inPrevModel3;
                [[vk::location(16)]] float4 inInstanceCustom;
                [[vk::location(17)]] uint inInstanceIndex;
            };

            // The built-ins before vertex(): Godot's VERTEX/NORMAL in model space (world space with world_vertex_coords).
            void gd_setup(VsIn input, float4x4 model, uint vertexId)
            {
                MODEL_MATRIX = model;
                MODEL_NORMAL_MATRIX = spatial_normal_matrix(model);
                MODELVIEW_MATRIX = mul(model, VIEW_MATRIX);
                MODELVIEW_NORMAL_MATRIX = mul(MODEL_NORMAL_MATRIX, (float3x3)VIEW_MATRIX);
                NODE_POSITION_WORLD = model[3].xyz;
                NODE_POSITION_VIEW = mul(float4(NODE_POSITION_WORLD, 1.0), VIEW_MATRIX).xyz;
                VERTEX = input.inPosition;
                NORMAL = input.inNormal;
                TANGENT = float3(0.0);
                BINORMAL = float3(0.0);
                UV = input.inUV;
                UV2 = input.inUV;
                COLOR = input.inColor;
                CUSTOM0 = input.inCustom0;
                CUSTOM1 = float4(0.0);
                CUSTOM2 = float4(0.0);
                CUSTOM3 = float4(0.0);
                INSTANCE_CUSTOM = input.inInstanceCustom;
                INSTANCE_ID = int(input.inInstanceIndex);
                VERTEX_ID = int(vertexId);
                POINT_SIZE = 1.0;
                ROUGHNESS = 1.0;

            """);
        if (p.WorldVertexCoords)
            o.Append("    VERTEX = mul(float4(VERTEX, 1.0), model).xyz;\n    NORMAL = normalize(mul(NORMAL, MODEL_NORMAL_MATRIX));\n");
        o.Append("}\n\n");
        o.Append(p.WorldVertexCoords
            ? "float3 gd_world_vertex() { return VERTEX; }\nfloat3 gd_world_normal() { return NORMAL; }\n"
            : "float3 gd_world_vertex() { return mul(float4(VERTEX, 1.0), MODEL_MATRIX).xyz; }\nfloat3 gd_world_normal() { return mul(NORMAL, MODEL_NORMAL_MATRIX); }\n");

        o.Append("struct VsOut\n{\n    float4 position : SV_Position;\n");
        foreach (var (location, field) in Interface(p, pass))
            if (Written(location))
                o.Append(CultureInfo.InvariantCulture, $"    [[vk::location({location})]] {field};\n");
        o.Append("};\n\n");

        var call = p.Vertex is not null ? "    vertex();\n" : "";
        o.Append("[shader(\"vertex\")]\nVsOut main(VsIn input, uint vertexId : SV_VulkanVertexID)\n{\n    gd_view_builtins();\n");
        o.Append("    float4x4 gd_model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);\n");
        if (pass == SpatialPass.Depth)
        {
            // Last frame's position for the velocity first (vertex() at last frame's time and model matrix); then this
            // frame's, whose built-ins and varyings the fragment stage gets.
            o.Append("    gd_setup(input, float4x4(input.inPrevModel0, input.inPrevModel1, input.inPrevModel2, input.inPrevModel3), vertexId);\n");
            o.Append("    TIME = frame.temporal.y > 0.5 ? frame.temporal.x : frame.clip.z;\n");
            o.Append(call);
            o.Append("    float3 gd_previous_world = gd_world_vertex();\n    TIME = frame.clip.z;\n");
        }

        o.Append("    gd_setup(input, gd_model, vertexId);\n").Append(call);
        o.Append("    float3 gd_position = gd_world_vertex();\n    VsOut o;\n");
        // The clip position: the same expression in the colour and depth passes (and invariant, SpirvInvariance), so the
        // scene pass meets the prepass depth exactly.
        o.Append(IsCaster(pass)
            ? "    o.position = mul(float4(gd_position, 1.0), gd_lvp.lightViewProj);\n"
            : "    o.position = mul(float4(gd_position, 1.0), frame.viewProjection);\n");
        if (Written(LocWorld))
            o.Append("    o.gd_world = gd_position;\n");
        if (Written(LocNormal))
            o.Append("    o.gd_normal = gd_world_normal();\n");
        if (Written(LocUv))
            o.Append("    o.gd_uv = UV;\n");
        if (Written(LocColor))
            o.Append("    o.gd_color = COLOR;\n");
        if (pass == SpatialPass.Shadow && Written(LocTime))
            o.Append("    o.gd_time = TIME;\n");
        for (var r = 0; r < 4; r++)
            if (Written(LocModel + r))
                o.Append(CultureInfo.InvariantCulture, $"    o.gd_model{r} = gd_model[{r}];\n");
        if (pass == SpatialPass.Depth)
        {
            if (Written(LocCurrent))
                o.Append("    o.gd_current = unjitterClip(o.position);\n");
            if (Written(LocPrevious))
                o.Append("    o.gd_previous = mul(float4(gd_previous_world, 1.0), frame.prevViewProjection);\n");
        }

        if (pass == SpatialPass.ObjectId && Written(LocObjectId))
            o.Append("    o.gd_object_id = input.inObjectId;\n");
        foreach (var v in p.Varyings)
            if (Written(v.Location))
                o.Append("    o.").Append(v.Name).Append(" = ").Append(v.Name).Append(";\n");
        o.Append("    return o;\n}\n");
        return o.ToString();
    }

    internal static string WriteFragment(Parsed p, SpatialPass pass)
    {
        var o = new Source();
        AppendCommon(o, p, pass, vertexStage: false);
        AppendViewBuiltIns(o, pass, vertexStage: false);
        if (p.Fragment is { } fragmentFn)
            o.User(fragmentFn, p.Name);

        o.Append("struct FsIn\n{\n");
        foreach (var (location, field) in Interface(p, pass))
            o.Append(CultureInfo.InvariantCulture, $"    [[vk::location({location})]] {field};\n");
        o.Append("};\n\n");

        var signature = pass switch
        {
            SpatialPass.Color => "float4 main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace) : SV_Target0",
            SpatialPass.Depth => "float2 main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace) : SV_Target0",
            SpatialPass.ShadowPoint => "float main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace) : SV_Depth",
            SpatialPass.ObjectId => "uint main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace) : SV_Target0",
            _ => "void main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace)",
        };
        o.Append("[shader(\"fragment\")]\n").Append(signature).Append("\n{\n    gd_view_builtins();\n");
        if (pass == SpatialPass.Shadow)
            o.Append("    TIME = input.gd_time;\n");
        foreach (var v in p.Varyings)
            o.Append("    ").Append(v.Name).Append(" = input.").Append(v.Name).Append(";\n");
        // Derivatives first, while every invocation of the quad is alive (before fragment()'s branches and discards).
        o.Append("""
                float3 gd_dpdx = ddx(input.gd_world);
                float3 gd_dpdy = ddy(input.gd_world);
                float2 gd_duvdx = ddx(input.gd_uv);
                float2 gd_duvdy = ddy(input.gd_uv);
                MODEL_MATRIX = float4x4(input.gd_model0, input.gd_model1, input.gd_model2, input.gd_model3);
                MODEL_NORMAL_MATRIX = spatial_normal_matrix(MODEL_MATRIX);
                MODELVIEW_MATRIX = mul(MODEL_MATRIX, VIEW_MATRIX);
                MODELVIEW_NORMAL_MATRIX = mul(MODEL_NORMAL_MATRIX, (float3x3)VIEW_MATRIX);
                NODE_POSITION_WORLD = MODEL_MATRIX[3].xyz;
                NODE_POSITION_VIEW = mul(float4(NODE_POSITION_WORLD, 1.0), VIEW_MATRIX).xyz;
                float3 gd_geometry_normal = normalize(input.gd_normal);

            """);
        if (p.Cull == CullMode.Disabled)
            o.Append("    if (!frontFacing)\n        gd_geometry_normal = -gd_geometry_normal; // Godot's side check: back faces face the viewer\n");
        o.Append("""
                VERTEX = mul(float4(input.gd_world, 1.0), VIEW_MATRIX).xyz;
                LIGHT_VERTEX = VERTEX;
                NORMAL = mul(float4(gd_geometry_normal, 0.0), VIEW_MATRIX).xyz;
                TANGENT = float3(0.0);
                BINORMAL = float3(0.0);
                VIEW = dot(VERTEX, VERTEX) > 0.0 ? normalize(-VERTEX) : float3(0.0, 0.0, 1.0);
                UV = input.gd_uv;
                UV2 = input.gd_uv;
                COLOR = input.gd_color;
                FRAGCOORD = fragCoord;
                FRONT_FACING = frontFacing;
                POINT_COORD = float2(0.5);
                ALBEDO = float3(1.0);
                ALPHA = 1.0;
                ALPHA_SCISSOR_THRESHOLD = 0.0;
                METALLIC = 0.0;
                SPECULAR = 0.5;
                ROUGHNESS = 1.0;
                EMISSION = float3(0.0);
                AO = 1.0;
                AO_LIGHT_AFFECT = 0.0;
                NORMAL_MAP = float3(0.5, 0.5, 1.0);
                NORMAL_MAP_DEPTH = 1.0;

            """);
        o.Append(UsesFrame(pass) ? "    SCREEN_UV = fragCoord.xy * frame.viewport.zw;\n" : "    SCREEN_UV = float2(0.0);\n");
        if (p.Fragment is not null)
            o.Append("    fragment();\n");
        o.Append("    if (gd_alpha_mode == 1 && ALPHA < ALPHA_SCISSOR_THRESHOLD)\n        discard;\n");

        switch (pass)
        {
            case SpatialPass.Depth:
                o.Append("    return velocityFromClip(input.gd_current, input.gd_previous);\n}\n");
                return o.ToString();
            case SpatialPass.ObjectId:
                o.Append("    return input.gd_object_id;\n}\n");
                return o.ToString();
            case SpatialPass.ShadowPoint:
                o.Append("    return length(input.gd_world - gd_caster.lightPosRange.xyz) / gd_caster.lightPosRange.w;\n}\n");
                return o.ToString();
            case SpatialPass.Shadow:
                o.Append("}\n");
                return o.ToString();
        }

        // The colour pass: the surface lit by the engine (lights, shadows, sky lighting, SSAO), fogged.
        o.Append("    float3 gd_n = normalize(mul(float4(NORMAL, 0.0), INV_VIEW_MATRIX).xyz);\n");
        if (p.UsesNormalMap)
            o.Append("    gd_n = spatial_normal_map(gd_n, NORMAL_MAP, NORMAL_MAP_DEPTH, gd_dpdx, gd_dpdy, gd_duvdx, gd_duvdy);\n");
        o.Append(p.UsesLightVertex
            ? "    float3 gd_light_position = mul(float4(LIGHT_VERTEX, 1.0), INV_VIEW_MATRIX).xyz;\n"
            : "    float3 gd_light_position = input.gd_world;\n");
        if (p.Unshaded)
        {
            o.Append("    float3 gd_color = ALBEDO;\n");
        }
        else
        {
            o.Append("    float gd_ssao = gd_alpha_mode == 2 ? 1.0 : ambientOcclusionAt(fragCoord);\n");
            if (p.BlinnPhong)
            {
                // FoliageMaterial3D.BlinnFromRoughness, scaled by SPECULAR (0.5 = Godot's default = that strength).
                o.Append("""
                        float gd_r = clamp(ROUGHNESS, 0.05, 1.0);
                        float gd_a = gd_r * gd_r;
                        float gd_shininess = clamp(2.0 / (gd_a * gd_a) - 2.0, 1.0, 1024.0);
                        float3 gd_color = shadeLightsBlinnPhong(ALBEDO, gd_n, gd_geometry_normal, gd_light_position,
                            0.5 * (1.0 - gd_r) * SPECULAR, gd_shininess, gd_ssao);

                    """);
            }
            else
            {
                o.Append("""
                        PbrSurface gd_surface;
                        gd_surface.albedo = ALBEDO;
                        gd_surface.metallic = saturate(METALLIC);
                        gd_surface.roughness = saturate(ROUGHNESS);
                        gd_surface.ao = saturate(AO);
                        gd_surface.N = gd_n;
                        float3 gd_color = shadeLightsPbr(gd_surface, gd_geometry_normal, gd_light_position, gd_ssao);

                    """);
            }
        }

        o.Append("    gd_color += EMISSION;\n");
        if (!p.FogDisabled)
            o.Append("    gd_color = applyFog(gd_color, input.gd_world);\n");
        o.Append("    return float4(gd_color, gd_alpha_mode == 2 ? ALPHA : 1.0);\n}\n");
        return o.ToString();
    }

    /// <summary>Generated Slang with <c>#line</c> directives around the user's code (errors name the .gdshader and its line).</summary>
    private sealed class Source
    {
        private readonly StringBuilder _text = new();
        private int _line = 1;

        public Source Append(string s)
        {
            _text.Append(s);
            foreach (var c in s)
                if (c == '\n')
                    _line++;
            return this;
        }

        public Source Append(char c) => Append(c.ToString());

        public Source Append(IFormatProvider provider, FormattableString s) => Append(s.ToString(provider));

        public void User(Chunk chunk, string file)
        {
            Append(string.Create(CultureInfo.InvariantCulture, $"#line {chunk.Line} \"{file}\"\n"));
            Append(CanvasShaderCompiler.GlslMatrixConstructors(chunk.Code));
            Append("\n");
            Append(string.Create(CultureInfo.InvariantCulture, $"#line {_line + 1} \"{file} (generated)\"\n"));
        }

        public override string ToString() => _text.ToString();
    }

    // Top-level statements with the offset of their first character: ';'-terminated declarations and '{…}' blocks.
    private static List<(string Text, int Offset)> SplitTopLevel(string code)
    {
        var result = new List<(string, int)>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    Add(start, i + 1);
                    start = i + 1;
                }
            }
            else if (c == ';' && depth == 0)
            {
                Add(start, i);
                start = i + 1;
            }
        }

        Add(start, code.Length);
        return result;

        void Add(int from, int to)
        {
            while (from < to && char.IsWhiteSpace(code[from]))
                from++;
            if (from < to)
                result.Add((code[from..to].TrimEnd(), from));
        }
    }

    private static int[] LineStarts(string code)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < code.Length; i++)
            if (code[i] == '\n')
                starts.Add(i + 1);
        return [.. starts];
    }

    private static int LineOf(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    private static Regex WordRegex(string word) => new($@"\b{word}\b", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\bshader_type\s+(?<type>\w+)")]
    private static partial Regex ShaderTypeRegex();

    // ALPHA assigned (=, +=, …; not ==): Godot then blends the surface.
    [GeneratedRegex(@"\bALPHA\s*[-+*/]?=(?!=)")]
    private static partial Regex AlphaWriteRegex();

    [GeneratedRegex(@"^varying\s+(?<flat>flat\s+)?(?:(?:smooth|lowp|mediump|highp)\s+)*(?<type>\w+)\s+(?<name>\w+)$")]
    private static partial Regex VaryingRegex();
}
