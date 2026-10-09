// Shadow density (ADR 0172): leaf casters that are denser than the canopy they stand for (cluster cards, impostors) keep
// only a share of their texels, by a stable 4 × 4 Bayer pattern on the shadow map, so the PCF/PCSS kernel reads a partial
// shadow (no TAA in shadow maps, so the pattern does not move).
#pragma once

// True when a caster fragment at shadow-map pixel `pixel` (SV_Position.xy) casts at `density` (0..1; 1 = always).
bool shadowDensityKeeps(float density, float2 pixel)
{
    if (density >= 1.0)
        return true;
    uint2 q = uint2(pixel) & 3u;
    static const float kBayer[16] = { 0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0 };
    return density > (kBayer[q.y * 4u + q.x] + 0.5) / 16.0;
}
