// 2D point lights (docs/design/canvas.md, ADR 0117): Godot 4.7's canvas.glsl positional-light loop without normals or
// shadows. Include after canvas.glsl in a fragment stage with CANVAS_LIGHT_SET defined (the set index of the frame's
// light block). Each light maps a target-pixel vertex to its texture's UV; outside the texture it adds nothing.

#define CANVAS_MAX_LIGHTS 8
#define CANVAS_LIGHT_BLEND_ADD 0u
#define CANVAS_LIGHT_BLEND_SUB 1u
#define CANVAS_LIGHT_BLEND_MIX 2u

struct CanvasLight {
    vec4 matrixX;   // uv.x = dot(matrixX.xy, vertex) + matrixX.z
    vec4 matrixY;   // uv.y = dot(matrixY.xy, vertex) + matrixY.z
    vec4 color;     // rgb: the light's colour, a: colour alpha × energy
    uvec4 flags;    // x: blend mode
};

layout(set = CANVAS_LIGHT_SET, binding = 0, std140) uniform CanvasLights {
    CanvasLight canvas_lights[CANVAS_MAX_LIGHTS];
};

layout(set = CANVAS_LIGHT_SET, binding = 1) uniform sampler2D canvas_light_textures[CANVAS_MAX_LIGHTS];

// color: after the canvas modulation; base: the item's colour before it (Godot's base_color).
void canvas_apply_lights(inout vec4 color, vec4 base, vec2 vertex)
{
    uint mask = floatBitsToUint(canvas_pc.lights.x);
    for (uint i = 0u; i < uint(CANVAS_MAX_LIGHTS); i++)
    {
        if ((mask & (1u << i)) == 0u)
            continue;
        CanvasLight light = canvas_lights[i];
        vec2 uv = vec2(dot(light.matrixX.xy, vertex) + light.matrixX.z, dot(light.matrixY.xy, vertex) + light.matrixY.z);
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThanEqual(uv, vec2(1.0))))
            continue;
        vec4 light_color = textureLod(canvas_light_textures[i], uv, 0.0);
        light_color.rgb *= light.color.rgb * light.color.a;
        light_color.rgb *= base.rgb;
        if (light.flags.x == CANVAS_LIGHT_BLEND_ADD)
            color.rgb += light_color.rgb * light_color.a;
        else if (light.flags.x == CANVAS_LIGHT_BLEND_SUB)
            color.rgb -= light_color.rgb * light_color.a;
        else
            color.rgb = mix(color.rgb, light_color.rgb, light_color.a);
    }
}
