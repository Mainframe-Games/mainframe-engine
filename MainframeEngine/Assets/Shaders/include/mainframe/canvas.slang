// The surface-shader contract for 2D canvas items (ADR 0237, ADR 0113's pipeline): `import mainframe.canvas;` as the first
// statement of a game's .slang file. The engine generates the vertex and fragment stages and calls the shader's optional
// functions:
//
//     void vertex(inout CanvasVertex v[, Material m][, out Varyings o])
//     void fragment(inout CanvasFragment s[, Material m][, Varyings i])
//
// `struct Material` (optional) holds the parameters, with their defaults as literal initializers, and the render modes
// as attributes on the struct; `struct Varyings` (optional) the values vertex passes to fragment. Positions are canvas
// pixels; matrices are the C# values: transform with mul(v, M). See docs/design/canvas.md#canvas-shaders.
module mainframe_canvas;

__exported import mainframe.math;
__exported import mainframe.material;
__exported import mainframe.surface.canvas;

/// How the item blends (`[blend(Blend.Add)]`).
public enum Blend
{
    Mix,
    Add,
    Sub,
    Mul,
    PremulAlpha,
    Disabled,
};

/// The blend mode.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct blendAttribute
{
    Blend mode;
};

/// The canvas modulation (CanvasModulate) and 2D lights do not apply.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct unshadedAttribute
{
};

/// Only the 2D lights' contribution is drawn.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct lightOnlyAttribute
{
};
