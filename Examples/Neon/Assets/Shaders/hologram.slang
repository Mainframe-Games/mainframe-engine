import mainframe.spatial;
// Neon's giant holograms (World/NeonCity.cs): additive light with scanlines, a rim, flicker and the odd glitch. `shape`:
// 0 a koi (the kit's Holo_Koi, 1 m long: it swims a circle of `orbit` local metres round the node and wiggles), 1 an
// emblem (Holo_Logo: it turns about +Y), 2 a dancer (a unit quad, uv 0..1: a figure drawn as distance-field capsules).

[unshaded, blend(Blend.Add), cull(Cull.Disabled), depthDraw(DepthDraw.Never), noFog]
struct Material
{
    float shape = 0.0;
    [color] float4 color = float4(0.1, 0.9, 1.0, 1.0);
    [color] float4 color2 = float4(1.0, 0.25, 0.7, 1.0);
    float energy = 3.0;
    float orbit = 1.2;
    float speed = 0.12;
    float phase = 0.0;
    float line_spacing = 0.6;
};

struct Varyings
{
    float3 world;
    float3 local;
};

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float capsule(float2 p, float2 a, float2 b, float r)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h) - r;
}

float2 limb(float2 from, float angle, float len)
{
    return from + float2(sin(angle), -cos(angle)) * len;
}

// The dancer: a figure of capsules, its joints swinging to a beat.
float dancer(float2 p, float t)
{
    float beat = t * 2.2;
    float2 hip = float2(0.5 + 0.03 * sin(beat), 0.42 + 0.015 * abs(sin(beat)));
    float2 neck = hip + float2(0.02 * sin(beat + 0.5), 0.22);
    float d = capsule(p, hip, neck, 0.035);
    d = min(d, length(p - (neck + float2(0.01 * sin(beat), 0.07))) - 0.05);
    // arms: shoulder -> elbow -> hand, mirrored and out of phase
    for (int s = 0; s < 2; s++)
    {
        float side = s == 0 ? -1.0 : 1.0;
        float2 shoulder = neck + float2(side * 0.06, -0.02);
        float a1 = side * (1.9 + 0.9 * sin(beat + side * 1.2));
        float2 elbow = limb(shoulder, a1, 0.12);
        float2 hand = limb(elbow, a1 + side * (0.6 + 0.7 * sin(beat * 2.0 + side)), 0.11);
        d = min(d, capsule(p, shoulder, elbow, 0.022));
        d = min(d, capsule(p, elbow, hand, 0.018));
        // legs: hip -> knee -> foot
        float2 h2 = hip + float2(side * 0.04, 0.0);
        float a2 = side * (0.15 + 0.25 * max(0.0, sin(beat + (side > 0.0 ? 0.0 : 3.14))));
        float2 knee = limb(h2, a2, 0.17);
        float2 foot = limb(knee, a2 * 0.3 - side * 0.05, 0.17);
        d = min(d, capsule(p, h2, knee, 0.03));
        d = min(d, capsule(p, knee, foot, 0.024));
    }
    return d;
}

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    float3 p = v.vertex;
    float3 n = v.normal;
    if (m.shape < 0.5)
    {
        // Swim: a wave down the body, stronger towards the tail (+Z); then round the circle, head first.
        float tail = clamp(p.z + 0.5, 0.0, 1.0);
        p.x += sin(p.z * 7.0 - v.time * 3.0) * 0.07 * tail * tail;
        float a = v.time * m.speed + m.phase;
        p += float3(m.orbit, 0.12 * sin(v.time * 0.4), 0.0);
        float c = cos(a);
        float s = sin(a);
        p = float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
        n = float3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z);
    }
    else if (m.shape < 1.5)
    {
        float a = v.time * m.speed;
        float c = cos(a);
        float s = sin(a);
        p = float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
        n = float3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z);
    }
    v.vertex = p;
    v.normal = n;
    o.local = v.vertex;
    o.world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    // Glitch: now and then the hologram jumps and dims for a few frames.
    float tick = floor(s.time * 7.0);
    float glitch = step(0.94, hash11(tick + m.shape * 13.0));
    float flicker = (0.88 + 0.12 * sin(s.time * 41.0)) * (1.0 - 0.6 * glitch);
    float scan = 0.55 + 0.45 * sin((i.world.y + glitch * 0.7) / m.line_spacing * 6.2832 - s.time * 3.0);
    float3 col = m.color.rgb;
    float alpha = 1.0;

    if (m.shape > 1.5)
    {
        float2 p = float2(s.uv.x, 1.0 - s.uv.y);
        p.x += glitch * 0.02 * sin(p.y * 80.0);
        float d = dancer(p, s.time);
        float body = 1.0 - smoothstep(0.0, 0.006, d);
        float halo = exp(-max(d, 0.0) * 40.0) * 0.35;
        col = lerp(m.color2.rgb, m.color.rgb, p.y);
        alpha = body * 0.6 + halo + (1.0 - smoothstep(0.004, 0.012, abs(d))) * 0.8;
        // a ring of light on the roof under the dancer
        alpha += (1.0 - smoothstep(0.0, 0.01, abs(length((p - float2(0.5, 0.06)) * float2(1.0, 6.0)) - 0.18))) * 0.6;
    }
    else
    {
        float rim = pow(1.0 - abs(dot(normalize(s.normal), normalize(s.view))), 2.0);
        if (m.shape < 0.5)
        {
            // koi markings: patches of the second colour
            float spots = step(0.55, sin(i.local.z * 23.0 + 1.3) * sin(i.local.x * 31.0 + i.local.y * 17.0));
            col = lerp(m.color.rgb, m.color2.rgb, spots);
        }
        alpha = 0.25 + rim * 1.4;
    }
    s.albedo = float3(0.0);
    s.emission = col * m.energy * alpha * scan * flicker;
}
