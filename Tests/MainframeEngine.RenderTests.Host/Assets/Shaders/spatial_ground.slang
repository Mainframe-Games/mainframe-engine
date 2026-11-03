import mainframe.spatial;
// Render-test spatial shader (ADR 0205): procedural ground from the world position, as Crash Site Defense's streamed
// chunks do. A nearest-filtered biome map picks one of two palettes per 4 m cell (sampled at a wobbled world position),
// value noise mottles it, a hash grid adds specks; lit by the engine and shadowed. Parameters: a colour array, scalars and
// a texture with attributes.

struct Material
{
    [color] float4 palette[4];
    float cell_size = 4.0;
    float mottle_scale = 0.6;
    [range(0.0, 1.0)] float speck_density = 0.08;
    [filter(Filter.Nearest), repeat(Repeat.Disabled)] Sampler2D biome_map;
};

static const float WOBBLE = 0.35;

float hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

float value_noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + float2(1.0, 0.0));
    float c = hash(i + float2(0.0, 1.0));
    float d = hash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

void fragment(inout SurfaceFragment s, Material m)
{
    float2 world = mul(float4(s.vertex, 1.0), s.invViewMatrix).xz;
    float2 wobble = float2(value_noise(world * 0.7), value_noise(world * 0.7 + 17.0)) - 0.5;
    float2 map_uv = (world + wobble * WOBBLE * m.cell_size) / (m.cell_size * 4.0) + 0.5;
    float biome = m.biome_map.Sample(map_uv).r;
    float3 low = lerp(m.palette[0].rgb, m.palette[1].rgb, value_noise(world * m.mottle_scale));
    float3 high = lerp(m.palette[2].rgb, m.palette[3].rgb, value_noise(world * m.mottle_scale + 5.0));
    float3 albedo = lerp(low, high, step(0.5, biome));
    float speck = step(1.0 - m.speck_density, hash(floor(world * 8.0)));
    s.albedo = albedo * (1.0 - 0.45 * speck);
    s.roughness = 0.9;
}
