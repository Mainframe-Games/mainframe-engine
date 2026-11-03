import mainframe.spatial;
// Procedural ground from the world position alone (ADR 0205), the way Crash Site Defense streams its chunks: low-frequency
// value noise picks a biome, a wobble breaks its border, mottling, darkened borders and specks finish it. Nothing is stored
// per pixel, so any number of meshes tile seamlessly.

struct Material
{
    [color] float4 moss = float4(0.36, 0.44, 0.24, 1.0);
    [color] float4 moss_light = float4(0.48, 0.54, 0.29, 1.0);
    [color] float4 dust = float4(0.58, 0.46, 0.33, 1.0);
    [color] float4 dust_light = float4(0.70, 0.60, 0.45, 1.0);
    float biome_scale = 0.08;
    [range(0.0, 1.0)] float speck_density = 0.06;
};

float hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

float value_noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash(i), hash(i + float2(1.0, 0.0)), u.x), lerp(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), u.x), u.y);
}

void fragment(inout SurfaceFragment s, Material m)
{
    float2 world = mul(float4(s.vertex, 1.0), s.invViewMatrix).xz;
    float wobble = value_noise(world * 0.9) * 0.18;
    float biome = value_noise(world * m.biome_scale) + wobble;
    float border = 1.0 - smoothstep(0.0, 0.05, abs(biome - 0.6));
    float3 low = lerp(m.moss.rgb, m.moss_light.rgb, value_noise(world * 0.7));
    float3 high = lerp(m.dust.rgb, m.dust_light.rgb, value_noise(world * 0.7 + 9.0));
    float3 albedo = lerp(low, high, step(0.6, biome)) * (1.0 - 0.35 * border);
    float speck = step(1.0 - m.speck_density, hash(floor(world * 10.0)));
    s.albedo = albedo * (1.0 - 0.4 * speck);
    s.roughness = 0.92;
}
