// GpuParticles3D draw passes (ParticleRenderer, ADR 0206): set 0 frame + lights + sky lighting, set 1 shadows, set 2 the
// draw pass material's sampler, albedo and emission images (white 1x1 when a slot is empty); its parameters are push
// constants (particle_common.slang). Albedo × texture × (with VertexColorUseAsAlbedo) the particle colour, unshaded or
// lit (diffuse, no highlight), plus emission (HDR: the glow picks it up), fogged like other geometry: mixed particles fade
// into the fog, additive ones fade out.
#include "particle_common.slang"
#include "shadows.slang"
#include "lights.slang"
#include "fog.slang"

[[vk::binding(0, 2)]] SamplerState particleSampler;
[[vk::binding(1, 2)]] Texture2D albedoTexture;
[[vk::binding(2, 2)]] Texture2D emissionTexture;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor; // linear RGBA particle colour
};

// How much fog covers worldPos (0..1): applyFog's amount (fog.slang), for additive particles that fade out instead.
float fogAmount(float3 worldPos)
{
    if (frame.fogColor.a == 0.0)
        return 0.0;
    float3 toPoint = worldPos - frame.cameraPosition.xyz;
    float dist = length(toPoint);
    float start = fogStartDistance();
    if (dist < 1e-4 || dist <= start)
        return 0.0;
    float density = frame.fogParams.x;
    float height = frame.fogParams.y;
    float heightDensity = frame.fogParams.z;
    float y0 = frame.cameraPosition.y + toPoint.y * (start / dist);
    float segment = dist - start;
    float dy = worldPos.y - y0;
    float depth;
    if (abs(dy) > 1e-3)
        depth = segment * (fogHeightIntegral(worldPos.y, density, height, heightDensity) -
                           fogHeightIntegral(y0, density, height, heightDensity)) / dy;
    else
        depth = segment * density * exp(-heightDensity * max(y0 - height, 0.0));
    return 1.0 - exp(-max(depth, 0.0));
}

[shader("fragment")]
float4 main(FsIn input) : SV_Target0
{
    float2 uv = input.inUV * pc.uvTransform.xy + pc.uvTransform.zw;
    float2 duvdx = materialGrad(ddx(uv));
    float2 duvdy = materialGrad(ddy(uv));
    uint bits = pc.flags.x;
    float4 albedo = pc.albedo;
    if ((bits & kParticleHasAlbedo) != 0u)
        albedo *= albedoTexture.SampleGrad(particleSampler, uv, duvdx, duvdy);
    if ((bits & kParticleVertexColor) != 0u)
        albedo *= input.inColor;
    uint alphaMode = pc.flags.z;
    if (alphaMode == 1u && albedo.a < pc.emission.w)
        discard;

    float3 color;
    if (pc.flags.y == 1u)
    {
        color = albedo.rgb;
    }
    else
    {
        // Lit: diffuse + ambient on the particle's normal, turned toward the viewer (both faces of a billboard).
        float3 N = normalize(input.inNormal);
        if (dot(N, frame.cameraPosition.xyz - input.inWorldPos) < 0.0)
            N = -N;
        color = shadeLightsBlinnPhong(albedo.rgb, N, N, input.inWorldPos, 0.0, 1.0);
    }

    float3 emission = pc.emission.rgb;
    if ((bits & kParticleHasEmission) != 0u)
        emission *= emissionTexture.SampleGrad(particleSampler, uv, duvdx, duvdy).rgb;
    color += emission;

    if (pc.flags.w == 1u)
        color *= 1.0 - fogAmount(input.inWorldPos); // additive: the fog hides it
    else
        color = applyFog(color, input.inWorldPos);
    return float4(color, alphaMode == 2u ? albedo.a : 1.0);
}
