namespace MainframeEngine;

/// <summary>
/// A canvas surface shader's material interface (<c>import mainframe.canvas;</c>, ADR 0237): its parameters, whether it
/// has <c>vertex()</c>/<c>fragment()</c>, and its render modes. Built by mf-shaders from slangc's reflection and read back
/// from the shader's <c>x.slang.mflayout.json</c>; parameters are one std140 block at set 1 binding 0 and textures at
/// bindings 1….
/// </summary>
public sealed record CanvasShaderProgram(
    IReadOnlyList<ShaderUniform> Uniforms,
    int UniformBlockSize,
    bool HasVertexFunction,
    bool HasFragmentFunction,
    CanvasBlendMode BlendMode,
    bool Unshaded,
    bool LightOnly)
{
    public int SamplerCount => Uniforms.Count(u => u.IsSampler);
}
