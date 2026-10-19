// SprayCards3D / SprayMaterial3D (ADR 0173): a soft card of mist. Its alpha is two layers of the noise texture drifting
// over the card, in a round falloff, × the card's fade over its cycle, faded out where it meets the scene behind it (the
// view's scene copy, set 3, when the view has one: waterScene.refraction.w) and near the camera. Lit by the sky and the
// sun (shadowed), brighter looking towards the sun. Straight alpha, blended (AlphaReactive in the main view).
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "fog.slang"
#include "water_scene.slang"

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float2 inUV;
    [[vk::location(2)]] float4 inCard; // x = fade, y = phase, z = cycle time, w = view depth
};

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    float3 P = input.inWorldPos;
    float2 uv = input.inUV;
    float phase = input.inCard.y;
    float t = input.inCard.z;

    float2 p = uv * 2.0 - 1.0;
    float radial = 1.0 - smoothstep(0.25, 1.0, length(p));
    float2 drift = float2(phase * 7.31, phase * 3.17);
    // Two layers of the noise drifting over the card.
    float a = albedoTexture.SampleBias(materialSampler, uv * 0.45 + drift + float2(0.0, t * 0.35), frame.mipBias).r; // ADR 0174
    float b = albedoTexture.SampleBias(materialSampler, uv * 0.9 + drift.yx - float2(t * 0.2, 0.0), frame.mipBias).r;
    float density = saturate(a * 1.3 * (0.4 + b) - 0.1) * radial * input.inCard.x * material.albedo.a;

    // Soft against the scene behind (the copy holds the opaques' linear depth) and near the camera.
    float depth = input.inCard.w;
    if (waterScene.refraction.w > 0.0)
    {
        float sceneZ = sceneDepthAt(int2(fragCoord.xy), 0);
        density *= saturate((sceneZ - depth) / max(material.uvTransform.w, 1e-3));
    }
    density *= saturate((depth - frame.clip.x * 2.0) / 0.75);
    if (density <= 1e-4)
        return float4(0.0);

    float3 V = normalize(frame.cameraPosition.xyz - P);
    float3 light = iblDiffuse(V);
    if (lights.counts.x > 0)
    {
        DirLight sun = lights.dir[0];
        float3 L = normalize(-sun.directionIntensity.xyz);
        float shadow = lights.counts.w == 0 ? lerp(1.0, dirShadow(0, P, L, L), sun.color.w) : 1.0;
        float forward = pow(saturate(dot(-V, L)), 6.0) * 1.5;
        light += sun.color.xyz * sun.directionIntensity.w * (0.3 + forward) * shadow;
    }

    float3 color = applyFog(material.albedo.rgb * light, P);
    return float4(color, density);
}
