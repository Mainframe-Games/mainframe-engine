// Bokeh depth of field (ADR 0168): the push block shared by Post/DofPrefilter, DofGather and DofComposite, and the
// circle of confusion from the scene depth. Include with: #include "dof.slang"
#pragma once

struct Dof
{
    float4 projection;    // the unjittered projection's M33, M34, M43, M44 (row vectors): view distance from depth
    float  farDistance;   // DofBlurFarDistance (m)
    float  farTransition; // DofBlurFarTransition (m); 0: a hard edge
    float  nearDistance;  // DofBlurNearDistance (m)
    float  nearTransition;
    float  maxRadius;     // the full blur's radius in full-resolution pixels (DofMaxRadius)
    uint   flags;         // 1: far blur, 2: near blur
    uint   taps;          // gather taps (16 or 32)
    float  pad;
};

[[vk::push_constant]] ConstantBuffer<Dof> pc;

// Distance from the camera plane (m) of depth-buffer value d: z_view = (M43 − d·M44) / (d·M34 − M33), perspective or
// orthographic (System.Numerics' right-handed projections look down −z).
float viewDistance(float d)
{
    float4 p = pc.projection;
    return -(p.z - d * p.w) / (d * p.y - p.x);
}

float ramp(float beyond, float transition)
{
    return transition > 0.0 ? saturate(beyond / transition) : (beyond > 0.0 ? 1.0 : 0.0);
}

// The signed circle of confusion in full-resolution pixels: positive behind the far plane, negative in front of the
// near one, 0 in focus (PostProcessSettings.DofBlur × DofMaxRadius). Non-decreasing with distance.
float circleOfConfusion(float depth)
{
    float z = viewDistance(depth);
    float far = (pc.flags & 1u) != 0u ? ramp(z - pc.farDistance, pc.farTransition) : 0.0;
    float near = (pc.flags & 2u) != 0u ? ramp(pc.nearDistance - z, pc.nearTransition) : 0.0;
    return (far >= near ? far : -near) * pc.maxRadius;
}
