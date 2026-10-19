// Volumetric fog (ADR 0171), pass 3 of 3: the composite at full resolution, into a copy of the HDR scene colour. The four
// half-resolution texels around the pixel (each standing for the full-resolution pixel at twice its coordinates) are
// weighted bilinearly × by how close their view depth is to the pixel's (a depth-aware upsample: fog does not bleed
// across silhouettes); when none is close, the closest one is used. Then colour × transmittance + in-scattered light;
// the sky takes pc.params.x of it (Godot's volumetric_fog_sky_affect). The scene's alpha (TAA's reactive mask) is kept.
[[vk::binding(0, 0)]] Sampler2D hdrScene;           // point
[[vk::binding(1, 0)]] Sampler2D fog;                // half resolution, point (rgb in-scatter, a transmittance)
[[vk::binding(2, 0)]] Sampler2D<float> sceneDepth;  // point

struct Composite
{
    float4 depth;  // M33, M43, M34, M44 of the projection the scene was drawn with
    float4 params; // x = sky affect, yz = the fog image's size
};

[[vk::push_constant]] ConstantBuffer<Composite> pc;

// View depth (positive, metres) of a depth-buffer value: perspective or orthographic.
float viewDepth(float d)
{
    return (pc.depth.y - d * pc.depth.w) / (pc.depth.x - d * pc.depth.z);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 pixel = int2(fragCoord.xy);
    float4 scene = hdrScene.Load(int3(pixel, 0));
    float d = sceneDepth.Load(int3(pixel, 0));
    float z = viewDepth(d);
    bool sky = d >= 1.0;

    int2 fogSize = int2(pc.params.yz);
    float2 h = fragCoord.xy * 0.5 - 0.5; // this pixel in half-resolution texel coordinates
    int2 base = int2(floor(h));
    float2 f = h - float2(base);
    float tolerance = 0.05 * z + 0.05;
    float4 sum = float4(0.0);
    float weights = 0.0;
    float4 closest = float4(0.0, 0.0, 0.0, 1.0);
    float closestDelta = 1e30;
    for (int i = 0; i < 4; i++)
    {
        int2 o = int2(i & 1, i >> 1);
        int2 t = clamp(base + o, int2(0), fogSize - int2(1));
        float td = sceneDepth.Load(int3(t * 2, 0));
        float4 s = fog.Load(int3(t, 0));
        // Sky against sky matches; sky against a surface never does.
        float delta = (td >= 1.0) == sky ? (sky ? 0.0 : abs(viewDepth(td) - z)) : 1e20;
        float bilinear = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
        float w = bilinear * max(1.0 - delta / tolerance, 0.0);
        sum += s * w;
        weights += w;
        if (delta < closestDelta)
        {
            closestDelta = delta;
            closest = s;
        }
    }

    float4 v = weights > 1e-4 ? sum / weights : closest;
    float affect = sky ? pc.params.x : 1.0;
    float transmittance = lerp(1.0, v.a, affect);
    return float4(scene.rgb * transmittance + v.rgb * affect, scene.a);
}
