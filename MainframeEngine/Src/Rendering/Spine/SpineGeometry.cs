using System.Numerics;
using Spine;

namespace MainframeEngine;

/// <summary>One drawable attachment of a skeleton, in draw order (<see cref="SpineGeometry"/>).</summary>
public readonly ref struct SpineDrawItem
{
    public SpineDrawItem(ReadOnlySpan<float> vertices, ReadOnlySpan<float> uvs, ReadOnlySpan<int> triangles, Vector4 color,
        AtlasRegion region, Spine.BlendMode blend, int drawIndex, Slot slot)
    {
        Vertices = vertices;
        Uvs = uvs;
        Triangles = triangles;
        Color = color;
        Region = region;
        Blend = blend;
        DrawIndex = drawIndex;
        Slot = slot;
    }

    /// <summary>World positions, x/y pairs (Spine units; Y down when <see cref="Bone.yDown"/>).</summary>
    public ReadOnlySpan<float> Vertices { get; }

    /// <summary>Atlas UVs, one pair per vertex.</summary>
    public ReadOnlySpan<float> Uvs { get; }

    /// <summary>Triangle list into the vertex pairs.</summary>
    public ReadOnlySpan<int> Triangles { get; }

    /// <summary>Skeleton × slot × attachment colour, straight alpha.</summary>
    public Vector4 Color { get; }

    /// <summary>The atlas region (its page's <c>rendererObject</c> is the renderer's texture).</summary>
    public AtlasRegion Region { get; }

    /// <summary>The slot's blend mode.</summary>
    public Spine.BlendMode Blend { get; }

    /// <summary>The slot's position in the draw order.</summary>
    public int DrawIndex { get; }

    public Slot Slot { get; }
}

/// <summary>Receives a skeleton's drawable attachments from <see cref="SpineGeometry.Build{TSink}"/>.</summary>
public interface ISpineGeometrySink
{
    void Add(in SpineDrawItem item);
}

/// <summary>
/// Walks a skeleton's applied draw order the way the official runtimes do (spine-godot's <c>update_meshes</c>, Spine 4.3):
/// skips slots without an attachment or with an inactive bone, resolves region/mesh sequences, multiplies skeleton, slot and
/// attachment colours, and applies clipping attachments with <see cref="SkeletonClipping"/>. Shared by the 3D
/// <see cref="SpineNode"/> renderer and the canvas <see cref="SpineSprite"/>. Allocation-free once its buffers have grown.
/// </summary>
public sealed class SpineGeometry
{
    private static readonly int[] QuadTriangles = [0, 1, 2, 2, 3, 0];
    private readonly SkeletonClipping _clipper = new();
    private float[] _world = new float[64];

    public void Build<TSink>(Skeleton skeleton, TSink sink) where TSink : ISpineGeometrySink
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var drawOrder = skeleton.DrawOrder.AppliedPose;
        var slots = drawOrder.Items;
        var skeletonColor = skeleton.GetColor();
        for (int i = 0, n = drawOrder.Count; i < n; i++)
        {
            var slot = slots[i];
            var pose = slot.AppliedPose;
            var attachment = pose.Attachment;
            if (attachment is null || !slot.Bone.Active)
            {
                _clipper.ClipEnd(slot);
                continue;
            }

            var tint = skeletonColor * pose.GetColor();
            float[] uvs;
            int[] triangles;
            int floats;
            AtlasRegion region;
            switch (attachment)
            {
                case RegionAttachment regionAttachment:
                    {
                        var sequence = regionAttachment.Sequence;
                        var index = sequence.ResolveIndex(pose);
                        regionAttachment.ComputeWorldVertices(slot, sequence.GetOffsets(index), _world, 0, 2);
                        uvs = sequence.GetUVs(index);
                        region = (AtlasRegion)sequence.GetRegion(index);
                        triangles = QuadTriangles;
                        floats = 8;
                        tint *= regionAttachment.GetColor();
                        break;
                    }

                case MeshAttachment mesh:
                    {
                        floats = mesh.WorldVerticesLength;
                        if (floats > _world.Length)
                            Array.Resize(ref _world, Math.Max(floats, _world.Length * 2));
                        mesh.ComputeWorldVertices(skeleton, slot, 0, floats, _world, 0, 2);
                        var sequence = mesh.Sequence;
                        var index = sequence.ResolveIndex(pose);
                        uvs = sequence.GetUVs(index);
                        region = (AtlasRegion)sequence.GetRegion(index);
                        triangles = mesh.Triangles;
                        tint *= mesh.GetColor();
                        break;
                    }

                case ClippingAttachment clip:
                    _clipper.ClipStart(skeleton, slot, clip);
                    continue;
                default:
                    _clipper.ClipEnd(slot);
                    continue;
            }

            var color = new Vector4(tint.r, tint.g, tint.b, tint.a);
            if (_clipper.IsClipping)
            {
                _clipper.ClipTriangles(_world, triangles, triangles.Length, uvs);
                var clipped = _clipper.ClippedTriangles;
                if (clipped.Count == 0)
                {
                    _clipper.ClipEnd(slot);
                    continue;
                }

                var vertices = _clipper.ClippedVertices;
                sink.Add(new SpineDrawItem(vertices.Items.AsSpan(0, vertices.Count), _clipper.ClippedUVs.Items.AsSpan(0, vertices.Count),
                    clipped.Items.AsSpan(0, clipped.Count), color, region, slot.Data.BlendMode, i, slot));
            }
            else
            {
                sink.Add(new SpineDrawItem(_world.AsSpan(0, floats), uvs.AsSpan(0, floats), triangles, color, region, slot.Data.BlendMode, i, slot));
            }

            _clipper.ClipEnd(slot);
        }

        _clipper.ClipEnd();
    }
}
