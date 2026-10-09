// SSAO pass 1 (ADR 0165): ground-truth ambient occlusion at half resolution from the depth prepass (Jimenez et al. 2016;
// the structure of Intel's XeGTAO). Per texel: the view position and a normal rebuilt from the full-resolution depth,
// `slices` slice directions rotated per texel by a 4 × 4 Bayer pattern (and per frame with TAA's jitter index), `steps`
// depth samples each way along each slice out to the radius (quadratically spaced: denser near the texel), the highest
// horizon on each side faded out between 38 % and 100 % of the radius, then the cosine-weighted visible arc between the
// two horizons (eq. 10) × the projected normal's length, divided by the same integral without occluders, so an open
// surface is exactly 1 whatever the slice count. A near-field term from the first half of the steps adds the detail.
// The bent normal (ADR 0170; XeGTAO's): per slice, the cosine-weighted mean direction of the visible arc (along the slice
// and towards the camera), summed over the slices and normalised.
// Output RGBA16F: r = visibility (not clamped: the denoise averages it), g = linear depth (metres; sky: kGtaoSkyDistance),
// b, a = the view-space bent normal's x and y (its z faces the camera).
#include "gtao.slang"

[[vk::binding(0, 0)]] Sampler2D<float> sceneDepth;

// 4 × 4 Bayer order: neighbouring texels get rotations far apart, and the denoise's 4 × 4 window holds each once.
static const uint kBayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

// acos to ~1e-4 rad (XeGTAO's FastACos).
float fastAcos(float x)
{
    float a = abs(x);
    float r = (-0.156583 * a + PI * 0.5) * sqrt(1.0 - a);
    return x >= 0.0 ? r : PI - r;
}

float3 positionAt(int2 p)
{
    return gtaoViewPosition(p, sceneDepth.Load(int3(p, 0)));
}

