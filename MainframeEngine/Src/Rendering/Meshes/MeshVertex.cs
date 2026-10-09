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
/// The optional second vertex stream (20 bytes, binding 2): an RGBA8 colour (<see cref="MeshSurface.Colors"/>) and a
/// float4 (<see cref="MeshSurface.Custom0"/>). Uploaded only for meshes with a surface that has either stream (or one
/// drawn with a material that reads it, filled with the defaults: white and zero).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshVertexExt(uint color, Vector4 custom0)
{
    /// <summary>RGBA8, red in the lowest byte (<c>R8G8B8A8_UNORM</c> in memory order).</summary>
    public uint Color = color;
    public Vector4 Custom0 = custom0;

    /// <summary>Bytes per vertex.</summary>
    public const int Size = 20;

    /// <summary>Opaque white, the colour of a vertex without one.</summary>
    public const uint White = 0xFFFFFFFFu;

    /// <summary>Packs an RGBA 0..1 colour into RGBA8 (clamped, rounded to nearest).</summary>
    public static uint PackColor(Vector4 c)
    {
        static uint B(float v) => (uint)MathF.Round(Math.Clamp(float.IsNaN(v) ? 0f : v, 0f, 1f) * 255f);
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24;
    }
}

/// <summary>
/// The optional wind stream (16 bytes, binding 4; ADR 0172): <see cref="MeshSurface.Custom1"/> and
/// <see cref="MeshSurface.Custom2"/> as RGBA16F, read by the foliage pipelines (hierarchical wind pivots and stiffness).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct MeshVertexWind
{
    public Half X1, Y1, Z1, W1;
    public Half X2, Y2, Z2, W2;

    public MeshVertexWind(Vector4 custom1, Vector4 custom2)
    {
        (X1, Y1, Z1, W1) = ((Half)custom1.X, (Half)custom1.Y, (Half)custom1.Z, (Half)custom1.W);
        (X2, Y2, Z2, W2) = ((Half)custom2.X, (Half)custom2.Y, (Half)custom2.Z, (Half)custom2.W);
    }

    /// <summary>Bytes per vertex.</summary>
    public const int Size = 16;
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

    /// <summary><see cref="MeshInstanced"/> plus the <see cref="MeshVertexExt"/> at binding 2 (colour, custom0).</summary>
    MeshInstancedExt,
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

    /// <summary>Binding 2: the second vertex stream (<see cref="MeshVertexExt"/>).</summary>
    public static readonly VertexInputBindingDescription[] MeshInstancedExtBindings =
    [
        .. MeshInstancedBindings,
        new() { Binding = 2, Stride = MeshVertexExt.Size, InputRate = VertexInputRate.Vertex },
    ];

    /// <summary>Binding 4: the wind stream (<see cref="MeshVertexWind"/>, ADR 0172); foliage pipelines always bind it.</summary>
    public const uint WindBinding = 4;

    private static readonly VertexInputBindingDescription WindStream =
        new() { Binding = WindBinding, Stride = MeshVertexWind.Size, InputRate = VertexInputRate.Vertex };

    /// <summary>Foliage colour pipelines: <see cref="MeshInstancedExtBindings"/> + the wind stream.</summary>
    public static readonly VertexInputBindingDescription[] FoliageBindings = [.. MeshInstancedExtBindings, WindStream];

    /// <summary>Locations 14 and 15: custom1 and custom2 (RGBA16F, binding 4).</summary>
    public static readonly VertexInputAttributeDescription[] WindAttributes =
    [
        new() { Location = 14, Binding = WindBinding, Format = Format.R16G16B16A16Sfloat, Offset = 0 },
        new() { Location = 15, Binding = WindBinding, Format = Format.R16G16B16A16Sfloat, Offset = 8 },
    ];

    /// <summary>Location 8: the vertex colour (RGBA8 unorm); location 9: custom0.</summary>
    public static readonly VertexInputAttributeDescription ColorAttribute =
        new() { Location = 8, Binding = 2, Format = Format.R8G8B8A8Unorm, Offset = 0 };

    public static readonly VertexInputAttributeDescription Custom0Attribute =
        new() { Location = 9, Binding = 2, Format = Format.R32G32B32A32Sfloat, Offset = 4 };

    // Each pipeline gets exactly the attributes its vertex shader reads: Slang drops unread shader inputs, and an
    // attribute the shader does not read is a validation warning (ADR 0144).

    /// <summary>Lit and outline pipelines (<c>Mesh.vk.vert</c>, <c>MeshOutline.vk.vert</c>): all but the object id.</summary>
    public static readonly VertexInputAttributeDescription[] MeshAttributes = MeshInstancedAttributes[..7];

    /// <summary>Lit pipelines of surfaces with vertex streams (<c>MeshExt.vk.vert</c>): <see cref="MeshAttributes"/> + colour.</summary>
    public static readonly VertexInputAttributeDescription[] MeshExtAttributes = [.. MeshAttributes, ColorAttribute];

    /// <summary>Foliage pipelines (<c>Foliage.vk.vert</c>): <see cref="MeshAttributes"/> + colour + custom0 + custom1 + custom2.</summary>
    public static readonly VertexInputAttributeDescription[] FoliageAttributes = [.. MeshAttributes, ColorAttribute, Custom0Attribute, .. WindAttributes];

    /// <summary>Water pipelines (<c>Water.vk.vert</c>): <see cref="MeshAttributes"/> + custom0 (column depth, flow, foam).</summary>
    public static readonly VertexInputAttributeDescription[] WaterAttributes = [.. MeshAttributes, Custom0Attribute];

    /// <summary>Object-ID pipeline (<c>MeshId.vk.vert</c>): position, uv, model rows and object id (no normal).</summary>
    public static readonly VertexInputAttributeDescription[] MeshIdAttributes = [MeshInstancedAttributes[0], .. MeshInstancedAttributes[2..]];

    /// <summary>
    /// The depth prepass (ADR 0163): binding 3 holds last frame's <see cref="MeshInstanceData"/> (instance rate, read at the
    /// same instance index as binding 1: the view's buffer bound at the offset of its previous-model block, or a
    /// multimesh's own instances, which do not move).
    /// </summary>
    public const uint PreviousInstanceBinding = 3;

    private static readonly VertexInputBindingDescription PreviousInstances =
        new() { Binding = PreviousInstanceBinding, Stride = MeshInstanceData.Size, InputRate = VertexInputRate.Instance };

    /// <summary>Prepass bindings: <see cref="MeshInstancedBindings"/> + the previous instances.</summary>
    public static readonly VertexInputBindingDescription[] DepthBindings = [.. MeshInstancedBindings, PreviousInstances];

    /// <summary>Prepass bindings with the second vertex stream: <see cref="MeshInstancedExtBindings"/> + the previous instances.</summary>
    public static readonly VertexInputBindingDescription[] DepthExtBindings = [.. MeshInstancedExtBindings, PreviousInstances];

    /// <summary>Locations 10–13: last frame's model rows (binding 3).</summary>
    private static readonly VertexInputAttributeDescription[] PreviousModelAttributes =
    [
        new() { Location = 10, Binding = PreviousInstanceBinding, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
        new() { Location = 11, Binding = PreviousInstanceBinding, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
        new() { Location = 12, Binding = PreviousInstanceBinding, Format = Format.R32G32B32A32Sfloat, Offset = 32 },
        new() { Location = 13, Binding = PreviousInstanceBinding, Format = Format.R32G32B32A32Sfloat, Offset = 48 },
    ];

    /// <summary><c>Mesh/MeshDepth.vk.vert</c>: position, uv, model rows, previous model rows.</summary>
    public static readonly VertexInputAttributeDescription[] DepthAttributes =
        [MeshInstancedAttributes[0], .. MeshInstancedAttributes[2..7], .. PreviousModelAttributes];

    /// <summary><c>Mesh/MeshDepthExt.vk.vert</c>: <see cref="DepthAttributes"/> + colour.</summary>
    public static readonly VertexInputAttributeDescription[] DepthExtAttributes = [.. DepthAttributes, ColorAttribute];

    /// <summary><c>Foliage/FoliageDepth.vk.vert</c>: <see cref="DepthAttributes"/> + colour + custom0 + the wind stream.</summary>
    public static readonly VertexInputAttributeDescription[] DepthFoliageAttributes = [.. DepthAttributes, ColorAttribute, Custom0Attribute, .. WindAttributes];

    /// <summary>The foliage prepass: <see cref="DepthExtBindings"/> + the wind stream.</summary>
    public static readonly VertexInputBindingDescription[] DepthFoliageBindings = [.. DepthExtBindings, WindStream];

    /// <summary>Impostor pipelines (<c>Impostor.vk.vert</c>, ADR 0172): the corner UV and the model rows.</summary>
    public static readonly VertexInputAttributeDescription[] ImpostorAttributes = [MeshInstancedAttributes[2], .. MeshInstancedAttributes[3..7]];

    /// <summary><c>Impostor/ImpostorDepth.vk.vert</c>: <see cref="ImpostorAttributes"/> + the previous model rows.</summary>
    public static readonly VertexInputAttributeDescription[] ImpostorDepthAttributes = [.. ImpostorAttributes, .. PreviousModelAttributes];

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

    /// <summary>Impostor casters (ADR 0172): the model rows (1–4) and the corner UV (5).</summary>
    public static readonly VertexInputAttributeDescription[] ShadowImpostorInstancedAttributes = ShadowCutoutInstancedAttributes[1..];

    /// <summary>
    /// Foliage casters (wind): <see cref="ShadowCutoutInstancedAttributes"/> plus custom0 (binding 2) at location 6 and the
    /// wind stream's custom1 and custom2 (binding 4) at 7 and 8.
    /// </summary>
    public static readonly VertexInputBindingDescription[] ShadowFoliageInstancedBindings = FoliageBindings;

    public static readonly VertexInputAttributeDescription[] ShadowFoliageInstancedAttributes =
    [
        .. ShadowCutoutInstancedAttributes,
        new() { Location = 6, Binding = 2, Format = Format.R32G32B32A32Sfloat, Offset = 4 },
        new() { Location = 7, Binding = WindBinding, Format = Format.R16G16B16A16Sfloat, Offset = 0 },
        new() { Location = 8, Binding = WindBinding, Format = Format.R16G16B16A16Sfloat, Offset = 8 },
    ];
}
