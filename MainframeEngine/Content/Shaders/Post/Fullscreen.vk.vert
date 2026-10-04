#version 450

// Fullscreen triangle for post-processing passes: no vertex buffer, three vertices cover the viewport.
// outNdc is the NDC position; fragment shaders that read a same-sized image use gl_FragCoord instead.
layout(location = 0) out vec2 outNdc;

void main()
{
    const vec2[3] positions = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    vec2 pos = positions[gl_VertexIndex];
    gl_Position = vec4(pos, 0.0, 1.0);
    outNdc = pos;
}
