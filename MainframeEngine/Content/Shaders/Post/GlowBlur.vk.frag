#version 450

// One pass of Godot 4.7's glow blur (servers/rendering/renderer_rd/shaders/effects/copy.glsl, MODE_GAUSSIAN_BLUR +
// MODE_GLOW, MIT; ADR 0124), split into two raster passes per level:
//  horizontal: sample the source at each destination pixel centre (a 2×2 box on a source twice the size, clamped to
//              the edge), weight fireflies down on the first level, blur along x with Godot's 9-tap kernel;
//  vertical:   blur the horizontal result along y, undo the firefly weighting on the first level, multiply by the
//              glow strength (every level), and on the first level apply exposure, the HDR threshold feedback and the
//              luminance cap.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant) uniform Glow {
    vec2  dstSize;   // destination size in pixels
    uint  flags;     // 1: horizontal pass, 2: first level
    float strength;
    float exposure;
    float threshold;
    float scale;
    float bloom;
    float luminanceCap;
} pc;

const float kKernel[5] = float[5](0.2024, 0.1790, 0.1240, 0.0672, 0.0285);
const vec3 kLuma = vec3(0.299, 0.587, 0.114);

bool horizontal() { return (pc.flags & 1u) != 0u; }
bool first() { return (pc.flags & 2u) != 0u; }

// The source at destination pixel `px`'s centre, clamped to the edge in destination pixels.
vec4 tap(vec2 px)
{
    vec2 uv = clamp((px + 0.5) / pc.dstSize, 0.5 / pc.dstSize, 1.0 - 0.5 / pc.dstSize);
    vec4 c = textureLod(source, uv, 0.0);
    if (horizontal() && first())
        c /= 1.0 + dot(c.rgb, kLuma / max(pc.luminanceCap, 6.0)); // tonemap the samples to tame fireflies
    return c;
}

void main()
{
    vec2 px = floor(gl_FragCoord.xy);
    vec2 axis = horizontal() ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 color = tap(px) * kKernel[0];
    for (int i = 1; i < 5; i++)
        color += (tap(px + axis * float(i)) + tap(px - axis * float(i))) * kKernel[i];

    if (!horizontal())
    {
        if (first())
            color /= 1.0 - dot(color.rgb, kLuma / max(pc.luminanceCap, 6.0)); // undo the firefly weighting
        color *= pc.strength;
        if (first())
        {
            color *= pc.exposure;
            float luminance = max(color.r, max(color.g, color.b));
            float feedback = max(smoothstep(pc.threshold, pc.threshold + pc.scale, luminance), pc.bloom);
            color = min(color * feedback, vec4(pc.luminanceCap));
        }
    }

    outColor = color;
}
