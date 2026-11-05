using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// One decal in the decal UBO (set 0 binding 8, <c>include/decals.slang</c>), std140, 192 bytes: the world → unit-box
/// transform as three columns (local = dot(column.xyz, p) + column.w), the four textures' atlas tiles (zero: none), the
/// linear modulate colour, the fades, the emission energy and distance fade, and the box's X and Y axes in world space.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DecalGpu
{
    public Vector4 ToLocalX, ToLocalY, ToLocalZ;
    public Vector4 AlbedoRect, NormalRect, OrmRect, EmissionRect;
    public Vector4 Modulate;

    /// <summary>x = albedo mix, y = normal fade, z = upper fade, w = lower fade.</summary>
    public Vector4 Fade;

    /// <summary>x = emission energy, y = distance fade (opacity), zw unused.</summary>
    public Vector4 Extra;
    public Vector4 AxisX, AxisY;

    public const int Size = 192;
}

/// <summary>
/// G6.5: the decals one view applies this frame. <see cref="Select"/> keeps the visible decals with something to draw
/// (frustum, distance fade, a texture in the atlas), the <see cref="ShaderLimits.MaxDecals"/> nearest when there are more;
/// <see cref="MaskFor"/> gives an instance the bits of the decals its bounds touch; <see cref="Upload"/> writes the UBO
/// of the frame slot. Every view (main viewport, each sub-viewport) owns one, through its <see cref="MeshViewDraws"/>.
/// </summary>
/// <remarks>Render thread only; allocation-free once its arrays have grown (the GPU buffers are created on first use).</remarks>
internal sealed class DecalSelection
{
    /// <summary>The UBO: a 16-byte header (x = count) and <see cref="ShaderLimits.MaxDecals"/> decals.</summary>
    public const int UboSize = 16 + ShaderLimits.MaxDecals * DecalGpu.Size;

