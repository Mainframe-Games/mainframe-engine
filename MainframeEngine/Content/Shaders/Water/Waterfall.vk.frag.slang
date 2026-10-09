// WaterfallMaterial3D (ADR 0173): a fall's sheet. Paired with Water.vk.vert, whose outputs it reads as River3D's jet
// writes them: inWater.x = how far down the fall (0 lip … 1 base), length(inWater.yz) = the sheet's speed in UV units per
// second, inWater.w = aeration; UV.u across the sheet, UV.v along it. Sets 0-2 as Water.vk.frag (the streak texture in
// the albedo slot, the built-in ripples in the normal slot). Straight alpha, blended (AlphaReactive in the main view).
#include "water.slang"
#include "fog.slang"

float3 fallFoamColor() { return material.albedo.rgb; }   // linear
float  fallOpacity() { return material.albedo.a; }
float3 fallWaterColor() { return material.emission.rgb; } // linear
float  fallAeration() { return material.emission.a; }
float  fallStreakScale() { return material.uvTransform.x; }
float  fallStreakLength() { return material.uvTransform.y; }
float  fallFlowScale() { return material.uvTransform.z; }
float  fallRoughness() { return material.uvTransform.w; }

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    float3 P = input.inWorldPos;
    float2 uv = input.inUV;
    float down = saturate(input.inWater.x);
    float speed = length(input.inWater.yz) * fallFlowScale();
    float aeration = saturate(input.inWater.w * fallAeration());

    // Streaks: the texture stretched along the sheet, two layers falling at different speeds.
    float time = frame.clip.z;
    float scale = fallStreakScale();
    float along = scale / fallStreakLength();
    float2 s0 = float2(uv.x * scale, (uv.y - time * speed) * along);
    float2 s1 = float2(uv.x * scale * 1.7 + 0.37, (uv.y - time * speed * 1.35) * along * 1.7);
    float a = albedoTexture.Sample(materialSampler, s0).r;
    float b = albedoTexture.Sample(materialSampler, s1).r;
    float streak = saturate(a * 0.7 + b * 0.5);

    // The sheet whitens as it falls (more where the streaks are), thins out and breaks up at its edges.
    float white = saturate(aeration * (0.45 + 0.9 * streak) + down * 0.25 * streak);
    float edge = smoothstep(0.0, 0.14, uv.x) * (1.0 - smoothstep(0.86, 1.0, uv.x));
    edge *= saturate(0.55 + streak + white);
    float alpha = saturate(lerp(fallOpacity() * (0.65 + 0.35 * streak), 1.0, white)) * edge;

    // Two-sided lighting: sky and sun (shadowed), the sun glowing through the sheet, glints on its clear water.
    float3 Ng = normalize(input.inNormal);
    float3 V = normalize(frame.cameraPosition.xyz - P);
    if (dot(Ng, V) < 0.0)
        Ng = -Ng;
    float sunShadow;
    float3 glints = waterLights(Ng, Ng, V, P, clamp(fallRoughness(), 0.02, 1.0), sunShadow);
    float3 light = waterDiffuseLight(Ng, sunShadow, 1.0);
    float3 glow = float3(0.0);
    if (lights.counts.x > 0)
    {
        DirLight sun = lights.dir[0];
        float3 L = normalize(-sun.directionIntensity.xyz);
        glow = sun.color.xyz * sun.directionIntensity.w * pow(saturate(dot(-V, L)), 4.0) * sunShadow * 0.5;
    }

    float3 water = fallWaterColor() * (light * 0.7 + glow) + glints * (1.0 - white);
    float3 foam = fallFoamColor() * (light * 0.85 + glow);
    float3 color = applyFog(lerp(water, foam, white), P);
    return float4(color, alpha);
}
