import mainframe.spatial;
// Render-test spatial shader (ADR 0216): a screen showing a sub-viewport through a texture parameter. The channels are
// rotated (rgb → gbr), so the test tells this shader's draw from a standard material's.

[unshaded]
struct Material
{
    [color, filter(Filter.Linear), repeat(Repeat.Disabled)] Sampler2D screen;
};

void fragment(inout SurfaceFragment s, Material m)
{
    s.albedo = m.screen.Sample(s.uv).gbr;
}
