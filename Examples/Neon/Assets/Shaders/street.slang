import mainframe.spatial;
// Neon's streets and pavements (World/NeonCity.cs): wet asphalt in the rain. The street meshes carry uv = (metres along the
// street, metres across from its centre line) and color = (half width / 64, kind, length / 1024, 1): kind 0 a street
// between two crossings, 1 a crossing (its uv runs along X, across Z, from its centre), 2 the pavement.
//
// Nothing here can really mirror the city, so city_mirror() traces the reflected ray to an imaginary wall of shops and
// windows the far side of the street: a row of shopfronts low down, lit windows above, the sky's glow over them. The
// roughness of the wet surface smears it along the view, as it does on a real road at night.

struct Material
{
    float marking_energy = 0.55;
    // The menu: how much of the city the wet streets mirror.
    float reflections = 1.0;
};

struct Varyings
{
    float3 world;
};

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

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

float stripe(float v, float centre, float half_width, float aa)
{
    return 1.0 - smoothstep(half_width - aa, half_width + aa, abs(v - centre));
}

// The reflected ray from `p` meets a wall `dist` metres off: what is on it there? Only soft shapes: a road never mirrors
// a window sharply, it smears each light into a streak along the view.
float3 city_mirror(float3 p, float3 r, float dist)
{
    float flat_ = max(length(r.xz), 1e-3);
    float y = dist / flat_ * r.y;
    float2 dir = r.xz / flat_;
    float along = dot(p.xz, float2(-dir.y, dir.x));
    float a = along * 0.11;
    float blend = frac(a);
    blend = blend * blend * (3.0 - 2.0 * blend);
    float id = floor(a);
    float3 shop0 = lerp(float3(1.0, 0.72, 0.45), neon(hash11(id * 0.37)), 0.55) * step(0.25, hash11(id * 0.71));
    float3 shop1 = lerp(float3(1.0, 0.72, 0.45), neon(hash11((id + 1.0) * 0.37)), 0.55) * step(0.25, hash11((id + 1.0) * 0.71));
    float3 c = lerp(shop0, shop1, blend) * exp(-y / 7.0) * 1.3;
    float lit = value_noise(float2(along * 0.28, y * 0.02));
    c += float3(1.0, 0.72, 0.5) * smoothstep(0.35, 0.8, lit) * smoothstep(5.0, 12.0, y) * exp(-y / 80.0) * 0.55;
    c += float3(0.3, 0.075, 0.27) * smoothstep(40.0, 200.0, y);
    return c;
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    o.world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float hw = s.color.r * 64.0;
    float kind = s.color.g;
    float2 uv = s.uv;
    float2 aa = fwidth(uv) + 1e-4;
    float2 p = i.world.xz;
    // How far off this is, for what must fade before it aliases: 1 m features at a few pixels (the street is always far).
    float2 metre = fwidth(p) + 1e-4;
    float far = clamp(max(max(aa.x, aa.y), max(metre.x, metre.y)) * 12.0 - 0.6, 0.0, 1.0);

    // Wetness: damp everywhere, standing water in the dips and along the ruts.
    float broad = value_noise(p * 0.07);
    float fine = value_noise(p * 0.31);
    float puddle = smoothstep(0.38, 0.8, broad * 0.75 + fine * 0.25);
    float grit = lerp(value_noise(p * 1.4), 0.5, far);

    float3 albedo = lerp(float3(0.02, 0.02, 0.024), float3(0.027, 0.027, 0.031), grit);
    float wall = 9.0;
    float marks = 0.0;
    float3 markColour = float3(0.0);

    if (kind < 0.5)
    {
        float v = uv.y;
        wall = hw + 8.0;
        // Patched asphalt: darker rectangles, and tyres polish the wheel lines.
        float2 patchCell = floor(float2(uv.x / 11.0, (v + 64.0) / 3.2));
        float patched = step(0.72, hash12(patchCell)) * 0.6;
        albedo *= 1.0 - 0.45 * patched;
        float lanes = max(1.0, floor(hw / 3.4));
        float lanePos = abs(frac(abs(v) / (hw / lanes)) - 0.5);
        float ruts = 1.0 - smoothstep(0.0, 0.12, abs(lanePos - 0.28));
        puddle = max(puddle, ruts * 0.55 * smoothstep(0.35, 0.7, broad));
        // The paint, worn away by the traffic.
        float worn = smoothstep(0.25, 0.6, value_noise(p * float2(1.3, 1.3)));
        if (hw > 9.0)
        {
            float centre = stripe(v, 0.22, 0.08, aa.y) + stripe(v, -0.22, 0.08, aa.y);
            marks += centre;
            markColour += float3(1.0, 0.62, 0.14) * centre;
        }
        else
        {
            float dash = step(frac(uv.x / 9.0), 0.5);
            marks += stripe(v, 0.0, 0.08, aa.y) * dash;
            markColour += float3(0.9) * stripe(v, 0.0, 0.08, aa.y) * dash;
        }
        for (int lane = 1; lane < 4; lane++)
        {
            float c = float(lane) * hw / lanes;
            float on = step(float(lane), lanes - 0.5) * step(frac(uv.x / 9.0 + 0.25), 0.4);
            float l = (stripe(v, c, 0.07, aa.y) + stripe(v, -c, 0.07, aa.y)) * on;
            marks += l;
            markColour += float3(0.85) * l;
        }
        // Zebra crossings at both ends of the block, and the stop line.
        float len = s.color.b * 1024.0;
        float end = min(uv.x, len - uv.x);
        float zebra = step(1.0, end) * step(end, 4.5) * step(0.5, frac(v / 1.2)) * step(abs(v), hw - 0.6);
        marks += zebra;
        markColour += float3(0.8) * zebra;
        marks *= worn;
        markColour *= worn;
        // The gutter: the kerb's shadow and the water that runs along it.
        float gutter = smoothstep(hw - 1.1, hw - 0.1, abs(v));
        albedo *= 1.0 - 0.4 * gutter;
        puddle = max(puddle, gutter * 0.85);
        float kerb = smoothstep(hw - 0.22, hw - 0.12, abs(v));
        albedo = lerp(albedo, float3(0.16, 0.155, 0.15), kerb);
        puddle *= 1.0 - kerb;
    }
    else if (kind < 1.5)
    {
        // A crossing: plain asphalt, a little more worn.
        wall = 18.0;
        albedo *= 1.15;
    }
    else
    {
        // Pavement: slabs, some cracked or darker, wet joints.
        float2 tile = p / float2(1.6, 0.8);
        tile.x += step(0.5, frac(tile.y * 0.5)) * 0.5;
        float2 tileId = floor(tile);
        float2 tf = frac(tile);
        // The slabs go before they alias: from a few pixels each the pavement is one tone.
        float slabs = clamp(1.4 - max(fwidth(tile).x, fwidth(tile).y) * 7.0, 0.0, 1.0);
        float joint = (1.0 - step(0.035, tf.x) * step(0.07, tf.y)) * slabs;
        float shade = lerp(1.0, 0.75 + 0.5 * hash12(tileId), slabs);
        albedo = lerp(float3(0.15, 0.14, 0.15) * shade, float3(0.05), joint);
        puddle = max(puddle * 0.35, joint * 0.4);
        wall = 7.0;
    }

    marks = clamp(marks * (1.0 - far), 0.0, 1.0);
    albedo = lerp(albedo, float3(0.5), marks * 0.55);
    puddle = clamp(puddle, 0.0, 1.0);

    float3 V = normalize(s.cameraPositionWorld - i.world);
    // The surface's ripples and grain tilt the mirror, more on the damp road than in standing water.
    float rough = lerp(0.05, 0.012, puddle);
    float2 ripple = lerp(p * 1.7, p * 0.23, far);
    float2 tilt = float2(value_noise(ripple + s.time * 0.15) - 0.5, value_noise(ripple + 41.0 - s.time * 0.12) - 0.5) * rough * (1.0 - far * 0.7);
    float3 n = normalize(float3(tilt.x, 1.0, tilt.y));
    float3 R = reflect(-V, n);
    R.y = abs(R.y);
    float fresnel = 0.03 + 0.97 * pow(1.0 - max(dot(V, n), 0.0), 5.0);
    float3 mirror = city_mirror(i.world, normalize(R), wall);
    float wet = lerp(0.55, 1.0, puddle);
    float3 emission = mirror * (fresnel * 1.5 + 0.012) * wet * m.reflections;

    // The light that reaches the ground anyway: the sky's glow, and the street's colours from above.
    float3 ambient = float3(0.16, 0.08, 0.17);
    emission += albedo * ambient * 2.4;
    emission += markColour * m.marking_energy * (1.0 - far) * lerp(0.8, 0.35, puddle);

    s.albedo = albedo * (1.0 - 0.35 * puddle);
    s.roughness = lerp(0.4, 0.12, puddle);
    s.metallic = 0.0;
    s.specular = 0.6;
    s.emission = emission;
}
