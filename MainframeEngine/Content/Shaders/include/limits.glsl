// Generated from Content/Shaders/limits.json by build/Shaders.targets. Do not edit.
#ifndef MF_LIMITS_GLSL
#define MF_LIMITS_GLSL

#define MAX_DIR_LIGHTS 4 // Directional lights in the lights UBO.
#define MAX_POINT_LIGHTS 16 // Point lights in the lights UBO.
#define MAX_SPOT_LIGHTS 8 // Spot lights in the lights UBO.
#define MAX_SHADOW_DIR 4 // Directional shadow maps (one per directional light).
#define MAX_SHADOW_SPOT 7 // Spot shadow maps: one less than the spot lights, to fit MoltenVK's 16 fragment samplers (4 + 7 + 4 + 1 material sampler shared by its textures, ADR 0019).
#define MAX_SHADOW_POINT 4 // Point shadow cube maps (six passes each).

#endif
