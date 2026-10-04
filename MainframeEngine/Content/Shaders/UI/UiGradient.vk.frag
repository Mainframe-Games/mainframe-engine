#version 450

// RmlUi gradient decorators (linear-, radial-, conic-gradient and their repeating forms), ported from RmlUi's GL3
// backend. Texture coordinates carry the element-space position; stop colours are premultiplied sRGB.
#define LINEAR 0
#define RADIAL 1
#define CONIC 2
#define REPEATING_LINEAR 3
#define REPEATING_RADIAL 4
#define REPEATING_CONIC 5
#define MAX_NUM_STOPS 16
#define PI 3.14159265

layout(location = 0) in vec2 inTexCoord;
layout(location = 1) in vec4 inColor;

layout(set = 2, binding = 0) uniform Gradient {
    int func;
    int numStops;
    vec2 p; // linear: start point, radial: centre, conic: centre
    vec2 v; // linear: vector to the end point, radial: inverse radius, conic: angled unit vector
    vec4 stopColors[MAX_NUM_STOPS];
    vec4 stopPositions[MAX_NUM_STOPS / 4]; // packed: position i is stopPositions[i / 4][i % 4]
} g;

layout(location = 0) out vec4 outColor;

float stopPosition(int i)
{
    return g.stopPositions[i >> 2][i & 3];
}

vec4 mixStopColors(float t)
{
    vec4 color = g.stopColors[0];
    for (int i = 1; i < g.numStops; i++)
        color = mix(color, g.stopColors[i], smoothstep(stopPosition(i - 1), stopPosition(i), t));
    return color;
}

void main()
{
    float t = 0.0;
    if (g.func == LINEAR || g.func == REPEATING_LINEAR)
    {
        float distSquare = dot(g.v, g.v);
        vec2 V = inTexCoord - g.p;
        t = dot(g.v, V) / distSquare;
    }
    else if (g.func == RADIAL || g.func == REPEATING_RADIAL)
    {
        vec2 V = inTexCoord - g.p;
        t = length(g.v * V);
    }
    else if (g.func == CONIC || g.func == REPEATING_CONIC)
    {
        mat2 R = mat2(g.v.x, -g.v.y, g.v.y, g.v.x);
        vec2 V = R * (inTexCoord - g.p);
        t = 0.5 + atan(-V.x, V.y) / (2.0 * PI);
    }

    if (g.func == REPEATING_LINEAR || g.func == REPEATING_RADIAL || g.func == REPEATING_CONIC)
    {
        float t0 = stopPosition(0);
        float t1 = stopPosition(g.numStops - 1);
        t = t0 + mod(t - t0, t1 - t0);
    }

    outColor = inColor * mixStopColors(t);
}
