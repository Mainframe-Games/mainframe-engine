using System.Numerics;

namespace MainframeEngine;

// Engine glue for the Godot-derived math types (Math/*.cs). They are built-in value types (scenes and resources store them
// as number arrays: Serialization/Codecs.cs, the generator's BuiltInValueTypes); Color converts to and from the Vector4
// the canvas and UI renderers take.

public partial struct Color
{
    /// <summary>The renderer form (R, G, B, A as X, Y, Z, W).</summary>
    public static implicit operator Vector4(Color c) => new(c.R, c.G, c.B, c.A);

    public static implicit operator Color(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
