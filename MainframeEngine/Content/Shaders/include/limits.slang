// Generated from Content/Shaders/limits.json by build/Shaders.targets. Do not edit.
#pragma once

#define MAX_DIR_LIGHTS 4 // Directional lights in the lights UBO.
#define MAX_POINT_LIGHTS 16 // Point lights in the lights UBO.
#define MAX_SPOT_LIGHTS 8 // Spot lights in the lights UBO.
#define MAX_SHADOW_DIR 4 // Shadowed directional lights: the first casts cascades, the others get an atlas tile each.
#define MAX_SHADOW_SPOT 8 // Shadowed spot lights (atlas tiles): every spot light can cast (M4: the atlas cut the shadow samplers from 15 to 6).
#define MAX_SHADOW_POINT 4 // Shadowed point lights (cube maps, six passes each).
#define MAX_SHADOW_CASCADES 4 // Cascades of the primary directional light (layers of the cascade array).
#define MAX_SHADOW_ATLAS_MAPS 11 // Atlas tiles: secondary directional lights (MAX_SHADOW_DIR - 1) plus spot lights (MAX_SHADOW_SPOT).
