// WaterMaterial3D (ADR 0159), the fallback look of docs/design/future/water.md (no SceneTextures): set 0 frame + lights
// + sky lighting, set 1 shadows, set 2 material (the StandardMaterial3D layout: the parameters per MaterialParams.From
// (WaterMaterial3D) below, the foam mask in the albedo slot, the ripple normals in the normal slot). Alpha-blended over
// what lies behind (straight alpha): the output colour × alpha is the light the water adds, 1 − alpha the share of the
// background that shows through. Linear HDR output.
#include "water.slang"
#include "fog.slang"

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    float3 P = input.inWorldPos;
    float2 xz = P.xz;
    float2 dx = materialGrad(ddx(xz)); // texture gradients (ADR 0174: mip bias under upscaling)
    float2 dy = materialGrad(ddy(xz));
    float column = input.inWater.x;

    WaterFlow f = waterFlow(input.inWater.yz, xz);
    float3 Ng = normalize(input.inNormal);
    float3 N = waterSurfaceNormal(Ng, xz, dx, dy, f);

    float3 V = normalize(frame.cameraPosition.xyz - P);
    float NdotV = max(dot(N, V), 1e-4);
    float roughness = clamp(waterRoughness(), 0.02, 1.0);

    // Fresnel (Schlick, F0 = 0.02) and the sky along the reflected ray x the split-sum BRDF.
    float F = kWaterF0 + (1.0 - kWaterF0) * pow(1.0 - NdotV, 5.0);
    float3 R = reflect(-V, N);
    R.y = abs(R.y); // ripples never reflect the ground below the horizon
    float2 brdf = iblBrdf(NdotV, roughness);
    float3 reflection = iblSpecular(R, roughness) * (kWaterF0 * brdf.x + brdf.y) * waterReflectionStrength();
    float sunShadow;
    float3 glints = waterLights(N, Ng, V, P, roughness, sunShadow);

    // The water body: Beer-Lambert over the column along the view ray, light scattered in where light is absorbed. The
    // screen-space AO here (ADR 0165) is the bed's (water is not in the depth prepass): it darkens only the sky light
    // scattered in the water body, not the surface's reflections and glints.
    float scatterAo = ambientOcclusionAt(fragCoord);
    float cosView = max(dot(Ng, V), 0.1);
    float3 transmittance = exp(-waterAbsorption() * (column / cosView));
    float3 scatter = waterScatterColor() * waterScatterStrength() * waterDiffuseLight(float3(0.0, 1.0, 0.0), sunShadow, scatterAo) *
                     (1.0 - transmittance);
    float through = (transmittance.r + transmittance.g + transmittance.b) / 3.0;

    // Premultiplied: what the water adds, and how much of the background still shows.
    float3 added = scatter * (1.0 - F) + reflection + glints;
    float keep = through * (1.0 - F);

    // Foam, lit as a rough white surface.
    float foam = waterFoam(column, input.inUV, input.inWater, xz, dx, dy, f);
    float3 foamColor = 0.85 * waterDiffuseLight(Ng, sunShadow, 1.0);
    added = lerp(added, foamColor, foam);
    keep *= 1.0 - foam;

    // Soft edges: the surface fades in over the first SoftEdgeDistance of column.
    float softEdge = waterSoftEdge();
    float edge = softEdge > 0.0 ? smoothstep(0.0, softEdge, column) : 1.0;
    added *= edge;
    keep = lerp(1.0, keep, edge);

    float alpha = 1.0 - keep;
    if (alpha <= 1e-4)
        return float4(0.0);
    float3 color = applyFog(added / alpha, P);
    return float4(color, alpha);
}
