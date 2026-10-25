// GLSL compatibility for shaders translated from Godot's shading language (canvas_item: CanvasShaderCompiler, spatial:
// SpatialShaderCompiler), compiled with -allow-glsl.
#pragma once

// GLSL's one-argument matrix constructors (the translators rewrite matN(x) to these): a scalar builds a diagonal, a
// matrix copies its upper-left block with the identity elsewhere. Slang fills every element from a scalar and rejects
// resizing. Rows here are GLSL columns (row-major layout), so m[i] is column i.
float2x2 glsl_mat2(float s) { return float2x2(s, 0.0, 0.0, s); }
float2x2 glsl_mat2(float2x2 m) { return m; }
float2x2 glsl_mat2(float3x3 m) { return float2x2(m[0].xy, m[1].xy); }
float2x2 glsl_mat2(float4x4 m) { return float2x2(m[0].xy, m[1].xy); }
float3x3 glsl_mat3(float s) { return float3x3(s, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, s); }
float3x3 glsl_mat3(float2x2 m) { return float3x3(float3(m[0], 0.0), float3(m[1], 0.0), float3(0.0, 0.0, 1.0)); }
float3x3 glsl_mat3(float3x3 m) { return m; }
float3x3 glsl_mat3(float4x4 m) { return float3x3(m[0].xyz, m[1].xyz, m[2].xyz); }
float4x4 glsl_mat4(float s) { return float4x4(s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, s); }
float4x4 glsl_mat4(float2x2 m)
{
    return float4x4(float4(m[0], 0.0, 0.0), float4(m[1], 0.0, 0.0), float4(0.0, 0.0, 1.0, 0.0), float4(0.0, 0.0, 0.0, 1.0));
}
float4x4 glsl_mat4(float3x3 m) { return float4x4(float4(m[0], 0.0), float4(m[1], 0.0), float4(m[2], 0.0), float4(0.0, 0.0, 0.0, 1.0)); }
float4x4 glsl_mat4(float4x4 m) { return m; }
