// Per-instance colour variation from the instance's origin (ADR 0175; InstanceVariation in C# is the same maths): the
// translation of the instance transform, quantised to 1/16 m, hashed (PCG) into two numbers in [-1, 1]; the albedo is
// scaled by 1 ± material.variation.x (value) and shifted along a warm/cool axis by up to material.variation.y (hue).
// Vertex stage: every vertex of an instance gets the same factor, so it rides in a varying. Include material.slang first.
#pragma once

static const float kInstanceQuantum = 16.0; // 1 / InstanceVariation.Quantum
static const float3 kInstanceWarmAxis = float3(0.45, 0.12, -0.7);

uint instancePcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint instanceHash(float3 origin)
{
    int3 q = int3(floor(origin * kInstanceQuantum + 0.5));
    return instancePcg(uint(q.x) + instancePcg(uint(q.y) + instancePcg(uint(q.z))));
}

// The albedo factor of an instance at `origin` (1 when the material has no variation).
float3 instanceTint(float3 origin)
{
    float2 jitter = material.variation.xy;
    if (jitter.x <= 0.0 && jitter.y <= 0.0)
        return float3(1.0);
    uint h = instanceHash(origin);
    float2 r = float2(float(h & 0xFFFFu), float(h >> 16u)) * (2.0 / 65535.0) - 1.0;
    float value = 1.0 + jitter.x * r.x;
    float3 hue = float3(1.0) + jitter.y * r.y * kInstanceWarmAxis;
    return max(hue * value, float3(0.0));
}
