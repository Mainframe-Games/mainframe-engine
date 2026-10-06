using System.Drawing;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of visuals drawn from a <see cref="Mesh"/> and <see cref="Material"/>s (Godot's <c>GeometryInstance3D</c>).
/// They do not draw themselves: the render server batches every instance in the world by pipeline, material and
/// mesh into instanced draws (opaque front-to-back by state, transparent back to front), culls them against the
/// camera frustum, draws them into the shadow maps and the object-ID pass.
/// </summary>
[EditorIcon("cube-unfolded")]
public abstract class GeometryInstance3D : VisualInstance3D
{
    /// <summary>Replaces the material of every surface (null: each surface's own material).</summary>
    [Export]
    public Material? MaterialOverride { get; set; }

    /// <summary>
    /// Drawn over every surface after its own material (Godot's <c>material_overlay</c>), with its
    /// <see cref="Material.NextPass"/> chain: highlights, outlines. Overlay draws test depth less-or-equal, so they land
    /// exactly on the surface; they cast no shadows and are not picked.
    /// </summary>
    [Export]
    public Material? MaterialOverlay { get; set; }

    internal override bool IsBatched => true;

    /// <summary>The mesh to draw this frame (null or empty: nothing).</summary>
    internal abstract Mesh? GetRenderMesh();

    /// <summary>The material of surface <paramref name="surface"/>: the override, else the mesh's, else the default.</summary>
    internal virtual Material GetRenderMaterial(Mesh mesh, int surface) =>
        MaterialOverride ?? mesh.GetSurfaceMaterial(surface) ?? StandardMaterial3D.Default;

    /// <summary>Object id written by the ID pass: the node's <see cref="Node.Id"/> (0 is "nothing").</summary>
    public uint ObjectId => (uint)(ulong)Id;

    // ── Render-server state (render thread) ────────────────────────────────────
    // What the node's GPU references were resolved from; any change re-resolves them (MeshRenderer.Sync).
    internal Mesh? ResolvedMesh;
    internal int ResolvedMeshGeneration = -1;
    internal Material? ResolvedOverride;
    internal int ResolvedStamp;
    internal MeshGpu? GpuMesh;
    internal MaterialGpu?[] GpuMaterials = [];
    internal Material? ResolvedOverlay;
    internal int ResolvedChainGeneration;

    /// <summary>Extra passes: each surface material's next-pass chain, then the overlay chain over every surface.</summary>
    internal MeshExtraPass[] GpuExtraPasses = [];

    /// <summary>Bumped by subclasses when their generated mesh or materials change.</summary>
    internal int RenderStamp;

    protected override void ReleaseRenderResources()
    {
        RenderServer?.ReleaseGeometry(this);
        base.ReleaseRenderResources();
    }
}

/// <summary>Draws a <see cref="MainframeEngine.Mesh"/> (Godot's <c>MeshInstance3D</c>): primitives, imported models, procedural geometry.</summary>
[EditorIcon("cube")]
public class MeshInstance3D : GeometryInstance3D
{
    [Export]
    public Mesh? Mesh { get; set; }

    internal override Mesh? GetRenderMesh() => Mesh;
}

/// <summary>How a <see cref="Sprite3D"/> treats its texture's alpha.</summary>
public enum SpriteAlphaCut : byte
{
    /// <summary>Alpha blended, sorted back to front.</summary>
    Disabled,

    /// <summary>Alpha tested at 0.5: opaque pass, casts cutout-free shadows.</summary>
    Discard,
}

/// <summary>
/// A textured quad in the XY plane facing +Z, sized from the texture (Godot's <c>Sprite3D</c>): one world unit per
/// <see cref="PixelSize"/>⁻¹ pixels. Unshaded and double-sided by default, like Godot. Drawn through the mesh
/// batcher, but each sprite owns its quad mesh and material (one draw per sprite; share a
/// <see cref="MeshInstance3D"/> with a <see cref="QuadMesh"/> and material to batch many). Billboarding is not
/// supported yet.
/// </summary>
[EditorIcon("photo")]
public class Sprite3D : GeometryInstance3D
{
    private readonly QuadMesh _quad = new();
    private readonly StandardMaterial3D _material = new() { DoubleSided = true, ShadingMode = ShadingMode.Unshaded, Transparency = AlphaMode.Blend };
    private int _builtTextureVersion = -1;
    private Texture2D? _builtTexture;

    [Export]
    public Texture2D? Texture
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    }

    /// <summary>World units per texture pixel.</summary>
    [Export(Range = "0.0001,1,0.0001")]
    public float PixelSize
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    } = 0.01f;

    /// <summary>Tint (sRGB) multiplied with the texture.</summary>
    [Export]
    public Color Modulate
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    } = Color.White;

    /// <summary>Lit by the scene's lights instead of unshaded.</summary>
    [Export]
    public bool Shaded
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    }

    [Export]
    public bool DoubleSided
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    } = true;

    [Export]
    public SpriteAlphaCut AlphaCut
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    }

    /// <summary>Shifts the quad (pixels × <see cref="PixelSize"/>); the default centres it.</summary>
    [Export]
    public Vector2 Offset
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    }

    internal override Mesh? GetRenderMesh()
    {
        if (Texture is not { } texture)
            return null;
        Sync(texture);
        return _quad;
    }

    internal override Material GetRenderMaterial(Mesh mesh, int surface) => MaterialOverride ?? _material;

    private void Sync(Texture2D texture)
    {
        if (ReferenceEquals(texture, _builtTexture) && texture.Version == _builtTextureVersion && _quad.Size != default
            && _syncedStamp == RenderStamp)
            return;
        _builtTexture = texture;
        _builtTextureVersion = texture.Version;
        _syncedStamp = RenderStamp;
        _quad.Size = new Vector2(texture.Width, texture.Height) * PixelSize;
        _quad.CenterOffset = new Vector3(Offset * PixelSize, 0f);
        _material.AlbedoTexture = texture;
        _material.AlbedoColor = Modulate;
        _material.ShadingMode = Shaded ? ShadingMode.BlinnPhong : ShadingMode.Unshaded;
        _material.DoubleSided = DoubleSided;
        _material.Transparency = AlphaCut == SpriteAlphaCut.Discard ? AlphaMode.Cutout : AlphaMode.Blend;
    }

    private int _syncedStamp = -1;
}
