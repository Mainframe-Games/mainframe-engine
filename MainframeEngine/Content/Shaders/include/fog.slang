// Distance + height fog (WorldEnvironment's Fog* exports, ADR 0150) from the per-frame data (frame.fogColor,
// frame.fogParams). Density at height y is fogParams.x × exp(−fogParams.z × max(y − fogParams.y, 0)): uniform below the
// fog height, thinning above it (height density 0 = uniform everywhere). The optical depth along the view ray is
// integrated exactly, so it holds wherever the camera is. Sun scatter adds the first directional light's colour to the
// fog when looking towards it (Godot's fog_sun_scatter).
#pragma once

#include "frame.slang"
#include "lights_data.slang"

// ∫ density(y) dy from the fog height to y (negative below it).
float fogHeightIntegral(float y, float density, float height, float heightDensity)
{
    float above = y - height;
    if (above <= 0.0 || heightDensity < 1e-5)
        return density * above;
    return density * (1.0 - exp(-heightDensity * above)) / heightDensity;
}

// The fog's light along the view direction (unit vector from the camera).
float3 fogLight(float3 viewDir)
{
    float3 color = frame.fogColor.rgb;
    float scatter = frame.fogParams.w;
    if (scatter > 0.0 && lights.counts.x > 0)
    {
        float3 toSun = -normalize(lights.dir[0].directionIntensity.xyz);
        float amount = pow(max(dot(viewDir, toSun), 0.0), 8.0);
        color += lights.dir[0].color.rgb * lights.dir[0].directionIntensity.w * amount * scatter;
    }
    return color;
}

// Fogs a surface colour at worldPos as seen from the camera. Unchanged while fog is off (fogColor.a == 0).
float3 applyFog(float3 color, float3 worldPos)
{
    if (frame.fogColor.a == 0.0)
        return color;
    float3 toPoint = worldPos - frame.cameraPosition.xyz;
    float dist = length(toPoint);
    if (dist < 1e-4)
        return color;

    float density = frame.fogParams.x;
    float height = frame.fogParams.y;
    float heightDensity = frame.fogParams.z;
    float y0 = frame.cameraPosition.y;
    float dy = worldPos.y - y0;
    float depth;
    if (abs(dy) > 1e-3)
        depth = dist * (fogHeightIntegral(worldPos.y, density, height, heightDensity) -
                        fogHeightIntegral(y0, density, height, heightDensity)) / dy;
    else
        depth = dist * density * exp(-heightDensity * max(y0 - height, 0.0));
    float amount = 1.0 - exp(-max(depth, 0.0));
    return lerp(color, fogLight(toPoint / dist), amount);
}

// Fogs the sky along a view ray to infinity: only height fog reaches the sky (it thins out upwards), so the horizon
// fades into the fog and the zenith keeps most of its colour. Uniform fog (height density 0) leaves the sky clear.
float3 applySkyFog(float3 color, float3 dir)
{
    if (frame.fogColor.a == 0.0 || frame.fogParams.z < 1e-5)
        return color;
    float density = frame.fogParams.x;
    float height = frame.fogParams.y;
    float heightDensity = frame.fogParams.z;
    float amount = 1.0; // at or below the horizon the ray never leaves the fog
    if (dir.y > 1e-4)
    {
        float total = density / heightDensity; // the integral to infinite height
        float depth = (total - fogHeightIntegral(frame.cameraPosition.y, density, height, heightDensity)) / dir.y;
        amount = 1.0 - exp(-max(depth, 0.0));
    }
    return lerp(color, fogLight(dir), amount);
}
