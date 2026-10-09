// FoliageMaterial3D's parameters inside the StandardMaterial3D block (material.slang; MaterialParams.From(Foliage) in
// C#), so foliage shares set 2's layout and the cutout caster pipelines (ADR 0151, 0172):
//   emission.x = translucency, emission.y = wind strength scale, emission.z = branch bend scale, emission.w = trunk sway
//   params     = specular, shininess (from the roughness until PBR), alpha cutoff (0 when not cut out), normal scale
//   flags.x    = texture bits; 4 (the emission slot) = the detail texture: thickness + AO (leaves) or moss (bark)
//   flags.z    = back faces: 1 flip the normal, 2 keep it, 3 culled
//   foliage0   = translucency scatter exponent, translucency colour (linear rgb)
//   foliage1   = distance fade (per instance, ADR 0172): begin, end (m; 0 = unbounded), margin (m), 1 when on
//   foliage2   = moss coverage (0..1), detail normal tiling (0 = off), detail normal strength, moss noise scale (1/m)
//   foliage3   = moss colour (linear rgb), shadow density (0..1, the foliage caster: ShadowFoliageCutout.vk.frag)
// Include material.slang first.
#pragma once

float foliageTranslucencyAmount() { return material.emission.x; }
float foliageWindStrength() { return material.emission.y; }
float foliageBranchBend() { return material.emission.z; }
float foliageTrunkSway() { return material.emission.w; }
float foliageTranslucencyScatter() { return material.foliage0.x; }
float3 foliageTranslucencyColor() { return material.foliage0.yzw; }
bool foliageHasDetailTexture() { return (material.flags.x & kHasEmissionTexture) != 0u; }

// How much of the vertex canopy AO (Custom0.w) applies when light probes are bound (ADR 0170): the probes already carry the
// canopy's occlusion at probe scale, so the vertex term keeps only part of its strength (G8e's CanopyAoStrength).
static const float kCanopyAoWithProbes = 0.4;

static const uint kFoliageBackFlip = 1u;
static const uint kFoliageBackKeep = 2u;
static const uint kFoliageBackCull = 3u;

// ── Per-instance distance fade (LOD cross-fade, ADR 0172) ─────────────────────────────────────────────────────────
// A level of detail drawn per instance between foliage1.x and foliage1.y (camera to the instance origin, m), with a
// dithered band of ±margin around each end, so two levels cross-fade (TAA turns the dither into a fade). Instances
// wholly outside [begin − margin, end + margin) collapse in the vertex stage.

// True when the level is drawn for an instance at `distance` (the vertex stage; collapse the instance otherwise).
bool distanceFadeVisible(float4 fade, float distance)
{
    if (fade.w <= 0.0)
        return true;
    if (fade.x > 0.0 && distance < fade.x - fade.z)
        return false;
    return fade.y <= 0.0 || distance < fade.y + fade.z;
}

// The level's share of the instance at `distance`: 1 inside its range, ramping over the bands (0 = the other level).
float2 distanceFadeRamps(float4 fade, float distance)
{
    float margin = max(fade.z, 1e-3);
    float fadeIn = fade.x > 0.0 ? saturate((distance - (fade.x - margin)) / (2.0 * margin)) : 1.0;
    float fadeOut = fade.y > 0.0 ? saturate((distance - (fade.y - margin)) / (2.0 * margin)) : 0.0;
    return float2(fadeIn, fadeOut);
}

// True when the fragment at a dither threshold `t` (0..1) belongs to this level: complementary with the neighbouring
// level's (the incoming one keeps t < fadeIn, the outgoing one keeps t >= fadeOut), so the two never overlap or leave
// holes at one distance.
bool distanceFadeKeeps(float4 fade, float distance, float t)
{
    if (fade.w <= 0.0)
        return true;
    float2 ramps = distanceFadeRamps(fade, distance);
    return t < ramps.x && t >= ramps.y;
}

// A clip position outside the view volume for every vertex of a collapsed instance (zero-area, clipped).
static const float4 kCollapsedPosition = float4(2.0, 2.0, 2.0, 1.0);
