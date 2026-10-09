namespace MainframeEngine;

/// <summary>
/// Many copies of one <see cref="MainframeEngine.Mesh"/>, each with its own transform (Godot's <c>MultiMesh</c>,
/// transforms only), drawn by a <see cref="MultiMeshInstance3D"/> as one render item: one instanced draw per surface
/// from a persistent GPU instance buffer that is rewritten only when the transforms change (foliage, scatter).
/// </summary>
/// <remarks>
/// Transforms are in the instance node's space. <see cref="InstanceCount"/> sizes the buffer (resizing resets every
/// transform to identity); <see cref="VisibleInstanceCount"/> draws only the first instances. Set many transforms with
/// <see cref="SetTransforms"/>: each setter bumps <see cref="Version"/>, which the renderer checks once per frame.
/// </remarks>
[EditorIcon("stack-2")]
public sealed class MultiMesh : Resource
{
    private Transform3D[] _transforms = [];
    private int _version = 1;
    private int _boundsVersion;
    private int _boundsMeshVersion;
    private Mesh? _boundsMesh;
    private Aabb _bounds = Aabb.Empty;

    /// <summary>The mesh every instance draws.</summary>
    [Export]
    public Mesh? Mesh
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Number of instances. Changing it resets every transform to identity (Godot clears the buffer too).</summary>
    [Export(Range = "0,10000000,1")]
    public int InstanceCount
    {
        get => _transforms.Length;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value == _transforms.Length) return;
            _transforms = new Transform3D[value];
            _transforms.AsSpan().Fill(Transform3D.Identity);
            Touch();
        }
    }

    /// <summary>How many instances are drawn, from the first (-1 = all; clamped to <see cref="InstanceCount"/>).</summary>
    [Export(Range = "-1,10000000,1")]
    public int VisibleInstanceCount
    {
        get;
        set
        {
            value = Math.Max(-1, value);
            if (field == value) return;
            field = value;
            Touch();
        }
    } = -1;

    /// <summary>
    /// Every instance's transform (Godot's <c>buffer</c>, as transforms). Assigning sets <see cref="InstanceCount"/> to
    /// the array's length; after editing the array in place call <see cref="NotifyChanged"/>.
    /// </summary>
    [Export]
    public Transform3D[] Transforms
    {
        get => _transforms;
        set
        {
            _transforms = value ?? [];
            Touch();
        }
    }

    /// <summary>The instances drawn: <see cref="VisibleInstanceCount"/>, or all.</summary>
    public int DrawnInstanceCount => VisibleInstanceCount < 0 ? _transforms.Length : Math.Min(VisibleInstanceCount, _transforms.Length);

    /// <summary>Changes whenever the mesh reference, the count or a transform changes.</summary>
    public int Version => _version;

    public void SetInstanceTransform(int index, in Transform3D transform)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_transforms.Length, nameof(index));
        _transforms[index] = transform;
        Touch();
    }

    public Transform3D GetInstanceTransform(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_transforms.Length, nameof(index));
        return _transforms[index];
    }

    /// <summary>Copies <paramref name="transforms"/> into instances <paramref name="start"/> onwards (one version bump).</summary>
    public void SetTransforms(ReadOnlySpan<Transform3D> transforms, int start = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (start > _transforms.Length || transforms.Length > _transforms.Length - start)
            throw new ArgumentOutOfRangeException(nameof(transforms),
                $"{transforms.Length} transforms from {start} do not fit {_transforms.Length} instances.");
        transforms.CopyTo(_transforms.AsSpan(start));
        Touch();
    }

    /// <summary>Call after modifying <see cref="Transforms"/> in place.</summary>
    public void NotifyChanged() => Touch();

    /// <summary>
    /// The bounds of the drawn instances in the instance node's space: the mesh's bounds under each transform
    /// (<see cref="Aabb.Empty"/> without a mesh or instances). Cached until the transforms or the mesh change.
    /// </summary>
    public Aabb GetAabb()
    {
        var mesh = Mesh;
        var meshVersion = mesh?.Version ?? 0;
        if (_boundsVersion == _version && ReferenceEquals(_boundsMesh, mesh) && _boundsMeshVersion == meshVersion)
            return _bounds;

        var bounds = Aabb.Empty;
        if (mesh is not null)
        {
            var local = mesh.Bounds;
            var count = DrawnInstanceCount;
            for (var i = 0; i < count; i++)
                bounds = bounds.Merge(local.Transform(_transforms[i].ToMatrix4x4()));
        }

        _bounds = bounds;
        _boundsVersion = _version;
        _boundsMesh = mesh;
        _boundsMeshVersion = meshVersion;
        return bounds;
    }

    private void Touch()
    {
        _version++;
        EmitChanged();
    }
}
