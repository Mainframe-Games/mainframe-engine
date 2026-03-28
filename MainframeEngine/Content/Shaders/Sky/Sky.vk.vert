#version 450

// Fullscreen triangle — positions are baked in, no vertex buffer required.
layout(location = 0) out vec2 outUV; // NDC xy passed to fragment for ray reconstruction

void main()
{
    const vec2[3] positions = vec2[3](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    vec2 pos = positions[gl_VertexIndex];
    gl_Position = vec4(pos, 0.0, 1.0); // drawn first so z value is irrelevant
    outUV = pos; // NDC [-1, 1] forwarded to fragment
}
