// Refracting WaterMaterial3D (ADR 0173, WaterMaterial3D.RefractionEnabled): the look of Water.vk.frag composed with
// what lies behind the surface, from the view's scene copy (set 3, include/water_scene.slang). Drawn after the opaque
// half of the scene pass with BlendMode.ColorOnlyReactive (main view: the colour replaces the pixel, the alpha marks
// TAA's reactivity) or ColorOnly (other views). Sets 0-2 as Water.vk.frag (the caustic pattern in the emission slot).
//  1. The column: the copy's depth at the pixel gives the bed straight behind the surface (foam rings and soft edges
//     from the true column, so rocks breaking the surface get foam).
//  2. Refraction: the view ray bent at the rippled surface (Snell, IOR 1.33, scaled by the strength) reaches the bed
//     after the straight path's length; its pixel is read from the copy, unless what is there lies in front of the
//     surface (a foreground object), in which case the unbent pixel is kept. Blurrier levels for deeper water.
//  3. Caustics on the bed: a generated pattern (two scrolled layers, min blend) projected along the sun onto the bed's
//     world XZ, × the bed's sun shadow, × the sunlight left after its path through the water, faded out with depth.
//  4. Beer-Lambert absorption per channel over the refracted path length, in-scattering where light is absorbed.
//  5. Reflection: screen-space against the copy's half-resolution level, falling back to the sky's radiance by the
//     reflection's confidence; Schlick Fresnel; GGX glints of every light as before.
//  6. Foam, soft edges (towards the unbent copy), fog.
#include "water.slang"
#include "water_scene.slang"
#include "fog.slang"

static const float kWaterEta = 1.0 / 1.33;

float waterCausticsInvScale() { return waterScene.caustics.x; }
float waterCausticsStrength() { return waterScene.caustics.y; }
float waterCausticsMaxDepth() { return waterScene.caustics.z; }

