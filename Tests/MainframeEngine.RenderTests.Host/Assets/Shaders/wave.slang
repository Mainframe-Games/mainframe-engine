import mainframe.canvas;
// Render-test canvas shader: a vertex wobble from model-matrix world space, a colour array parameter, a texture
// parameter and the time/fragCoord/uv built-ins (docs/design/canvas.md, canvas shaders).

struct Material
{
    [color] float4 tints[2];
    float stripes = 6.0;
    [filter(Filter.Nearest), repeat(Repeat.Disabled)] Sampler2D mask;
};

struct Varyings
{
    float2 world;
};

void vertex(inout CanvasVertex v, out Varyings o)
{
    o.world = mul(float4(v.vertex, 0.0, 1.0), v.modelMatrix).xy;
    v.vertex.y += sin(v.vertex.x * 0.5) * 2.0;
}

void fragment(inout CanvasFragment s, Material m, Varyings i)
{
    float band = step(0.5, frac(s.uv.x * m.stripes));
    float4 tint = lerp(m.tints[0], m.tints[1], band);
    float mask = m.mask.Sample(s.uv).a;
    s.color = float4(tint.rgb * (0.75 + 0.25 * frac(i.world.y / 40.0)), mask * s.color.a);
}
