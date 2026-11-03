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

/// <summary>A spatial shader's <c>[blend(Blend.…)]</c> (the order of mainframe.spatial's <c>Blend</c>).</summary>
public enum SpatialBlendMode : byte
{
    Mix,
    Add,

    /// <summary><c>Blend.PremulAlpha</c>: the shader's colour is already multiplied by its alpha.</summary>
    PremultAlpha,
}

/// <summary>A spatial shader's <c>[depthDraw(DepthDraw.…)]</c>.</summary>
public enum SpatialDepthDraw : byte
{
    /// <summary>The default: opaque and cut-out surfaces write depth, blended ones do not.</summary>
    Opaque,

    /// <summary>Blended surfaces write depth too.</summary>
    Always,

    /// <summary>Nothing writes depth (the surface is not in the depth prepass).</summary>
    Never,
}

/// <summary>
/// A spatial surface shader's material interface (<c>import mainframe.spatial;</c>, ADR 0237 over ADR 0205's passes): its
/// parameters and render modes, built by mf-shaders from slangc's reflection and read back from the shader's
/// <c>x.slang.layout.json</c>. Parameters are one std140 block at binding 0 of the material set (set 2; set 1 in the shadow
/// casters) and up to <see cref="MaxSamplers"/> textures at bindings 1….
/// </summary>
public sealed record SpatialShaderProgram(
    string Name,
    IReadOnlyList<ShaderUniform> Uniforms,
    int UniformBlockSize,
    bool HasVertexFunction,
    bool HasFragmentFunction,
    AlphaMode Alpha,
    CullMode Cull,
    SpatialBlendMode BlendMode,
    SpatialDepthDraw DepthDraw,
    bool DepthTestDisabled,
    bool Unshaded,
    bool BlinnPhong,
    bool WorldVertexCoords,
    bool FogDisabled)
{
    /// <summary>
    /// Textures a spatial shader may declare: the mesh pipelines' fragment stage already uses 12 of MoltenVK's 16 samplers
    /// per stage (the frame's sky lighting, ambient occlusion and probes, the shadow maps).
    /// </summary>
    public const int MaxSamplers = 4;

    public int SamplerCount => Uniforms.Count(u => u.IsSampler);

    /// <summary>The pipeline state the shader's render modes ask for (part of the pipeline key).</summary>
    public MaterialRenderState RenderState =>
        new(Alpha, Cull, DoubleSided: false, NoDepthTest: DepthTestDisabled, DepthDraw: DepthDraw switch
        {
            SpatialDepthDraw.Always => DepthDrawMode.Always,
            SpatialDepthDraw.Never => DepthDrawMode.Never,
            _ => DepthDrawMode.Default,
        });

    /// <summary>
    /// What a pass's SPIR-V files add to the shader's path: <c>x.slang.vert.spv</c> for the colour pass,
    /// <c>x.slang.depth.vert.spv</c>, <c>.shadow</c>, <c>.shadow-point</c> and <c>.id</c> for the others.
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
