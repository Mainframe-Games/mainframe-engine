import mainframe.spatial;
// Render-test spatial shader (ADR 0205): the dissolve of a felled prop. Object-space value noise against `progress`
// cuts the surface away (alpha against alphaScissorThreshold: an alpha-tested cutout, in the shadow and picking passes
// too) and an emissive band glows along the cut edge; `hit_flash` tints the whole surface (a parameter set from C#).

[alpha(Alpha.Cutout)]
struct Material
{
    [range(0.0, 1.0)] float progress = 0.45;
    float edge_width = 0.08;
    [color] float4 base_color = float4(0.30, 0.55, 0.85, 1.0);
    [color] float3 edge_color = float3(1.0, 0.45, 0.1);
    float edge_energy = 4.0;
    [color] float4 hit_flash = float4(1.0, 1.0, 1.0, 0.0);
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
    float n000 = hash3(i);
    float n100 = hash3(i + float3(1.0, 0.0, 0.0));
    float n010 = hash3(i + float3(0.0, 1.0, 0.0));
    float n110 = hash3(i + float3(1.0, 1.0, 0.0));
    float n001 = hash3(i + float3(0.0, 0.0, 1.0));
    float n101 = hash3(i + float3(1.0, 0.0, 1.0));
    float n011 = hash3(i + float3(0.0, 1.0, 1.0));
    float n111 = hash3(i + float3(1.0, 1.0, 1.0));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y), lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    o.local = v.vertex;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float n = noise3(i.local * 3.5) * 0.65 + noise3(i.local * 9.0) * 0.35;
    float edge = 1.0 - smoothstep(0.0, m.edge_width, n - m.progress);
    s.albedo = lerp(m.base_color.rgb, m.hit_flash.rgb, m.hit_flash.a);
    s.emission = m.edge_color * m.edge_energy * edge;
    s.roughness = 0.5;
    s.metallic = 0.1;
    s.alpha = n;
    s.alphaScissorThreshold = m.progress;
}
