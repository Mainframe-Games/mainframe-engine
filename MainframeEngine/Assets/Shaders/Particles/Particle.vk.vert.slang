// GpuParticles3D draw passes (ParticleRenderer, ADR 0206): set 0 frame (camera), the draw pass mesh's MeshVertex at
// binding 0 and one ParticleInstance per particle at binding 1 (instance rate: position + scale, velocity + angle, linear
// colour), in world space, or the emitter's with LocalCoords (pc.model is then its world matrix, else the identity).
// The particle's basis comes from the billboard mode (Godot's billboard_mode on the draw pass material).
#include "particle_common.slang"

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inPositionScale; // xyz = position, w = scale
    [[vk::location(4)]] float4 inVelocityAngle; // xyz = velocity, w = angle (radians)
    [[vk::location(5)]] float4 inColor;         // linear RGBA (process material colour × ramps)
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] float4 outColor;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = pc.model;
    float3 center = mul(float4(input.inPositionScale.xyz, 1.0), model).xyz;
    float3 velocity = mul(float4(input.inVelocityAngle.xyz, 0.0), model).xyz;
    float angle = input.inVelocityAngle.w;
    float scale = input.inPositionScale.w;
    float emitterScale = length(model[0].xyz); // the emitter's scale also sizes camera-facing particles (local coordinates)

    uint billboard = particleBillboardMode();
    bool alignY = (pc.flags.x & kParticleAlignY) != 0u;
    float3 camRight = frame.invViewRotation[0].xyz;
    float3 camUp = frame.invViewRotation[1].xyz;
    float3 camBack = frame.invViewRotation[2].xyz;

    float3 X, Y, Z;
    float speed = length(velocity);
    if (billboard == kBillboardEnabled)
    {
        X = camRight * emitterScale;
        Y = camUp * emitterScale;
        Z = camBack * emitterScale;
    }
    else if (billboard == kBillboardFixedY)
    {
        // Billboard.Face's FixedY: upright, turned about world Y toward the camera.
        float3 x = float3(camBack.z, 0.0, -camBack.x);
        if (dot(x, x) < 1e-8)
            x = float3(camRight.x, 0.0, camRight.z);
        X = (dot(x, x) < 1e-8 ? float3(1.0, 0.0, 0.0) : normalize(x)) * emitterScale;
        Y = float3(0.0, 1.0, 0.0) * emitterScale;
        Z = cross(X, Y) / max(emitterScale, 1e-6);
    }
    else if (billboard == kBillboardParticles)
    {
        if (alignY && speed > 1e-6)
        {
            // Velocity-aligned: Y along the velocity, turned about it to face the camera (sparks, rain streaks).
            float3 y = velocity / speed;
            float3 toCamera = normalize(frame.cameraPosition.xyz - center);
            float3 x = cross(y, toCamera);
            x = dot(x, x) < 1e-8 ? camRight : normalize(x);
            X = x * emitterScale;
            Y = y * emitterScale;
            Z = cross(x, y) * emitterScale;
        }
        else
        {
            // Godot's BILLBOARD_PARTICLES: the camera's basis turned by the particle's angle in the view plane.
            float s = sin(angle);
            float c = cos(angle);
            X = (camRight * c - camUp * s) * emitterScale;
            Y = (camRight * s + camUp * c) * emitterScale;
            Z = camBack * emitterScale;
        }
    }
    else
    {
        // No billboard: the process material's orientation in the emitter's space (Godot's particle transform).
        float3 x, y, z;
        float3 localVelocity = input.inVelocityAngle.xyz;
        float localSpeed = length(localVelocity);
        if (alignY && localSpeed > 1e-6)
        {
            y = localVelocity / localSpeed;
            x = cross(y, float3(0.0, 0.0, 1.0));
            x = dot(x, x) < 1e-8 ? float3(1.0, 0.0, 0.0) : normalize(x);
            z = cross(x, y);
        }
        else if ((pc.flags.x & kParticleDisableZ) != 0u)
        {
            float s = sin(angle);
            float c = cos(angle);
            x = float3(c, -s, 0.0);
            y = float3(s, c, 0.0);
            z = float3(0.0, 0.0, 1.0);
        }
        else
        {
            float s = sin(angle);
            float c = cos(angle);
            x = float3(c, 0.0, -s);
            y = float3(0.0, 1.0, 0.0);
            z = float3(s, 0.0, c);
        }

        X = mul(float4(x, 0.0), model).xyz;
        Y = mul(float4(y, 0.0), model).xyz;
        Z = mul(float4(z, 0.0), model).xyz;
    }

    float3 local = input.inPosition * scale;
    float3 worldPos = center + X * local.x + Y * local.y + Z * local.z;
    float3 normal = X * input.inNormal.x + Y * input.inNormal.y + Z * input.inNormal.z;

    VsOut o;
    o.outWorldPos = worldPos;
    o.outNormal = normal;
    o.outUV = input.inUV;
    o.outColor = input.inColor;
    o.position = mul(float4(worldPos, 1.0), frame.viewProjection);
    return o;
}
