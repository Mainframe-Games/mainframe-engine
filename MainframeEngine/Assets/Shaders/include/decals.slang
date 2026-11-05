// Forward decals (G6.5, Decal nodes): set 0 binding 8 holds the view's decals (DecalSelection / DecalGpu in C#, std140,
// 192 bytes each), binding 9 the shared decal atlas (linear RGBA8 with mips: albedo and emission sRGB-encoded, normal and
// ORM linear). A surface instance's decal mask (MeshInstanceData, location 10) names the decals its bounds touch; the
// fragment shader applies them in index order to its albedo, normal, ORM and emission before lighting.
#pragma once
#include "limits.slang"
#include "common.slang"

struct DecalGpu
{
    float4 toLocalX; // world → unit box [-0.5, 0.5]^3: local.x = dot(toLocalX.xyz, p) + toLocalX.w
    float4 toLocalY;
    float4 toLocalZ;
    float4 albedoRect; // atlas tile: xy corner, zw size (all 0: no texture)
    float4 normalRect;
    float4 ormRect;
    float4 emissionRect;
    float4 modulate; // linear rgb, a = coverage
    float4 fade;     // albedo mix, normal fade, upper fade, lower fade
    float4 extra;    // emission energy, distance fade
    float4 axisX;    // the box's X axis (texture u) in world space
    float4 axisY;    // the box's Y axis (the projection points along -Y) in world space
};

struct DecalBlock
{
    uint4 count; // x = decals
    DecalGpu decals[MAX_DECALS];
};

[[vk::binding(8, 0)]] ConstantBuffer<DecalBlock> decalData;
[[vk::binding(9, 0)]] Sampler2D decalAtlas;

// What decals change of a surface before it is lit. orm: ambient occlusion, roughness, metallic.
struct DecalSurface
{
    float3 albedo;
    float3 N;
    float3 orm;
    float3 emission;
};

float4 decalSample(float4 rect, float2 uv, float2 duvdx, float2 duvdy)
{
    return decalAtlas.SampleGrad(rect.xy + uv * rect.zw, duvdx * rect.zw, duvdy * rect.zw);
}

// Applies the decals in mask to s. dpdx/dpdy: the world position's screen derivatives, taken before any branch (so
// the atlas sampling below has gradients); Ngeo: the geometric normal for the normal fade.
void applyDecals(uint mask, float3 worldPos, float3 dpdx, float3 dpdy, float3 Ngeo, inout DecalSurface s)
{
    while (mask != 0u)
    {
        uint i = firstbitlow(mask);
        mask &= mask - 1u;
        DecalGpu d = decalData.decals[i];
        float3 local = float3(dot(d.toLocalX.xyz, worldPos) + d.toLocalX.w,
                              dot(d.toLocalY.xyz, worldPos) + d.toLocalY.w,
                              dot(d.toLocalZ.xyz, worldPos) + d.toLocalZ.w);
        if (any(abs(local) > float3(0.5)))
            continue;

        // Godot's fades: towards the box's top and bottom faces (pow of the distance from the middle plane), by the
        // angle between the surface and the projection axis, and the view's distance fade.
        float y = local.y * 2.0;
        float fade = pow(saturate(1.0 - abs(y)), y > 0.0 ? d.fade.z : d.fade.w) * d.extra.y;
        float3 up = d.axisY.xyz;
        if (d.fade.y > 0.0)
            fade *= smoothstep(d.fade.y, 1.0, dot(Ngeo, up) * 0.5 + 0.5);
        if (fade <= 0.0)
            continue;

        float2 uv = local.xz + 0.5; // u along +X, v along +Z (the texture's top edge at the box's -Z face)
        float2 duvdx = float2(dot(d.toLocalX.xyz, dpdx), dot(d.toLocalZ.xyz, dpdx));
        float2 duvdy = float2(dot(d.toLocalX.xyz, dpdy), dot(d.toLocalZ.xyz, dpdy));

        float coverage = fade * d.modulate.a;
        if (d.albedoRect.z > 0.0)
        {
            float4 albedo = decalSample(d.albedoRect, uv, duvdx, duvdy);
            coverage *= albedo.a;
            s.albedo = lerp(s.albedo, srgbToLinear(albedo.rgb) * d.modulate.rgb, coverage * d.fade.x);
        }

        if (d.normalRect.z > 0.0)
        {
            // OpenGL/glTF convention (+Y up the image, i.e. towards the box's -Z face), in the box's frame.
            float3 n = decalSample(d.normalRect, uv, duvdx, duvdy).xyz * 2.0 - 1.0;
            float3 T = d.axisX.xyz;
            float3 B = normalize(cross(up, T)); // the box's -Z axis (Z = X × Y): up the image
            float3 decalN = normalize(T * n.x + B * n.y + up * max(n.z, 1e-3));
            s.N = normalize(lerp(s.N, decalN, coverage));
        }

        if (d.ormRect.z > 0.0)
            s.orm = lerp(s.orm, decalSample(d.ormRect, uv, duvdx, duvdy).rgb, coverage);

        if (d.emissionRect.z > 0.0)
        {
            float4 e = decalSample(d.emissionRect, uv, duvdx, duvdy);
            s.emission += srgbToLinear(e.rgb) * d.modulate.rgb * e.a * d.extra.x * fade;
        }
    }
}
