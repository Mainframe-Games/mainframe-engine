// FXAA 3.11 (Timothy Lottes), the PC quality path with preset 12, on the tonemapped LDR image (ADR 0154): the
// tonemap wrote sRGB-encoded colour into an R8G8B8A8_UNORM target; this pass reads it with bilinear filtering and
// writes the swapchain before the overlay (canvas, gizmos, UI) draws, so only the 3D scene is smoothed.
// Luma is computed from the encoded colour (perceptual), not stored in alpha.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D ldr;

struct Fxaa
{
    float2 rcpFrame;  // 1 / target size in pixels
    uint decodeSrgb;  // 1: the swapchain view encodes sRGB itself, so write linear values
};

[[vk::push_constant]] ConstantBuffer<Fxaa> pc;

// Quality settings (FXAA 3.11 defaults).
static const float kSubpix = 0.75;             // amount of sub-pixel aliasing removal
static const float kEdgeThreshold = 0.166;     // minimum local contrast to process
static const float kEdgeThresholdMin = 0.0833; // ignore dark areas below this

// Preset 12: 5 search steps along the edge.
static const int kSteps = 5;
static const float kStep[5] = { 1.0, 1.5, 2.0, 4.0, 12.0 };

float luma(float3 rgb) { return dot(rgb, float3(0.299, 0.587, 0.114)); }
float lumaAt(float2 uv) { return luma(ldr.SampleLevel(uv, 0.0).rgb); }
float lumaOff(float2 uv, float2 offset) { return luma(ldr.SampleLevel(uv + offset * pc.rcpFrame, 0.0).rgb); }

