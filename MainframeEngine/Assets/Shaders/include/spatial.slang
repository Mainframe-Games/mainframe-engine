// Helpers of the template around translated Godot spatial shaders (SpatialShaderCompiler, ADR 0205; compiled with
// -allow-glsl). Native Slang: mul(v, M) with the C# matrices. Nothing here reads a descriptor, so the shadow casters
// (whose set 0 is the light matrix, not the frame) include it too.
#pragma once

#include "glsl_compat.slang"

// The normal matrix of a model matrix: the cofactor of its upper 3x3 (∝ the inverse transpose; non-uniform scale),
// signed by the determinant so mirrored instances keep outward normals. As Mesh.vk.vert: mul(n, spatial_normal_matrix(M)).
float3x3 spatial_normal_matrix(float4x4 model)
{
    float3x3 m = (float3x3)model;
    float3x3 cofactor = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    float det = dot(m[0], cofactor[0]);
    return cofactor * (det < 0.0 ? -1.0 : 1.0);
}

// The inverse of a rigid view matrix from its inverse rotation and the camera position (FrameData.invViewRotation,
// cameraPosition): Godot's INV_VIEW_MATRIX.
float4x4 spatial_inv_view(float4x4 invViewRotation, float3 cameraPosition)
{
    return float4x4(invViewRotation[0], invViewRotation[1], invViewRotation[2], float4(cameraPosition, 1.0));
}

// Godot's NORMAL_MAP (tangent space, +Y up, 0..1) applied to the world normal N with a tangent frame solved from the
// screen-space derivatives of the position and UV (no vertex tangents; include/material.slang's materialNormal), scaled
// by NORMAL_MAP_DEPTH.
float3 spatial_normal_map(float3 N, float3 map, float depth, float3 dp1, float3 dp2, float2 duv1, float2 duv2)
{
    float det = duv1.x * duv2.y - duv2.x * duv1.y;
    if (abs(det) < 1e-12)
        return N;
    float s = sign(det);
    float3 dPdu = (dp1 * duv2.y - dp2 * duv1.y) * s;
    float3 dPdv = (dp2 * duv1.x - dp1 * duv2.x) * s;
    float3 T = dPdu - N * dot(N, dPdu);
    float3 B = N * dot(N, dPdv) - dPdv; // +Y in the map points towards decreasing v (up in the image)
    if (dot(T, T) < 1e-20 || dot(B, B) < 1e-20)
        return N;
    T = normalize(T);
    B = normalize(B);
    float3 n = map * 2.0 - 1.0;
    n.xy *= depth;
    return normalize(T * n.x + B * n.y + N * max(n.z, 1e-3));
}
