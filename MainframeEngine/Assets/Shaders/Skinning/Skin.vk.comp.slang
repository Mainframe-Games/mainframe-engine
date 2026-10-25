// Compute pre-skinning (ADR 0207): one thread per vertex of a skinned instance. Reads the mesh's bind-pose vertices with
// their four joints and weights (SkinVertex, 64 bytes) and the instance's joint palette (three float4 rows per joint: the
// column-vector 3x4 matrix, i.e. the columns of the C# row-vector Matrix4x4), and writes the posed vertex in the engine's
// MeshVertex layout (position, normal, uv: 8 floats) into the instance's vertex buffer for this frame slot. Every mesh
// pipeline (lit, shadows, outline, object ids, prepass) then draws that buffer unchanged. A vertex whose weights are all
// zero (a static surface of the mesh) is copied.

struct SkinVertex
{
    float4 positionU; // xyz position, w = uv.x
    float4 normalV;   // xyz normal, w = uv.y
    uint4 joints;
    float4 weights;
};

struct SkinPush
{
    uint vertexCount;
    uint paletteBase; // first float4 of this instance's palette
    uint jointCount;
    uint pad;
};

[[vk::binding(0, 0)]] StructuredBuffer<SkinVertex> sourceVertices;
[[vk::binding(1, 0)]] StructuredBuffer<float4> palette;
[[vk::binding(2, 0)]] RWStructuredBuffer<float> skinnedVertices;
[[vk::push_constant]] ConstantBuffer<SkinPush> skin;

[shader("compute")]
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= skin.vertexCount)
        return;

    SkinVertex src = sourceVertices[index];
    float4 p = float4(src.positionU.xyz, 1.0);
    float3 n = src.normalV.xyz;
    float3 position = src.positionU.xyz;
    float3 normal = n;
    float total = src.weights.x + src.weights.y + src.weights.z + src.weights.w;
    if (total > 0.0)
    {
        position = float3(0.0, 0.0, 0.0);
        normal = float3(0.0, 0.0, 0.0);
        for (uint k = 0; k < 4; k++)
        {
            float w = src.weights[k];
            uint joint = src.joints[k];
            if (w > 0.0 && joint < skin.jointCount)
            {
                uint row = skin.paletteBase + joint * 3;
                float4 r0 = palette[row];
                float4 r1 = palette[row + 1];
                float4 r2 = palette[row + 2];
                position += w * float3(dot(r0, p), dot(r1, p), dot(r2, p));
                normal += w * float3(dot(r0.xyz, n), dot(r1.xyz, n), dot(r2.xyz, n));
            }
        }

        float len = length(normal);
        normal = len > 1e-12 ? normal / len : n;
    }

    uint o = index * 8;
    skinnedVertices[o + 0] = position.x;
    skinnedVertices[o + 1] = position.y;
    skinnedVertices[o + 2] = position.z;
    skinnedVertices[o + 3] = normal.x;
    skinnedVertices[o + 4] = normal.y;
    skinnedVertices[o + 5] = normal.z;
    skinnedVertices[o + 6] = src.positionU.w;
    skinnedVertices[o + 7] = src.normalV.w;
}
