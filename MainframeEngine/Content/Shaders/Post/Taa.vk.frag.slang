// TAA resolve (ADR 0166, TaaEffect): this frame's jittered HDR scene colour against last frame's resolve, into the
// history's current target (linear HDR; alpha = the view depth the next frame's disocclusion test reads). Paired with
// Fullscreen.vk.vert; one output pixel per fragment.
//
// 1. Reconstruct the current colour at the output pixel's centre from the 3 x 3 input samples around it, each weighted
//    by a Blackman-Harris fit of its distance (the sample of texel t was taken at t + 0.5 - jitter). Input and output
//    have the same size today; the arithmetic is written in input pixels so TAAU can render below the output size.
// 2. Reproject: the velocity of the closest depth in the 3 x 3 (silhouettes carry their motion one pixel out).
// 3. Read the history with a 5-tap Catmull-Rom filter (sharp under sub-pixel motion).
// 4. Drop it where it saw something else (its stored view depth is not where this pixel was last frame), clip it to the
//    neighbourhood's variance box in YCoCg (Salvi 2016; mean +- 1.25 sigma inside min/max) and blend.
// 5. Reactive pixels keep less history: the scene alpha is 1 for opaque surfaces and the sky, and water in the main view
//    lowers it by its opacity (BlendMode.AlphaReactive): its flow animates under the bed's (zero) motion vectors, and a
//    full history would smear the ripples along the flow.
// Everything from step 1 on works on Karis' luminance-weighted colour c / (1 + exposure * luma): a few bright texels
// (sky through needles) cannot dominate a pixel, which stops them flickering; the result is unweighted at the end.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D sceneColor;      // this frame, jittered, linear HDR; a = 1 - reactivity (point sampler)
[[vk::binding(1, 0)]] Sampler2D history;         // last frame's resolve, linear HDR, a = view depth (bilinear sampler)
[[vk::binding(2, 0)]] Sampler2D velocityImage;   // screen-UV motion, previousUv = uv - velocity (point sampler)
[[vk::binding(3, 0)]] Sampler2D<float> sceneDepth; // [0, 1], 1 = far (point sampler)

// TaaParams in C# (TaaEffect.Params).
struct TaaParams
{
    float4 depthCurrent;  // column 3 of the inverse unjittered view-projection: view depth = 1 / dot(ndcH, this)
    float4 depthPrevious; // column 3 of inverse VP x last frame's VP: last frame's view depth = dot(ndcH, this) / dot(ndcH, depthCurrent)
    float2 jitterPixels;  // this frame's jitter in input pixels: the geometry moved by this much (x right, y down)
    float2 jitterNdc;     // the same in NDC (+Y up)
    float2 outputSize;
    float2 rcpOutputSize;
    float feedbackStill;  // history weight of a still pixel
    float feedbackMoving; // history weight at movingPixels of motion per frame
    float movingPixels;
    float exposure;       // scales the luminance weighting to what the tonemap will show
    float depthTolerance; // relative view-depth difference that counts as disoccluded
    uint flags;           // bit 0: the history is last frame's; bit 1: depth rejection
    float filterFalloff;  // the reconstruction's weight is exp(filterFalloff * d^2), d in output pixels
    float feedbackReactive; // history weight where the scene alpha marks the pixel reactive (water: alpha 0)
};

[[vk::push_constant]] ConstantBuffer<TaaParams> pc;

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);
static const float kGamma = 1.25;          // variance box half-size in standard deviations
static const float kMaxInput = 65000.0;    // keeps infinities (an over-bright sun) finite
static const float kWideFalloff = -2.29;   // exp(-2.29 d^2): Unreal's Gaussian fit of a Blackman-Harris window

float3 toWeighted(float3 c)
{
    return c / (1.0 + dot(c, kLuma) * pc.exposure);
}

float3 fromWeighted(float3 c)
{
    return c / max(1.0 - dot(c, kLuma) * pc.exposure, 1.0 / 1024.0);
}

float3 rgbToYCoCg(float3 c)
{
    return float3(dot(c, float3(0.25, 0.5, 0.25)), dot(c, float3(0.5, 0.0, -0.5)), dot(c, float3(-0.25, 0.5, -0.25)));
}

