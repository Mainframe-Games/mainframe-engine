// The fragment side of the impostors (ADR 0172): the three views' UVs, the dithered view selection, and the maps blended
// over the views. Include frame.slang, material.slang, alpha_dither.slang and impostor.slang first.
#pragma once

// Dithered view selection (foliage3.w > 0, ADR 0172): each pixel takes one of the three views with the probability of its
// weight, by the frame's dither threshold, so views never double up (crisp leaves, a third of the fetches); TAA averages
// the choice into the blend. Returns the weights of the chosen view (one of them 1).
float3 impostorDitherWeights(float3 weights, float t)
{
    if (material.foliage3.w <= 0.0)
    {
        // Blended: the weights sharpened (cubed), so the nearest view dominates and the others only ease the switch
        // between views: less ghosting of three slightly different canopies.
        float3 w = weights * weights * weights;
        return w / max(w.x + w.y + w.z, 1e-6);
    }
    return t < weights.x ? float3(1.0, 0.0, 0.0) : t < weights.x + weights.y ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
}

// The three views' atlas UVs and the views' shared screen-space gradients (taken in uniform control flow: a dithered
// pixel's chosen UV jumps between views, its own derivatives would not).
struct ImpostorUvs
{
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float2 gx;
    float2 gy;
    float3 weights; // after the dithered selection
};

ImpostorUvs impostorUvs(float4 cells01, float2 cell2, float2 local0, float2 local1, float2 local2, float3 weights, float2 pixel)
{
    float frames = impostorFrames();
    ImpostorUvs o;
    o.uv0 = impostorAtlasUv(cells01.xy, local0, frames);
    o.uv1 = impostorAtlasUv(cells01.zw, local1, frames);
    o.uv2 = impostorAtlasUv(cell2, local2, frames);
    o.gx = ddx(local0) / frames;
    o.gy = ddy(local0) / frames;
    o.weights = impostorDitherWeights(weights, ditherThreshold(pixel + float2(5.0, 11.0)));
    return o;
}

// A map's value blended over the views of `w` (alphaWeights for the maps that mean nothing where a view is empty); one
// fetch for a dithered selection, three for a blend.
float4 impostorSample(Texture2D map, ImpostorUvs v, float3 w)
{
    if (material.foliage3.w > 0.0)
        return map.SampleGrad(materialSampler, v.weights.x > 0.0 ? v.uv0 : v.weights.y > 0.0 ? v.uv1 : v.uv2, v.gx, v.gy) * (w.x + w.y + w.z);
    return map.SampleGrad(materialSampler, v.uv0, v.gx, v.gy) * w.x + map.SampleGrad(materialSampler, v.uv1, v.gx, v.gy) * w.y +
           map.SampleGrad(materialSampler, v.uv2, v.gx, v.gy) * w.z;
}

// The alpha test: with dithered views the plain test (a pixel's alpha comes from one view, so its derivative jumps
// where neighbours chose another view, and the derivative-sharpened dither would fade the canopy into haze; TAA already
// averages the views' edges); with blended views alpha_dither.slang's (dithered with TAA when the material asks).
bool impostorKeeps(float alpha, float2 pixel)
{
    bool blended = cutoutKeeps(alpha, material.params.z, pixel); // uniform control flow (fwidth)
    return material.foliage3.w > 0.0 ? alpha >= material.params.z : blended;
}

// The albedo (rgba) of the views; `alphaWeights` = each view's weight × its alpha, for the other maps.
float4 impostorAlbedo(ImpostorUvs v, out float3 alphaWeights)
{
    if (material.foliage3.w > 0.0)
    {
        float4 a = impostorSample(albedoTexture, v, v.weights);
        alphaWeights = v.weights * a.a;
        return a * material.albedo;
    }

    float4 a0 = albedoTexture.SampleGrad(materialSampler, v.uv0, v.gx, v.gy);
    float4 a1 = albedoTexture.SampleGrad(materialSampler, v.uv1, v.gx, v.gy);
    float4 a2 = albedoTexture.SampleGrad(materialSampler, v.uv2, v.gx, v.gy);
    alphaWeights = v.weights * float3(a0.a, a1.a, a2.a);
    float4 albedo = a0 * v.weights.x + a1 * v.weights.y + a2 * v.weights.z;
    float covered = alphaWeights.x + alphaWeights.y + alphaWeights.z;
    if (covered > 1e-5) // colour of the covering views only (their edges are dilated, but not their alpha)
        albedo.rgb = (a0.rgb * alphaWeights.x + a1.rgb * alphaWeights.y + a2.rgb * alphaWeights.z) / covered;
    return albedo * material.albedo;
}
