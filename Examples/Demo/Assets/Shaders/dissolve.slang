import mainframe.spatial;
// The dissolve of a felled prop (ADR 0205): object-space noise against `progress` cuts the surface away (alpha against
// alphaScissorThreshold: an alpha-tested cutout, in its shadow and in picking too) with a glowing edge; `hit_flash` tints
// the surface for a moment when the prop is struck (its alpha is the strength).

[alpha(Alpha.Cutout)]
struct Material
{
    [range(0.0, 1.0)] float progress = 0.3;
    float edge_width = 0.07;
    [color] float4 base_color = float4(0.42, 0.55, 0.72, 1.0);
    [color] float3 edge_color = float3(1.0, 0.5, 0.12);
    float edge_energy = 5.0;
    [color] float4 hit_flash = float4(1.0, 0.35, 0.3, 0.0);
};

struct Varyings
{
    float3 local;
};

float hash3(float3 p)
{
    return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
}

float noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float a = lerp(lerp(hash3(i), hash3(i + float3(1.0, 0.0, 0.0)), u.x), lerp(hash3(i + float3(0.0, 1.0, 0.0)), hash3(i + float3(1.0, 1.0, 0.0)), u.x), u.y);
    float b = lerp(lerp(hash3(i + float3(0.0, 0.0, 1.0)), hash3(i + float3(1.0, 0.0, 1.0)), u.x), lerp(hash3(i + float3(0.0, 1.0, 1.0)), hash3(i + float3(1.0, 1.0, 1.0)), u.x), u.y);
    return lerp(a, b, u.z);
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    o.local = v.vertex;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float n = noise3(i.local * 3.0) * 0.65 + noise3(i.local * 8.0) * 0.35;
    float edge = 1.0 - smoothstep(0.0, m.edge_width, n - m.progress);
    s.albedo = lerp(m.base_color.rgb, m.hit_flash.rgb, m.hit_flash.a);
    s.emission = m.edge_color * m.edge_energy * edge * step(0.001, m.progress) + m.hit_flash.rgb * m.hit_flash.a * 0.6;
    s.roughness = 0.45;
    s.metallic = 0.15;
    s.alpha = n;
    s.alphaScissorThreshold = m.progress;
}
