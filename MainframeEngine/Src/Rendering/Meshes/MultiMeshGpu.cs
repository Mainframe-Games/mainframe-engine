using System.Buffers;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The persistent instance buffer of one <see cref="MultiMeshInstance3D"/>: a device-local buffer of
/// <see cref="MeshInstanceData"/> in world space (instance transform × node transform, the node's object id), drawn
/// with the regular instanced pipelines. It holds every instance; the draw takes the first
/// <see cref="MultiMesh.DrawnInstanceCount"/>. Rebuilt only when the multimesh's content (mesh, count, transforms), the
/// node's transform or the mesh bounds change; a <see cref="MultiMesh.VisibleInstanceCount"/> change only updates
/// <see cref="Count"/> and the bounds (0 B), and a steady frame costs one comparison. A rebuild creates a new buffer and
/// retires the old one through the deletion queue, so frames in flight keep reading theirs.
/// </summary>
internal sealed class MultiMeshGpu
{
    private readonly IVulkanContext _ctx;
    private MultiMesh? _source;
    private int _version = -1;
    private int _contentVersion = -1;
    private int _uploaded;
    private Matrix4x4 _model;
    private Aabb _meshBounds = Aabb.Empty;
    private uint _objectId;

    public MultiMeshGpu(IVulkanContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>The instances (binding 1), or null when nothing is drawn.</summary>
    public GpuBuffer? Instances { get; private set; }

    /// <summary>Instances in <see cref="Instances"/>.</summary>
    public uint Count { get; private set; }

    /// <summary>World bounds of every drawn instance.</summary>
    public Aabb WorldBounds { get; private set; } = Aabb.Empty;

    /// <summary>Uploads the instances if anything they depend on changed. Returns false when there is nothing to draw.</summary>
    public bool Update(MultiMesh multimesh, in Matrix4x4 model, in Aabb meshBounds, uint objectId)
    {
        if (ReferenceEquals(multimesh, _source) && multimesh.ContentVersion == _contentVersion && model == _model &&
            meshBounds == _meshBounds && objectId == _objectId)
        {
            if (multimesh.Version != _version)
            {
                // Only the drawn count or the custom bounds moved: the buffer already holds every instance.
                _version = multimesh.Version;
                Count = (uint)Math.Min(multimesh.DrawnInstanceCount, _uploaded);
                WorldBounds = multimesh.GetAabb().Transform(model);
            }

            return Instances is not null && Count > 0;
        }

        _source = multimesh;
        _version = multimesh.Version;
        _contentVersion = multimesh.ContentVersion;
        _model = model;
        _meshBounds = meshBounds;
        _objectId = objectId;
        Release();

        var count = multimesh.InstanceCount;
        WorldBounds = multimesh.GetAabb().Transform(model);
        if (count == 0)
            return false;

        var transforms = multimesh.Transforms;
        var data = ArrayPool<MeshInstanceData>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
                data[i] = new MeshInstanceData(transforms[i].ToMatrix4x4() * model, objectId);
            Instances = GpuBuffer.CreateStatic<MeshInstanceData>(_ctx, data.AsSpan(0, count), BufferUsageFlags.VertexBufferBit);
        }
        finally
        {
            ArrayPool<MeshInstanceData>.Shared.Return(data);
        }

        _uploaded = count;
        Count = (uint)multimesh.DrawnInstanceCount;
        return Count > 0;
    }

    /// <summary>Releases the buffer (deferred until frames in flight finish); the next <see cref="Update"/> rebuilds it.</summary>
    public void Release()
    {
        Instances?.Dispose();
        Instances = null;
        Count = 0;
        _uploaded = 0;
    }

    /// <summary>Forgets the source so the next <see cref="Update"/> rebuilds.</summary>
    public void Invalidate()
    {
        Release();
        _source = null;
        _version = -1;
        _contentVersion = -1;
    }
}