// The bed's caustics: a factor on the sunlit bed's colour (1 = none).
float3 waterCaustics(float3 bed, float depthBelow, float3 normalOffset)
{
    if (waterCausticsStrength() <= 0.0 || lights.counts.x == 0 || depthBelow <= 0.0)
        return float3(1.0);
    DirLight sun = lights.dir[0];
    float3 L = normalize(-sun.directionIntensity.xyz); // towards the sun
    if (L.y <= 0.05)
        return float3(1.0);
    float fade = (1.0 - smoothstep(0.6, 1.0, depthBelow / max(waterCausticsMaxDepth(), 1e-3))) * smoothstep(0.0, 0.08, depthBelow);
    if (fade <= 0.0)
        return float3(1.0);

    // Where the sunbeam reaching this bed point entered the water, and the surface's ripples bend it there.
    float2 entry = bed.xz + L.xz * (depthBelow / L.y) + normalOffset.xz * depthBelow;
    float inv = waterCausticsInvScale();
    float time = frame.clip.z;
    float2 uv0 = entry * inv + float2(0.031, 0.017) * time;
    float2 uv1 = mul(kFarRotation, entry) * (inv * 1.37) - float2(0.023, 0.029) * time;
    float a = emissionTexture.SampleLevel(materialSampler, uv0, 0.0).r;
    float b = emissionTexture.SampleLevel(materialSampler, uv1, 0.0).r;
    float pattern = min(a, b) * 2.5;

    bool shadowsOn = lights.counts.w == 0;
    float shadow = shadowsOn ? dirShadow(0, bed, float3(0.0, 1.0, 0.0), L) : 1.0;
    shadow = lerp(1.0, shadow, sun.color.w);
    float3 sunThrough = exp(-waterAbsorption() * (depthBelow / L.y));
    return 1.0 + waterCausticsStrength() * pattern * shadow * fade * sunThrough;
}

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    float3 P = input.inWorldPos;
    float2 xz = P.xz;
    float2 dx = materialGrad(ddx(xz)); // texture gradients (ADR 0174: mip bias under upscaling)
    float2 dy = materialGrad(ddy(xz));

    WaterFlow f = waterFlow(input.inWater.yz, xz);
    float3 Ng = normalize(input.inNormal);
    float3 N = waterSurfaceNormal(Ng, xz, dx, dy, f);

    float3 V = normalize(frame.cameraPosition.xyz - P);
    float NdotV = max(dot(N, V), 1e-4);
    float roughness = clamp(waterRoughness(), 0.02, 1.0);
    float F = kWaterF0 + (1.0 - kWaterF0) * pow(1.0 - NdotV, 5.0);
    float sunShadow;
    float3 glints = waterLights(N, Ng, V, P, roughness, sunShadow);

    // 1. The bed straight behind the surface.
    int2 pixel = int2(fragCoord.xy);
    float2 uv = fragCoord.xy * frame.viewport.zw;
    float3 viewP = mul(float4(P, 1.0), frame.view).xyz;
    float surfaceZ = -viewP.z;
    float4 behind = sceneCopy.Load(int3(pixel, 0));
    float3 straightBed = sceneWorldPosition(sceneViewPosition(uv, behind.a));
    float column = max(P.y - straightBed.y, 0.0);
    float straightPath = max(distance(P, straightBed), 0.0);

    // 2. Refraction.
    float3 bent = refract(-V, N, kWaterEta);
    float3 direction = normalize(lerp(-V, bent, waterScene.refraction.x));
    // The bent lookup stays on screen: clamped to the edge, lookups past it all read the edge row and smeared into
    // vertical streaks along the bottom of the screen. The offset is shortened to end inside the screen, and fades out
    // over the last 8 % towards each edge (the straight bed there). Near the camera a ripple's bend covers a large part
    // of the screen and stretched the bed into bands: the offset is capped at 2.5 % of the screen height.
    float2 offset = sceneUv(mul(float4(P + direction * min(straightPath, 8.0), 1.0), frame.view).xyz) - uv;
    float2 aspect = float2(frame.viewport.x * frame.viewport.w, 1.0);
    float reach = length(offset * aspect);
    offset *= min(1.0, 0.025 / max(reach, 1e-5));
    float2 room = select(offset > 0.0, 1.0 - uv, uv);
    float2 fit = saturate(room / max(abs(offset), 1e-5));
    float2 edgeDistance = min(uv, 1.0 - uv);
    offset *= min(fit.x, fit.y) * saturate(min(edgeDistance.x, edgeDistance.y) / 0.08);
    float2 bentUv = saturate(uv + offset);
    int2 size = int2(frame.viewport.xy);
    int2 bentPixel = clamp(int2(bentUv * float2(size)), int2(0), size - 1);
    float bentZ = sceneDepthAt(bentPixel, 0);
    if (bentZ < surfaceZ + 0.02)
    {
        bentUv = uv; // a foreground object at the bent pixel: keep the bed straight behind
        bentZ = behind.a;
    }

    float3 bed = sceneWorldPosition(sceneViewPosition(bentUv, bentZ));
    float path = max(distance(P, bed), 0.0);
    float depthBelow = max(P.y - bed.y, 0.0);
    float blur = waterScene.refraction.y * waterScene.refraction.z * saturate(path / 2.0);
    float3 refracted = sceneCopy.SampleLevel(bentUv, blur).rgb;

    // 3. Caustics.
    refracted *= waterCaustics(bed, depthBelow, N - Ng);

    // 4. The water body along the refracted path.
    float scatterAo = ambientOcclusionAt(fragCoord);
    float3 transmittance = exp(-waterAbsorption() * path);
    float3 scatter = waterScatterColor() * waterScatterStrength() * waterDiffuseLight(float3(0.0, 1.0, 0.0), sunShadow, scatterAo) *
                     (1.0 - transmittance);
    float3 body = refracted * transmittance + scatter;

    // 5. Reflection: the scene where the screen-space ray finds it, the sky's radiance elsewhere.
    float3 R = reflect(-V, N);
    R.y = abs(R.y); // ripples never reflect the ground below the horizon
    float2 brdf = iblBrdf(NdotV, roughness);
    float3 reflected = iblSpecular(R, roughness);
    if (waterScene.caustics.w > 0.0)
    {
        float3 viewR = normalize(mul(float4(R, 0.0), frame.view).xyz);
        float4 ssr = sceneReflection(viewP, viewR, roughness, fragCoord.xy);
        reflected = lerp(reflected, ssr.rgb, ssr.a);
    }

    float3 reflection = reflected * (kWaterF0 * brdf.x + brdf.y) * waterReflectionStrength();
    float3 color = body * (1.0 - F) + reflection + glints;

    // 6. Foam from the true column (rings where rocks break the surface), soft edges towards the unbent bed, fog.
    float foam = waterFoam(column, input.inUV, input.inWater, xz, dx, dy, f);
    float3 foamColor = 0.85 * waterDiffuseLight(Ng, sunShadow, 1.0);
    color = lerp(color, foamColor, foam);
    color = applyFog(color, P);
    float softEdge = waterSoftEdge();
    float edge = softEdge > 0.0 ? smoothstep(0.0, softEdge, column) : 1.0;
    color = lerp(behind.rgb, color, edge);
    return float4(color, waterScene.extra.x * edge);
}
