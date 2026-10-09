// FoliageMaterial3D's parameters inside the StandardMaterial3D block (material.slang; MaterialParams.From(Foliage) in
// C#), so foliage shares set 2's layout and the cutout caster pipelines (ADR 0151):
//   emission.x = translucency, emission.y = wind strength scale, emission.z = branch bend scale
//   params     = specular, shininess (from the roughness until PBR), alpha cutoff (0 when not cut out), normal scale
//   flags.z    = back faces: 1 flip the normal, 2 keep it, 3 culled
// Include material.slang first.
#pragma once

float foliageTranslucencyAmount() { return material.emission.x; }
float foliageWindStrength() { return material.emission.y; }
float foliageBranchBend() { return material.emission.z; }

static const uint kFoliageBackFlip = 1u;
static const uint kFoliageBackKeep = 2u;
static const uint kFoliageBackCull = 3u;
