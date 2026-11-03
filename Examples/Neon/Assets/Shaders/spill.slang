import mainframe.spatial;
// Light on the wet ground (World/NeonCity.cs): the pools in front of the shops and under the street lamps. Nothing in the
// city is a light, so this is what a shop's window does to the pavement. color is the pool's colour, uv.y runs from its
// bright edge or centre (0) to where it fades (1) and uv.x across it (the edges fade too).

[unshaded, blend(Blend.Add), cull(Cull.Disabled), depthDraw(DepthDraw.Never), noFog]
struct Material
{
    float energy = 1.0;
};

struct Varyings
{
    float3 world;
};

float hash12(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float value_noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1.0, 0.0)), u.x), lerp(hash12(i + float2(0.0, 1.0)), hash12(i + float2(1.0, 1.0)), u.x), u.y);
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    o.world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float along = 1.0 - clamp(s.uv.y, 0.0, 1.0);
    float across = 1.0 - pow(abs(s.uv.x * 2.0 - 1.0), 4.0);
    // The wet ground breaks the pool up: brighter where water stands.
    float wet = 0.65 + 0.7 * value_noise(i.world.xz * 0.7) * value_noise(i.world.xz * 0.17 + 5.0);
    s.albedo = float3(0.0);
    s.emission = s.color.rgb * m.energy * along * along * across * wet;
}
