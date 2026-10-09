// The sky's velocity (ADR 0163): the sky is at infinity, so only the camera's rotation moves it. The pixel's ray (as
// include/sky.slang builds it, through this frame's jittered projection) projected by last frame's view-projection as a
// direction gives last frame's screen position; this frame's is the pixel without the jitter.
#include "frame.slang"

[shader("fragment")]
float2 main(float4 fragCoord : SV_Position) : SV_Target0
{
    // The prepass flips the viewport like the scene pass: row 0 is NDC +Y.
    float2 uv = fragCoord.xy * frame.viewport.zw;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 viewPos = mul(float4(ndc, 1.0, 1.0), frame.invProjection);
    float3 ray = mul(viewPos.xyz / viewPos.w, (float3x3)frame.invViewRotation);
    float4 previous = mul(float4(ray, 0.0), frame.prevViewProjection);
    if (previous.w <= 1e-6)
        return float2(0.0); // behind last frame's camera (a cut), or orthographic: no rotation to follow
    return ndcToScreenUv(ndc - frame.jitter.xy) - ndcToScreenUv(previous.xy / previous.w);
}