// The cosine-weighted visible arc of a slice between horizon angles h0 ≤ n ≤ h1, n the projected normal's angle from
// the view vector (Jimenez 2016, eq. 10: both sides summed).
float visibleArc(float h0, float h1, float n, float cosN, float sinN)
{
    return (2.0 * cosN + 2.0 * (h0 + h1) * sinN - cos(2.0 * h0 - n) - cos(2.0 * h1 - n)) * 0.25;
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 size = int2(gtao.proj3.zw);
    int2 hp = int2(fragCoord.xy);
    int2 p = min(hp * 2, size - 1); // the full-resolution texel this one stands for
    float depth = sceneDepth.Load(int3(p, 0));
    if (depth >= kGtaoSkyDepth)
        return float4(1.0, kGtaoSkyDistance, 0.0, 0.0);

    float3 P = gtaoViewPosition(p, depth);
    float3 V = normalize(-P);

    // The normal: per axis, the neighbour whose depth continues the surface (the smaller step), so edges stay sharp.
    float3 left = positionAt(max(p - int2(1, 0), int2(0)));
    float3 right = positionAt(min(p + int2(1, 0), size - 1));
    float3 up = positionAt(max(p - int2(0, 1), int2(0)));
    float3 down = positionAt(min(p + int2(0, 1), size - 1));
    bool useLeft = p.x + 1 >= size.x || (p.x > 0 && abs(P.z - left.z) < abs(right.z - P.z));
    bool useUp = p.y + 1 >= size.y || (p.y > 0 && abs(P.z - up.z) < abs(down.z - P.z));
    float3 dx = useLeft ? P - left : right - P; // towards +x (right)
    float3 dy = useUp ? P - up : down - P;      // down the screen: view −y
    float3 N = cross(dy, dx);
    float nLength = length(N);
    N = nLength > 1e-12 ? N / nLength : V;
    if (dot(N, V) < 0.0)
        N = -N;

    // The radius on screen (full-resolution pixels), capped; below a pixel nothing can occlude.
    float w = P.z * gtao.proj3.x + gtao.proj3.y; // clip w: the distance for a perspective camera
    float radius = gtao.shape.x;
    float radiusPx = min(radius * gtao.proj.x * 0.5 * gtao.proj3.z / max(w, 1e-6), gtao.shape.y);
    if (!(radiusPx >= 1.0))
        return float4(1.0, -P.z, N.x, N.y);

    float falloffRange = 0.615 * radius;
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = (radius - falloffRange) / falloffRange + 1.0;

    uint2 q = uint2(hp) & 3u;
    float index = float(kBayer[q.y * 4u + q.x]);
    float temporal = gtao.noise.x;
    float rotation = frac((index + 0.5) / 16.0 + temporal * 0.618034);
    float stepJitter = frac(index * 0.381966 + 0.5 + temporal * 0.754878);

    int slices = int(gtao.noise.y);
    int steps = int(gtao.noise.z);
    int nearSteps = (steps + 1) / 2;
    float bias = gtao.shape.z;
    float2 centre = float2(p) + 0.5;
    float visible = 0.0, visibleNear = 0.0, open = 0.0;
    float3 bent = float3(0.0);

    for (int s = 0; s < slices; s++)
    {
        float phi = (float(s) + rotation) * PI / float(slices);
        float2 omega = float2(cos(phi), sin(phi));         // on screen (+y down)
        float3 direction = float3(omega.x, -omega.y, 0.0); // in view space (+y up)
        float3 orthoDirection = direction - dot(direction, V) * V;
        float3 axis = normalize(cross(orthoDirection, V));
        float3 projectedN = N - axis * dot(N, axis);
        float projectedLength = length(projectedN);
        float cosN = saturate(dot(projectedN, V) / max(projectedLength, 1e-4));
        float n = (dot(orthoDirection, projectedN) < 0.0 ? -1.0 : 1.0) * fastAcos(cosN);
        float sinN = sin(n);
        float low0 = -sinN; // cos(n + π/2): the tangent plane on the +direction side
        float low1 = sinN;  // cos(n − π/2): on the −direction side
        float horizon0 = low0, horizon1 = low1, near0 = low0, near1 = low1;

        for (int j = 0; j < steps; j++)
        {
            float t = (float(j) + stepJitter) / float(steps);
            float2 offset = omega * max(t * t * radiusPx, float(j) + 1.0);
            [unroll]
            for (int side = 0; side < 2; side++)
            {
                float2 at = side == 0 ? centre + offset : centre - offset;
                int2 sp = int2(floor(at));
                if (any(sp < int2(0)) || any(sp >= size))
                    continue; // off screen: no occluder known
                float3 delta = positionAt(sp) - P;
                float distance = length(delta);
                float c = dot(delta, V) / max(distance, 1e-6);
                float weight = saturate(distance * falloffMul + falloffAdd);
                if (side == 0)
                {
                    c = lerp(low0, c, weight);
                    horizon0 = max(horizon0, c);
                    if (j < nearSteps)
                        near0 = max(near0, c);
                }
                else
                {
                    c = lerp(low1, c, weight);
                    horizon1 = max(horizon1, c);
                    if (j < nearSteps)
                        near1 = max(near1, c);
                }
            }
        }

        // Horizon angles from the view vector, clamped to the hemisphere around n and lowered by the horizon threshold.
        float h0 = n + clamp(-fastAcos(horizon1) - n - bias, -PI * 0.5, PI * 0.5);
        float h1 = n + clamp(fastAcos(horizon0) - n + bias, -PI * 0.5, PI * 0.5);
        float n0 = n + clamp(-fastAcos(near1) - n - bias, -PI * 0.5, PI * 0.5);
        float n1 = n + clamp(fastAcos(near0) - n + bias, -PI * 0.5, PI * 0.5);
        visible += projectedLength * visibleArc(h0, h1, n, cosN, sinN);
        visibleNear += projectedLength * visibleArc(n0, n1, n, cosN, sinN);
        open += projectedLength * (cosN + n * sinN); // visibleArc(n − π/2, n + π/2)

        // The visible arc's cosine-weighted mean direction: t0 along the slice, t1 towards the camera (XeGTAO).
        float t0 = (6.0 * sin(h0 - n) - sin(3.0 * h0 - n) + 6.0 * sin(h1 - n) - sin(3.0 * h1 - n) + 16.0 * sin(n) -
                    3.0 * (sin(h0 + n) + sin(h1 + n))) / 12.0;
        float t1 = (-cos(3.0 * h0 - n) - cos(3.0 * h1 - n) + 8.0 * cos(n) - 3.0 * (cos(h0 + n) + cos(h1 + n))) / 12.0;
        float orthoLength = length(orthoDirection);
        float3 slice = orthoLength > 1e-6 ? orthoDirection / orthoLength : float3(0.0);
        bent += projectedLength * (slice * t0 + V * t1);
    }

    float inv = 1.0 / max(open, 1e-4);
    float visibility = visible * inv;
    float detail = saturate(1.0 + (visibleNear * inv - 1.0) * gtao.shape.w);
    float bentLength = length(bent);
    bent = bentLength > 1e-6 ? bent / bentLength : N;
    return float4(visibility * detail, -P.z, bent.x, bent.y);
}
