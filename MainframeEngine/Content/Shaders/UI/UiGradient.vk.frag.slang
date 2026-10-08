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

struct Gradient
{
    int    func;
    int    numStops;
    float2 p; // linear: start point, radial: centre, conic: centre
    float2 v; // linear: vector to the end point, radial: inverse radius, conic: angled unit vector
    float4 stopColors[MAX_NUM_STOPS];
    float4 stopPositions[MAX_NUM_STOPS / 4]; // packed: position i is stopPositions[i / 4][i % 4]
};

[[vk::binding(0, 2)]] ConstantBuffer<Gradient> g;

float stopPosition(int i)
{
    return g.stopPositions[i >> 2][i & 3];
}

float4 mixStopColors(float t)
{
    float4 color = g.stopColors[0];
    for (int i = 1; i < g.numStops; i++)
        color = lerp(color, g.stopColors[i], smoothstep(stopPosition(i - 1), stopPosition(i), t));
    return color;
}

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord, [[vk::location(1)]] float4 inColor) : SV_Target0
{
    float t = 0.0;
    if (g.func == LINEAR || g.func == REPEATING_LINEAR)
    {
        float distSquare = dot(g.v, g.v);
        float2 V = inTexCoord - g.p;
        t = dot(g.v, V) / distSquare;
    }
    else if (g.func == RADIAL || g.func == REPEATING_RADIAL)
    {
        float2 V = inTexCoord - g.p;
        t = length(g.v * V);
    }
    else if (g.func == CONIC || g.func == REPEATING_CONIC)
    {
        // Each pair of arguments is a column of the rotation (row-major layout: mul(v, R)).
        float2x2 R = float2x2(g.v.x, -g.v.y, g.v.y, g.v.x);
        float2 V = mul(inTexCoord - g.p, R);
        t = 0.5 + atan2(-V.x, V.y) / (2.0 * PI);
    }

    if (g.func == REPEATING_LINEAR || g.func == REPEATING_RADIAL || g.func == REPEATING_CONIC)
    {
        float t0 = stopPosition(0);
        float t1 = stopPosition(g.numStops - 1);
        t = t0 + (t - t0 - (t1 - t0) * floor((t - t0) / (t1 - t0))); // GLSL mod(t - t0, t1 - t0)
    }

    return inColor * mixStopColors(t);
}