    private readonly Decal[] _selected = new Decal[ShaderLimits.MaxDecals];
    private readonly Aabb[] _bounds = new Aabb[ShaderLimits.MaxDecals];
    private readonly float[] _fade = new float[ShaderLimits.MaxDecals];
    private Decal[] _candidates = new Decal[ShaderLimits.MaxDecals];
    private float[] _candidateDistances = new float[ShaderLimits.MaxDecals];
    private float[] _candidateFades = new float[ShaderLimits.MaxDecals];
    private Aabb[] _candidateBounds = new Aabb[ShaderLimits.MaxDecals];
    private int[] _order = new int[ShaderLimits.MaxDecals];
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];

    /// <summary>Decals selected for the view this frame.</summary>
    public int Count { get; private set; }

    /// <summary>The selected decals, nearest first when the view had more than the limit (else in world order).</summary>
    public ReadOnlySpan<Decal> Selected => _selected.AsSpan(0, Count);

    /// <summary>The atlas the selection's tiles refer to (bound at set 0 binding 9 with this view).</summary>
    public DecalAtlas? Atlas { get; private set; }

    /// <summary>
    /// Picks the view's decals among <paramref name="decals"/>: visible in the tree, inside the frustum, not faded out, with
    /// a texture; the nearest <see cref="ShaderLimits.MaxDecals"/> (by distance from the camera to the box) when more.
    /// </summary>
    public void Select(List<Decal> decals, in Frustum frustum, Vector3 cameraPosition, DecalAtlas? atlas)
    {
        Count = 0;
        Atlas = atlas;
        if (decals.Count == 0)
            return;
        var candidates = 0;
        for (var i = 0; i < decals.Count; i++)
        {
            var decal = decals[i];
            if (!decal.IsVisibleInTree() || !HasTexture(decal))
                continue;
            var bounds = decal.WorldBounds;
            var distance = LodSelection.Distance(cameraPosition, bounds);
            var fade = decal.DistanceFadeAt(distance);
            if (fade <= 0f || !frustum.Intersects(bounds))
                continue;
            if (candidates == _candidates.Length)
                Grow();
            _candidates[candidates] = decal;
            _candidateDistances[candidates] = distance;
            _candidateFades[candidates] = fade;
            _candidateBounds[candidates] = bounds;
            _order[candidates] = candidates;
            candidates++;
        }

        if (candidates > ShaderLimits.MaxDecals)
        {
            // Nearest first (the order of exactly equal distances is unspecified).
            _candidateDistances.AsSpan(0, candidates).Sort(_order.AsSpan(0, candidates));
        }

        Count = Math.Min(candidates, ShaderLimits.MaxDecals);
        for (var k = 0; k < Count; k++)
        {
            var i = _order[k];
            _selected[k] = _candidates[i];
            _bounds[k] = _candidateBounds[i];
            _fade[k] = _candidateFades[i];
        }

        Array.Clear(_candidates, 0, candidates); // drop the references
    }

    private static bool HasTexture(Decal decal) =>
        decal.TextureAlbedo is not null || decal.TextureNormal is not null || decal.TextureOrm is not null || decal.TextureEmission is not null;

    private void Grow()
    {
        var size = _candidates.Length * 2;
        Array.Resize(ref _candidates, size);
        Array.Resize(ref _candidateDistances, size);
        Array.Resize(ref _candidateFades, size);
        Array.Resize(ref _candidateBounds, size);
        Array.Resize(ref _order, size);
    }

    /// <summary>Bits of the selected decals whose boxes' world bounds overlap <paramref name="bounds"/>.</summary>
    public uint MaskFor(in Aabb bounds)
    {
        uint mask = 0;
        for (var k = 0; k < Count; k++)
        {
            ref readonly var d = ref _bounds[k];
            if (d.Min.X <= bounds.Max.X && d.Max.X >= bounds.Min.X && d.Min.Y <= bounds.Max.Y && d.Max.Y >= bounds.Min.Y &&
                d.Min.Z <= bounds.Max.Z && d.Max.Z >= bounds.Min.Z)
                mask |= 1u << k;
        }

        return mask;
    }

    /// <summary>The UBO entry of selected decal <paramref name="k"/>.</summary>
    public DecalGpu Pack(int k)
    {
        var decal = _selected[k];
        var box = decal.BoxMatrix;
        Matrix4x4.Invert(box, out var toLocal);
        var modulate = decal.Modulate;
        return new DecalGpu
        {
            ToLocalX = new Vector4(toLocal.M11, toLocal.M21, toLocal.M31, toLocal.M41),
            ToLocalY = new Vector4(toLocal.M12, toLocal.M22, toLocal.M32, toLocal.M42),
            ToLocalZ = new Vector4(toLocal.M13, toLocal.M23, toLocal.M33, toLocal.M43),
            AlbedoRect = Atlas?.RectOf(decal.TextureAlbedo) ?? default,
            NormalRect = Atlas?.RectOf(decal.TextureNormal) ?? default,
            OrmRect = Atlas?.RectOf(decal.TextureOrm) ?? default,
            EmissionRect = Atlas?.RectOf(decal.TextureEmission) ?? default,
            Modulate = new Vector4(ColorSpace.SrgbToLinear(modulate.R / 255f), ColorSpace.SrgbToLinear(modulate.G / 255f),
                ColorSpace.SrgbToLinear(modulate.B / 255f), modulate.A / 255f),
            Fade = new Vector4(Math.Clamp(decal.AlbedoMix, 0f, 1f), Math.Clamp(decal.NormalFade, 0f, 0.999f),
                Math.Max(decal.UpperFade, 0f), Math.Max(decal.LowerFade, 0f)),
            Extra = new Vector4(Math.Max(decal.EmissionEnergy, 0f), _fade[k], 0f, 0f),
            AxisX = new Vector4(SafeNormalize(new Vector3(box.M11, box.M12, box.M13)), 0f),
            AxisY = new Vector4(SafeNormalize(new Vector3(box.M21, box.M22, box.M23)), 0f),
        };
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 0f ? Vector3.Normalize(v) : Vector3.UnitY;

    /// <summary>The frame slot's UBO (created on first use).</summary>
    public GpuBuffer BufferFor(IVulkanContext ctx, int slot)
    {
        if (_buffers[slot] is { } buffer)
            return buffer;
        buffer = GpuBuffer.Create(ctx, UboSize, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        buffer.MappedSpan.Clear();
        _buffers[slot] = buffer;
        return buffer;
    }

    /// <summary>Writes the selection into the current frame slot's UBO.</summary>
    public void Upload(IVulkanContext ctx)
    {
        var span = BufferFor(ctx, ctx.FrameSlot).MappedSpan;
        MemoryMarshal.Write(span, new Vector4(BitConverter.Int32BitsToSingle(Count), 0f, 0f, 0f));
        var entries = MemoryMarshal.Cast<byte, DecalGpu>(span[16..]);
        for (var k = 0; k < Count; k++)
            entries[k] = Pack(k);
    }

    /// <summary>Releases the UBOs (the mesh renderer does, when it is disposed).</summary>
    public void ReleaseBuffers()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }
}
