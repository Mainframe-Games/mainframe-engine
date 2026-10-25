// GpuParticles3D draw passes (ParticleRenderer / ParticlePush in C#, ADR 0206): the push constants (128 bytes, vertex +
// fragment) shared by Particles/Particle.vk.vert and .frag. The draw pass material's parameters ride in them, so the
// material set (set 2) holds only its sampler and two images.
#pragma once

#include "frame.slang"

struct ParticlePush
{
    float4x4 model;      // the emitter's world matrix with local coordinates, else the identity
    float4 albedo;       // linear rgb, a = alpha
    float4 emission;     // linear rgb × energy, w = alpha cutoff (cutout)
    float4 uvTransform;  // xy = scale, zw = offset
    uint4 flags;         // x = bits below, y = shading (0 lit, 1 unshaded), z = alpha mode (0 opaque, 1 cutout, 2 blend),
                         // w = blend mode (0 mix, 1 add)
};

[[vk::push_constant]] ConstantBuffer<ParticlePush> pc;

// flags.x
static const uint kParticleHasAlbedo      = 1u;
static const uint kParticleHasEmission    = 2u;
static const uint kParticleVertexColor    = 4u;   // StandardMaterial3D.VertexColorUseAsAlbedo
static const uint kParticleAlignY         = 8u;   // ParticleProcessMaterial.ParticleFlagAlignY
static const uint kParticleDisableZ       = 16u;  // ParticleProcessMaterial.ParticleFlagDisableZ
static const uint kParticleBillboardShift = 8u;   // bits 8-9: BillboardMode

// BillboardMode in C#.
static const uint kBillboardDisabled  = 0u;
static const uint kBillboardEnabled   = 1u;
static const uint kBillboardFixedY    = 2u;
static const uint kBillboardParticles = 3u;

uint particleBillboardMode()
{
    return (pc.flags.x >> kParticleBillboardShift) & 3u;
}
