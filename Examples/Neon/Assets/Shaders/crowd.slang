import mainframe.spatial;
// The crowd on the pavements (World/NeonCity.cs): a MultiMesh of one kit walker, part 0 the body and part 1 the umbrella.
// Nobody is a node: instanceCustom is (phase, hue, span, speed) and the shader walks each one along its pavement, +Z in
// the instance's space, starting again at the far end of the span metres.

struct Material
{
    float part = 0.0;
};

struct Varyings
{
    nointerpolation float3 custom;
};

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float3 neon(float h)
{
    float k = floor(frac(h) * 6.0);
    float3 c = float3(1.0, 0.12, 0.62);
    c = k > 0.5 ? float3(0.1, 0.85, 1.0) : c;
    c = k > 1.5 ? float3(0.55, 0.22, 1.0) : c;
    c = k > 2.5 ? float3(1.0, 0.55, 0.12) : c;
    c = k > 3.5 ? float3(0.35, 1.0, 0.55) : c;
    c = k > 4.5 ? float3(1.0, 0.18, 0.2) : c;
    return c;
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    float span = v.instanceCustom.z;
    float speed = v.instanceCustom.w;
    float walked = mod(v.time * speed + v.instanceCustom.x * span, span);
    v.vertex.z += walked;
    v.vertex.y += 0.03 * abs(sin(v.time * speed * 4.0 + v.instanceCustom.x * 40.0));
    // Anyone at the end of the span shrinks away rather than pop.
    float edge = smoothstep(0.0, 1.5, walked) * (1.0 - smoothstep(span - 1.5, span, walked));
    v.vertex.xz = lerp(float2(0.0, v.vertex.z), v.vertex.xz, edge);
    o.custom = v.instanceCustom.xyz;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    if (m.part > 0.5)
    {
        // Most umbrellas are black or clear; one in eight glows, a lit shaft in its handle being the fashion.
        float glow = step(0.875, hash11(i.custom.x * 91.0));
        s.albedo = lerp(float3(0.03), float3(0.2), step(0.5, hash11(i.custom.x * 37.0)));
        s.roughness = 0.2;
        s.emission = neon(i.custom.y) * glow * 0.8 + s.albedo * float3(0.5, 0.3, 0.5);
    }
    else
    {
        s.albedo = float3(0.03, 0.03, 0.035);
        s.emission = float3(0.05, 0.02, 0.05);
    }
}
