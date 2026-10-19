// Screen-space contact shadows (ADR 0167, G8e.2), an AfterPrepass post effect: from each pixel of the prepass depth, a
// short march towards the primary light in view space (pc.light.w metres in pc.params.y steps, the start jittered by
// interleaved gradient noise with TAA) finds an occluder when a step lies behind the depth stored at its pixel by more than a
// small bias and less than the thickness (pc.params.x). Writes the screen-space occlusion image the lit shaders read at
// set 0 binding 5 (ADR 0170, include/ambient_occlusion.slang): r = SSAO's AO and a = its packed bent normal, copied from
// SSAO's output when SSAO ran this frame (pc.flags.x), else 1 and none; g = the contact shadow (1 = lit), b = the pixel's
// view depth, which the lit shaders compare with their own (include/contact_shadows.slang). Full resolution, one pass.
[[vk::binding(0, 0)]] Sampler2D<float> sceneDepth;
[[vk::binding(1, 0)]] Sampler2D ssaoSource; // SSAO's full-resolution output (unread without SSAO)

struct Contact
{
    float4 projection; // x = M11, y = M22, z = M31, w = M32 of the jittered projection (row-vector convention)
    float4 depth;      // x = M33, y = M43, z = width, w = height (pixels)
    float4 light;      // xyz = towards the light in view space (normalized), w = march length (m)
    float4 params;     // x = thickness (m), y = steps (0: no march, contact shadows only copy SSAO), z = noise frame offset (< 0: no noise), w = distance where contact shadows end (m)
    float4 flags;      // x = 1: copy SSAO's r and a from ssaoSource
};

[[vk::push_constant]] ConstantBuffer<Contact> pc;

// View-space position of a depth-buffer value at a screen UV (0,0 top-left; NDC +Y up lands on row 0).
float3 viewPosition(float2 uv, float d)
{
    float z = -pc.depth.y / (d + pc.depth.x);
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float x = (ndc.x * -z - z * pc.projection.z) / pc.projection.x;
    float y = (ndc.y * -z - z * pc.projection.w) / pc.projection.y;
    return float3(x, y, z);
}

// Screen UV of a view-space point.
float2 screenUv(float3 p)
{
    float w = -p.z;
    float2 ndc = float2(p.x * pc.projection.x + p.z * pc.projection.z, p.y * pc.projection.y + p.z * pc.projection.w) / w;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
}

float linearDepth(float d)
{
    return pc.depth.y / (d + pc.depth.x);
}

float interleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// The contact shadow at this pixel (1 = lit) and the view depth it belongs to.
float2 contact(float4 fragCoord)
{
    float2 size = pc.depth.zw;
    int2   pixel = int2(fragCoord.xy);
    float  d = sceneDepth.Load(int3(pixel, 0));
    if (d >= 1.0)
        return float2(1.0, 65000.0); // the sky: nothing to shadow

    float2 uv = fragCoord.xy / size;
    float3 p0 = viewPosition(uv, d);
    float  viewDepth = -p0.z;
    float  maxDistance = pc.params.w;
    if (viewDepth >= maxDistance || pc.params.y < 1.0)
        return float2(1.0, viewDepth);

    int    steps = int(pc.params.y);
    float  stepLength = pc.light.w / float(steps);
    // With TAA, the start is jittered per pixel and frame (TAA averages it); without, every ray starts half a step out
    // (fixed per-pixel noise would leave a visible dither on the shadows' ends).
    float  noise = pc.params.z < 0.0 ? 0.5 : interleavedGradientNoise(fragCoord.xy + 5.588238 * pc.params.z);
    float  thickness = pc.params.x + 0.01 * viewDepth;
    float  bias = 0.01 + 0.002 * viewDepth; // keeps a surface from shadowing itself at grazing angles

    float occluded = 0.0;
    for (int i = 0; i < steps; i++)
    {
        float3 p = p0 + pc.light.xyz * (stepLength * (float(i) + noise));
        if (p.z >= -1e-3)
            break; // the ray left the view (behind the near plane)
        float2 q = screenUv(p);
        if (any(q < float2(0.0)) || any(q > float2(1.0)))
            break;
        float stored = linearDepth(sceneDepth.Load(int3(int2(q * size), 0)));
        float behind = -p.z - stored;
        if (behind > bias && behind < thickness)
        {
            occluded = 1.0;
            break;
        }
    }

    // Fade out towards the end distance (the cascades take over) and near the screen's edges.
    float2 edge = min(uv, 1.0 - uv);
    float fade = (1.0 - smoothstep(maxDistance * 0.7, maxDistance, viewDepth)) * smoothstep(0.0, 0.02, min(edge.x, edge.y));
    return float2(1.0 - occluded * fade, viewDepth);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 shadow = contact(fragCoord);
    float4 ssao = pc.flags.x > 0.5 ? ssaoSource.Load(int3(int2(fragCoord.xy), 0)) : float4(1.0, 1.0, 1.0, 0.0);
    return float4(ssao.r, shadow.x, shadow.y, ssao.a);
}
