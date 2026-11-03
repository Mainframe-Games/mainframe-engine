import mainframe.canvas;
// Fixture (SurfaceShaderTests): a canvas shader with render modes, a texture and varyings.

[blend(Blend.Mul), lightOnly]
struct Material
{
    [color] float4 tints[2];
    float stripes = 6.0;
    [filter(Filter.Nearest)] Sampler2D mask;
};

struct Varyings
{
    float2 world;
};

void vertex(inout CanvasVertex v, out Varyings o)
{
    o.world = mul(float4(v.vertex, 0.0, 1.0), v.modelMatrix).xy;
}

void fragment(inout CanvasFragment s, Material m, Varyings i)
{
    s.color = lerp(m.tints[0], m.tints[1], frac(i.world.x * m.stripes)) * m.mask.Sample(s.uv).a;
}
