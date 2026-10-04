using System.Numerics;

namespace MainframeEngine;

public abstract class Light
{
    private Vector3 _color = Vector3.One;

    public Vector3 Position { get; set; }

    /// <summary>Light colour as authored (sRGB, like a colour picker); lighting uses <see cref="LinearColor"/>.</summary>
    public Vector3 Color
    {
        get => _color;
        set
        {
            _color = value;
            LinearColor = ColorSpace.SrgbToLinear(value);
        }
    }

    /// <summary><see cref="Color"/> converted to linear once, when set (what the lights UBO carries).</summary>
    public Vector3 LinearColor { get; private set; } = Vector3.One;

    public float Intensity { get; set; } = 1f;
}
