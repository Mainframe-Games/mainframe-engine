import mainframe.spatial;
// Neon's far skyline (World/NeonCity.cs): open cylinders round the city beyond the real towers, their inside drawn as
// procedural silhouettes with window grids, sinking into the haze. uv.x is metres round the cylinder, uv.y metres above
// the street. Each `slot` metres holds one building: its height, width, setback and antenna come from a hash.

[unshaded, cull(Cull.Disabled), noFog]
struct Material
{
    float seed = 0.0;
    float slot = 26.0;
    float min_height = 40.0;
    float max_height = 260.0;
    float haze = 0.6;
    [color] float4 haze_color = float4(0.28, 0.08, 0.3, 1.0);
    [color] float4 haze_low = float4(0.5, 0.12, 0.38, 1.0);
    float window_energy = 1.2;
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

void fragment(inout SurfaceFragment s, Material m)
{
    float x = s.uv.x / m.slot;
    float id = floor(x) + m.seed * 1000.0;
    float f = frac(x);
    float y = s.uv.y;

    float h1 = hash11(id * 1.37);
    float height = lerp(m.min_height, m.max_height, h1 * h1 * h1);
    float left = 0.04 + 0.22 * hash11(id * 2.11);
    float right = 0.96 - 0.22 * hash11(id * 3.71);
    // A setback: the top third narrower.
    float setback = step(0.5, hash11(id * 5.3)) * step(height * 0.68, y);
    left = lerp(left, lerp(left, right, 0.25), setback);
    right = lerp(right, lerp(left, right, 0.75), setback);
    // A second, lower building in front of the slot's right half.
    float front = step(0.55, hash11(id * 7.9)) * step(y, height * 0.45) * step(0.55, f);
    float inside = step(left, f) * step(f, right) * step(y, height) + front;
    // An antenna on the tall ones.
    float antenna = step(0.7, h1) * step(abs(f - (left + right) * 0.5), 0.012) * step(y, height + 30.0 * h1);
    if (inside + antenna < 0.5)
    {
        discard;
    }

    // Windows: a grid on the building, a lit fraction per building.
    float2 cell = float2(s.uv.x / 2.6, y / 3.5);
    float2 cid = floor(cell);
    float2 cf = frac(cell);
    float window = step(0.2, cf.x) * step(cf.x, 0.8) * step(0.25, cf.y) * step(cf.y, 0.8);
    float litFraction = lerp(0.15, 0.6, hash11(id * 11.3));
    float lit = step(hash12(cid + id), litFraction);
    float2 aa = fwidth(cell);
    float blur = clamp(max(aa.x, aa.y) * 1.5 - 0.3, 0.0, 1.0);
    float glow = lerp(window * lit, litFraction * 0.36, blur);
    float tint = hash12(cid * 0.7 + id);
    float3 colour = tint < 0.6 ? float3(1.0, 0.62, 0.32) : (tint < 0.9 ? float3(0.7, 0.85, 1.0) : float3(1.0, 0.2, 0.7));
    // A neon crown on some.
    float crown = step(0.6, hash11(id * 13.1)) * step(abs(y - height + 1.5), 0.8) * inside;
    float3 crownColour = hash11(id * 17.0) < 0.5 ? float3(1.0, 0.15, 0.6) : float3(0.1, 0.8, 1.0);

    // Haze: thicker towards the street, where the city's light fills it.
    float low = exp(-y / 70.0);
    float amount = clamp(m.haze + (1.0 - m.haze) * low * 0.85, 0.0, 1.0);
    float3 hazeColour = lerp(m.haze_color.rgb, m.haze_low.rgb, low);
    float3 silhouette = float3(0.006, 0.006, 0.012);
    s.albedo = lerp(silhouette, hazeColour, amount);
    s.emission = (colour * glow * m.window_energy * inside + crownColour * crown * 3.0 + float3(1.0, 0.05, 0.02) * antenna * step(height + 28.0 * h1, y) * 6.0) * (1.0 - amount * 0.8);
}