float3 fxaa(float2 posM)
{
    float3 rgbM = ldr.SampleLevel(posM, 0.0).rgb;
    float lumaM = luma(rgbM);
    float lumaS = lumaOff(posM, float2(0.0, 1.0));
    float lumaE = lumaOff(posM, float2(1.0, 0.0));
    float lumaN = lumaOff(posM, float2(0.0, -1.0));
    float lumaW = lumaOff(posM, float2(-1.0, 0.0));

    float rangeMax = max(max(lumaN, lumaW), max(lumaE, max(lumaS, lumaM)));
    float rangeMin = min(min(lumaN, lumaW), min(lumaE, min(lumaS, lumaM)));
    float range = rangeMax - rangeMin;
    if (range < max(kEdgeThresholdMin, rangeMax * kEdgeThreshold))
        return rgbM;

    float lumaNW = lumaOff(posM, float2(-1.0, -1.0));
    float lumaSE = lumaOff(posM, float2(1.0, 1.0));
    float lumaNE = lumaOff(posM, float2(1.0, -1.0));
    float lumaSW = lumaOff(posM, float2(-1.0, 1.0));

    // Edge orientation: horizontal or vertical, from second derivatives over the 3 × 3 neighbourhood.
    float lumaNS = lumaN + lumaS;
    float lumaWE = lumaW + lumaE;
    float subpixRcpRange = 1.0 / range;
    float subpixNSWE = lumaNS + lumaWE;
    float edgeHorz1 = -2.0 * lumaM + lumaNS;
    float edgeVert1 = -2.0 * lumaM + lumaWE;
    float lumaNESE = lumaNE + lumaSE;
    float lumaNWNE = lumaNW + lumaNE;
    float edgeHorz2 = -2.0 * lumaE + lumaNESE;
    float edgeVert2 = -2.0 * lumaN + lumaNWNE;
    float lumaNWSW = lumaNW + lumaSW;
    float lumaSWSE = lumaSW + lumaSE;
    float edgeHorz4 = abs(edgeHorz1) * 2.0 + abs(edgeHorz2);
    float edgeVert4 = abs(edgeVert1) * 2.0 + abs(edgeVert2);
    float edgeHorz3 = -2.0 * lumaW + lumaNWSW;
    float edgeVert3 = -2.0 * lumaS + lumaSWSE;
    float edgeHorz = abs(edgeHorz3) + edgeHorz4;
    float edgeVert = abs(edgeVert3) + edgeVert4;

    float subpixNWSWNESE = lumaNWSW + lumaNESE;
    float lengthSign = pc.rcpFrame.x;
    bool horzSpan = edgeHorz >= edgeVert;
    float subpixA = subpixNSWE * 2.0 + subpixNWSWNESE;
    if (!horzSpan)
    {
        lumaN = lumaW;
        lumaS = lumaE;
    }
    else
    {
        lengthSign = pc.rcpFrame.y;
    }
    float subpixB = subpixA * (1.0 / 12.0) - lumaM;

    // Which side of the pixel the edge lies on.
    float gradientN = lumaN - lumaM;
    float gradientS = lumaS - lumaM;
    float lumaNN = lumaN + lumaM;
    float lumaSS = lumaS + lumaM;
    bool pairN = abs(gradientN) >= abs(gradientS);
    float gradient = max(abs(gradientN), abs(gradientS));
    if (pairN)
        lengthSign = -lengthSign;
    float subpixC = saturate(abs(subpixB) * subpixRcpRange);

    float2 posB = posM;
    float2 offNP = horzSpan ? float2(pc.rcpFrame.x, 0.0) : float2(0.0, pc.rcpFrame.y);
    if (!horzSpan)
        posB.x += lengthSign * 0.5;
    else
        posB.y += lengthSign * 0.5;

    // Walk along the edge in both directions until the luma leaves the edge pair's average.
    float2 posN = posB - offNP * kStep[0];
    float2 posP = posB + offNP * kStep[0];
    float subpixD = -2.0 * subpixC + 3.0;
    float lumaEndN = lumaAt(posN);
    float subpixE = subpixC * subpixC;
    float lumaEndP = lumaAt(posP);
    if (!pairN)
        lumaNN = lumaSS;
    float gradientScaled = gradient * 0.25;
    float lumaMM = lumaM - lumaNN * 0.5;
    float subpixF = subpixD * subpixE;
    bool lumaMLTZero = lumaMM < 0.0;
    lumaEndN -= lumaNN * 0.5;
    lumaEndP -= lumaNN * 0.5;
    bool doneN = abs(lumaEndN) >= gradientScaled;
    bool doneP = abs(lumaEndP) >= gradientScaled;
    if (!doneN)
        posN -= offNP * kStep[1];
    if (!doneP)
        posP += offNP * kStep[1];

    for (int i = 2; i < kSteps && (!doneN || !doneP); i++)
    {
        if (!doneN)
            lumaEndN = lumaAt(posN) - lumaNN * 0.5;
        if (!doneP)
            lumaEndP = lumaAt(posP) - lumaNN * 0.5;
        doneN = abs(lumaEndN) >= gradientScaled;
        doneP = abs(lumaEndP) >= gradientScaled;
        if (!doneN)
            posN -= offNP * kStep[i];
        if (!doneP)
            posP += offNP * kStep[i];
    }

    float dstN = horzSpan ? posM.x - posN.x : posM.y - posN.y;
    float dstP = horzSpan ? posP.x - posM.x : posP.y - posM.y;
    bool goodSpanN = (lumaEndN < 0.0) != lumaMLTZero;
    bool goodSpanP = (lumaEndP < 0.0) != lumaMLTZero;
    float spanLength = dstP + dstN;
    bool directionN = dstN < dstP;
    float dst = min(dstN, dstP);
    bool goodSpan = directionN ? goodSpanN : goodSpanP;
    float subpixG = subpixF * subpixF;
    float pixelOffset = dst * (-1.0 / spanLength) + 0.5;
    float subpixH = subpixG * kSubpix;
    float pixelOffsetGood = goodSpan ? pixelOffset : 0.0;
    float pixelOffsetSubpix = max(pixelOffsetGood, subpixH);
    if (!horzSpan)
        posM.x += pixelOffsetSubpix * lengthSign;
    else
        posM.y += pixelOffsetSubpix * lengthSign;
    return ldr.SampleLevel(posM, 0.0).rgb;
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float3 color = fxaa(fragCoord.xy * pc.rcpFrame);
    if (pc.decodeSrgb != 0u)
        color = srgbToLinear(color);
    return float4(color, 1.0);
}
