// What every surface shader's `struct Material` can say about its parameters (ADR 0237): the attributes mf-shaders reads
// from slangc's reflection. Re-exported by mainframe.spatial and mainframe.canvas, so a shader imports only one of those.
// The enums' orders are the C# ones (SlangReflection maps them by value).
module mainframe_material;

/// How a texture parameter is filtered (`[filter(Filter.Nearest)]`). Inherit: the item's or texture's own sampling.
public enum Filter
{
    Inherit,
    Nearest,
    Linear,
};

/// Whether a texture parameter repeats outside 0..1 (`[repeat(Repeat.Disabled)]`). Inherit: the item's or texture's own.
public enum Repeat
{
    Inherit,
    Disabled,
    Enabled,
};

/// What an unset texture parameter samples (`[defaultTexture(DefaultTexture.Black)]`; white without the attribute).
/// Normal: a flat normal map (0.5, 0.5, 1), sampled as linear data.
public enum DefaultTexture
{
    White,
    Black,
    Transparent,
    Normal,
};

/// A colour parameter: a vector authored in sRGB (spatial shaders get it converted to linear, as every authored colour;
/// canvas shaders get it as written), or a texture sampled as sRGB.
[__AttributeUsage(_AttributeTargets.Var)]
public struct colorAttribute
{
};

/// The range an editor offers for a numeric parameter. Not enforced.
[__AttributeUsage(_AttributeTargets.Var)]
public struct rangeAttribute
{
    float min;
    float max;
};

/// A texture parameter's filter, over the item's or texture's own.
[__AttributeUsage(_AttributeTargets.Var)]
public struct filterAttribute
{
    Filter mode;
};

/// A texture parameter's repeat, over the item's or texture's own.
[__AttributeUsage(_AttributeTargets.Var)]
public struct repeatAttribute
{
    Repeat mode;
};

/// What an unset texture parameter samples.
[__AttributeUsage(_AttributeTargets.Var)]
public struct defaultTextureAttribute
{
    DefaultTexture texture;
};

/// The inspector group a parameter is listed under.
[__AttributeUsage(_AttributeTargets.Var)]
public struct groupAttribute
{
    string name;
};
