using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The engine's standard vertex (32 bytes, interleaved): position, normal, UV. Position comes first so the shadow
/// pipelines (which read only <c>location 0</c> with a 32-byte stride) draw any mesh. No tangents: normal maps
/// use a screen-space cotangent frame (see <c>Content/Shaders/include/material.slang</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector2 UV = uv;

    /// <summary>Bytes per vertex.</summary>
    public const int Size = 32;
}

/// <summary>
/// Per-instance data of a batched mesh draw (80 bytes, binding 1, instance rate): the model matrix (row-vector
/// System.Numerics layout, read as the four rows of a row-major Slang <c>float4x4</c>) and the object id written by the ID pass.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MeshInstanceData
{
    public Matrix4x4 Model;
    public uint ObjectId;
    private uint _pad0, _pad1, _pad2;

    public const int Size = 80;

    public MeshInstanceData(in Matrix4x4 model, uint objectId)
    {
        Model = model;
        ObjectId = objectId;
        _pad0 = _pad1 = _pad2 = 0;
    }
}

/// <summary>Vertex input layouts used by mesh pipelines (part of the pipeline state key).</summary>
public enum VertexLayoutId : byte
{
    /// <summary><see cref="MeshVertex"/> at binding 0 + <c>MeshInstanceData</c> at binding 1 (instanced).</summary>
    MeshInstanced,
}

internal static class VertexLayouts
{
    /// <summary>Binding 0: <see cref="MeshVertex"/>; binding 1: <see cref="MeshInstanceData"/>.</summary>
    public static readonly VertexInputBindingDescription[] MeshInstancedBindings =
    [
        new() { Binding = 0, Stride = MeshVertex.Size, InputRate = VertexInputRate.Vertex },
        new() { Binding = 1, Stride = MeshInstanceData.Size, InputRate = VertexInputRate.Instance },
    ];

    /// <summary>0 position, 1 normal, 2 uv; 3–6 model matrix rows; 7 object id.</summary>
    public static readonly VertexInputAttributeDescription[] MeshInstancedAttributes =
    [
        new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
        new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
        new() { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
        new() { Location = 3, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
        new() { Location = 4, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
        new() { Location = 5, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
        new() { Location = 6, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 48 },
        new() { Location = 7, Binding = 1, Format = Format.R32Uint, Offset = 64 },
    ];

    // Each pipeline gets exactly the attributes its vertex shader reads: Slang drops unread shader inputs, and an
    // attribute the shader does not read is a validation warning (ADR 0144).

    /// <summary>Lit and outline pipelines (<c>Mesh.vk.vert</c>, <c>MeshOutline.vk.vert</c>): all but the object id.</summary>
    public static readonly VertexInputAttributeDescription[] MeshAttributes = MeshInstancedAttributes[..7];

    /// <summary>Object-ID pipeline (<c>MeshId.vk.vert</c>): position, uv, model rows and object id (no normal).</summary>
    public static readonly VertexInputAttributeDescription[] MeshIdAttributes = [MeshInstancedAttributes[0], .. MeshInstancedAttributes[2..]];

    /// <summary>Shadow casters: binding 0 positions (stride 32), binding 1 model rows at locations 1–4.</summary>
    public static readonly VertexInputBindingDescription[] ShadowInstancedBindings = MeshInstancedBindings;

    public static readonly VertexInputAttributeDescription[] ShadowInstancedAttributes =
    [
        new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
        new() { Location = 1, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
        new() { Location = 2, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
        new() { Location = 3, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
        new() { Location = 4, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 48 },
    ];

    /// <summary>Cutout shadow casters: <see cref="ShadowInstancedAttributes"/> plus the UV at location 5 (alpha test).</summary>
    public static readonly VertexInputAttributeDescription[] ShadowCutoutInstancedAttributes =
    [
        .. ShadowInstancedAttributes,
        new() { Location = 5, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
    ];
}
