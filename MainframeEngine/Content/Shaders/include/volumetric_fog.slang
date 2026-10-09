// Volumetric fog (ADR 0171): what the march and the temporal pass share. A half-resolution texel stands for the full
// resolution pixel at twice its coordinates (its top-left one): the march reads that pixel's depth, the temporal pass
// reprojects it and the composite weighs the four texels around a pixel by how close their depths are to its own.
// Positions come from the view's frame set (set 0): its jittered inverse projection and inverse view rotation, so the
// ray ends exactly on the surface the scene pass drew. Needs frame.slang.
#pragma once

#include "frame.slang"

// The full-resolution pixel a half-resolution texel stands for.
int2 volumetricPixel(int2 texel, float2 sceneSize)
{
    return min(texel * 2, int2(sceneSize) - int2(1));
}

// World position of a depth-buffer value at a full-resolution pixel (its centre).
float3 volumetricWorldPosition(int2 pixel, float depth, float2 sceneSize)
{
    float2 uv = (float2(pixel) + 0.5) / sceneSize;
    float4 ndc = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0); // NDC +Y is up and lands on row 0
    float4 view = mul(ndc, frame.invProjection);
    view.xyz /= view.w;
    return frame.cameraPosition.xyz + mul(float4(view.xyz, 0.0), frame.invViewRotation).xyz;
}

// The view ray through a full-resolution pixel: its start on the near plane and its unit direction. A perspective camera's
// rays start at the camera (near plane, within millimetres); an orthographic camera's run parallel.
void volumetricRay(int2 pixel, float2 sceneSize, out float3 origin, out float3 direction)
{
    origin = volumetricWorldPosition(pixel, 0.0, sceneSize);
    float3 far = volumetricWorldPosition(pixel, 0.5, sceneSize); // any depth past the near plane (the far one may be at infinity)
    direction = normalize(far - origin);
}
