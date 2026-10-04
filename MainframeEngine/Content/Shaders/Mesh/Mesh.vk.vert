#version 450
#extension GL_GOOGLE_include_directive : require

// Batched mesh instances (MeshRenderer): set 0 frame (camera), MeshVertex at binding 0, MeshInstanceData at binding 1
// (instance rate: model matrix + object id). One instanced draw per (pipeline, material, mesh surface) run.
#include "frame.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;
// System.Numerics row-vector matrix, uploaded as-is: its rows are the GLSL columns.
layout(location = 3) in vec4 inModel0;
layout(location = 4) in vec4 inModel1;
layout(location = 5) in vec4 inModel2;
layout(location = 6) in vec4 inModel3;
layout(location = 7) in uint inObjectId;

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec2 outUV;
layout(location = 3) flat out uint outObjectId;

void main()
{
    mat4 model = mat4(inModel0, inModel1, inModel2, inModel3);
    vec4 worldPos = model * vec4(inPosition, 1.0);

    // Normal matrix = cofactor of the upper 3x3 (∝ inverse transpose; handles non-uniform scale), with the
    // determinant's sign so mirrored instances keep outward normals.
    mat3 m = mat3(model);
    mat3 cofactor = mat3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    float det = dot(m[0], cofactor[0]);

    outWorldPos = worldPos.xyz;
    outNormal = cofactor * inNormal * (det < 0.0 ? -1.0 : 1.0);
    outUV = inUV;
    outObjectId = inObjectId;
    gl_Position = frame.viewProjection * worldPos;
}