float3 yCoCgToRgb(float3 c)
{
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// Moves p towards the centre of [boxMin, boxMax] until it is inside (Playdead's clip, not a per-channel clamp).
float3 clipToBox(float3 boxMin, float3 boxMax, float3 p)
{
    float3 centre = 0.5 * (boxMax + boxMin);
    float3 extents = 0.5 * (boxMax - boxMin) + 1e-5;
    float3 v = p - centre;
    float3 units = abs(v / extents);
    float m = max(units.x, max(units.y, units.z));
    return m > 1.0 ? centre + v / m : p;
}

// Catmull-Rom of the history at uv in five bilinear taps (the four corner taps' weights are negligible; Jimenez 2016).
float3 historyCatmullRom(float2 uv)
{
    float2 position = uv * pc.outputSize;
    float2 t1 = floor(position - 0.5) + 0.5;
    float2 f = position - t1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (t1 - 1.0) * pc.rcpOutputSize;
    float2 t3 = (t1 + 2.0) * pc.rcpOutputSize;
    float2 t12 = (t1 + w2 / w12) * pc.rcpOutputSize;

    float3 sum = history.SampleLevel(float2(t12.x, t0.y), 0.0).rgb * (w12.x * w0.y)
               + history.SampleLevel(float2(t0.x, t12.y), 0.0).rgb * (w0.x * w12.y)
               + history.SampleLevel(t12, 0.0).rgb * (w12.x * w12.y)
               + history.SampleLevel(float2(t3.x, t12.y), 0.0).rgb * (w3.x * w12.y)
               + history.SampleLevel(float2(t12.x, t3.y), 0.0).rgb * (w12.x * w3.y);
    float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(sum / weight, float3(0.0));
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 inputSize = textureSize2D(sceneColor, 0);
    int2 last = inputSize - 1;
    float2 uv = fragCoord.xy * pc.rcpOutputSize;      // the output pixel's centre (unjittered screen UV)
    float2 position = uv * float2(inputSize);          // in input pixels
    int2 centre = int2(floor(position));
    float2 toOutputPixels = pc.outputSize / float2(inputSize);

    float3 current = float3(0.0);
    float currentWeight = 0.0;
    float3 wide = float3(0.0); // a softer reconstruction (Blackman-Harris over 1.5 pixels) for pixels without history
    float wideWeight = 0.0;
    float3 m1 = float3(0.0);
    float3 m2 = float3(0.0);
    float3 lo = float3(1e9);
    float3 hi = float3(-1e9);
    float closest = 2.0;
    int2 closestTexel = clamp(centre, int2(0), last);

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            int2 texel = clamp(centre + int2(x, y), int2(0), last);
            float3 c = toWeighted(min(max(sceneColor.Load(int3(texel, 0)).rgb, float3(0.0)), float3(kMaxInput)));
            float2 d = (float2(centre + int2(x, y)) + 0.5 - pc.jitterPixels - position) * toOutputPixels;
            float w = exp(pc.filterFalloff * dot(d, d));
            current += c * w;
            currentWeight += w;
            float wideW = exp(kWideFalloff * dot(d, d));
            wide += c * wideW;
            wideWeight += wideW;

            float3 ycocg = rgbToYCoCg(c);
            m1 += ycocg;
            m2 += ycocg * ycocg;
            lo = min(lo, ycocg);
            hi = max(hi, ycocg);

            float z = sceneDepth.Load(int3(texel, 0));
            if (z < closest)
            {
                closest = z;
                closestTexel = texel;
            }
        }
    }

    current /= currentWeight;
    wide /= wideWeight;

    // The closest sample's view depth now and where it was last frame (unjittered NDC: its texel centre minus the jitter).
    float2 closestUv = (float2(closestTexel) + 0.5) / float2(inputSize);
    float4 ndc = float4(closestUv.x * 2.0 - 1.0 - pc.jitterNdc.x, 1.0 - closestUv.y * 2.0 - pc.jitterNdc.y, closest, 1.0);
    float inverseW = dot(ndc, pc.depthCurrent);
    float viewDepth = 1.0 / inverseW;
    float previousDepth = dot(ndc, pc.depthPrevious) / inverseW;

    float2 velocity = velocityImage.Load(int3(closestTexel, 0)).xy;
    float2 previousUv = uv - velocity;

    // Without history (the first frame, a cut, a disocclusion) a single jittered sample would show raw aliasing and the
    // dithered alpha's noise for a frame or more: the softer reconstruction hides both until the history builds up.
    float3 result = wide;
    bool useHistory = (pc.flags & 1u) != 0u && all(previousUv >= float2(0.0)) && all(previousUv <= float2(1.0));
    if (useHistory && (pc.flags & 2u) != 0u)
    {
        // Disoccluded when no history texel around where this pixel was saw that depth (3 x 3: thin geometry such as
        // needles covers some texels and not others from frame to frame, and must not count as a disocclusion).
        int2 previousTexel = int2(previousUv * pc.outputSize);
        int2 lastOutput = int2(pc.outputSize) - 1;
        float tolerance = pc.depthTolerance * max(abs(previousDepth), 1e-4);
        bool seen = false;
        [unroll]
        for (int j = -1; j <= 1; j++)
        {
            [unroll]
            for (int i = -1; i <= 1; i++)
            {
                float stored = history.Load(int3(clamp(previousTexel + int2(i, j), int2(0), lastOutput), 0)).a;
                seen = seen || abs(stored - previousDepth) <= tolerance;
            }
        }

        useHistory = seen;
    }

    if (useHistory)
    {
        float3 mean = m1 / 9.0;
        float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, float3(0.0)));
        float3 boxMin = max(lo, mean - kGamma * sigma);
        float3 boxMax = min(hi, mean + kGamma * sigma);
        float3 previous = toWeighted(min(historyCatmullRom(previousUv), float3(kMaxInput)));
        previous = yCoCgToRgb(clipToBox(boxMin, boxMax, rgbToYCoCg(previous)));

        float speed = length(velocity * pc.outputSize);
        float feedback = lerp(pc.feedbackStill, pc.feedbackMoving, saturate(speed / pc.movingPixels));
        // Reactive pixels (water: animated, blended over a bed whose motion vectors it does not share) keep less history.
        float reactive = saturate(1.0 - sceneColor.Load(int3(clamp(centre, int2(0), last), 0)).a);
        feedback = lerp(feedback, min(feedback, pc.feedbackReactive), reactive);
        result = lerp(current, max(previous, float3(0.0)), feedback);
    }

    return float4(fromWeighted(result), viewDepth);
}
