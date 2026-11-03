namespace MainframeEngine;

/// <summary>The scalar/vector/matrix types a surface shader's material parameter can have.</summary>
#pragma warning disable CA1720 // shader type names
public enum ShaderUniformType : byte
{
    Bool,
    Int,
    UInt,
    Float,
    Vec2,
    Vec3,
    Vec4,
    IVec2,
    IVec3,
    IVec4,
    Mat2,
    Mat3,
    Mat4,
    Sampler2D,
}
#pragma warning restore CA1720

/// <summary>
/// One material parameter of a surface shader (a field of its <c>struct Material</c>, ADR 0237): its std140 place in the
/// material block, or its texture binding, as slangc laid it out.
/// </summary>
public sealed record ShaderUniform(
    string Name,
    ShaderUniformType Type,
    int ArrayLength,
    int Offset,
    int Size,
    int Stride,
    float[]? Default,
    bool SourceColor,
    int Binding,
    CanvasTextureFilter Filter,
    CanvasTextureRepeat Repeat,
    ShaderTextureHint TextureHint = ShaderTextureHint.White)
{
    public bool IsSampler => Type == ShaderUniformType.Sampler2D;

    /// <summary>Number of float/int components (matrices count every element).</summary>
    public static int Components(ShaderUniformType type) => type switch
    {
        ShaderUniformType.Vec2 or ShaderUniformType.IVec2 => 2,
        ShaderUniformType.Vec3 or ShaderUniformType.IVec3 => 3,
        ShaderUniformType.Vec4 or ShaderUniformType.IVec4 => 4,
        ShaderUniformType.Mat2 => 4,
        ShaderUniformType.Mat3 => 9,
        ShaderUniformType.Mat4 => 16,
        _ => 1,
    };
}

/// <summary>
/// What an unset texture parameter samples (<c>[defaultTexture(DefaultTexture.…)]</c>; white without it); spatial shaders
/// sample a <see cref="Normal"/> texture as linear data.
/// </summary>
public enum ShaderTextureHint : byte
{
    White,
    Black,
    Transparent,
    Normal,
}
