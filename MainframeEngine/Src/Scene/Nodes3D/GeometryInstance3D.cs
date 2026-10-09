using DrawingColor = System.Drawing.Color;
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

    /// <summary>
    /// Distance from the camera (to the centre of the world bounds) below which the instance is not drawn, in the main
    /// pass or the shadow maps (Godot's <c>visibility_range_begin</c>, an HLOD distance); 0 = no lower bound.
    /// </summary>
    [ExportGroup("Visibility range")]
    [Export(Range = "0,100000,0.01")]
    public float VisibilityRangeBegin { get; set; }

    /// <summary>Distance from which the instance is no longer drawn (Godot's <c>visibility_range_end</c>); 0 = no upper bound.</summary>
    [Export(Range = "0,100000,0.01")]
    public float VisibilityRangeEnd { get; set; }

    /// <summary>
    /// True when <paramref name="distance"/> lies in [<paramref name="begin"/>, <paramref name="end"/>), a bound of 0
    /// (or less) being open.
    /// </summary>
    public static bool IsInVisibilityRange(float distance, float begin, float end) =>
        (begin <= 0f || distance >= begin) && (end <= 0f || distance < end);

    /// <summary>
    /// Whether the instance is drawn for a camera at <paramref name="cameraPosition"/>, given its world bounds: the
    /// distance to their centre is in [<see cref="VisibilityRangeBegin"/>, <see cref="VisibilityRangeEnd"/>). Instanced
    /// tree levels that choose their trees per instance override it (ADR 0172).
    /// </summary>
    public virtual bool IsInVisibilityRange(Vector3 cameraPosition, in Aabb worldBounds)
    {
        if (VisibilityRangeBegin <= 0f && VisibilityRangeEnd <= 0f)
            return true;
        return IsInVisibilityRange(Vector3.Distance(cameraPosition, worldBounds.Center), VisibilityRangeBegin, VisibilityRangeEnd);
    }

    /// <summary>
    /// Which shadow passes the instance casts into (ADR 0167; default <see cref="MainframeEngine.ShadowCasterLod.All"/>):
    /// <see cref="MainframeEngine.ShadowCasterLod.Fine"/> skips the primary light's coarse passes (its last
    /// <see cref="DirectionalLight.CoarseCascades"/> cascades and its far shadow), <see cref="MainframeEngine.ShadowCasterLod.Coarse"/>
    /// casts into them from any distance (and into the fine ones while in range). Levels of detail use it so far cascades
    /// draw cheap casters.
    /// </summary>
    [Export]
    public ShadowCasterLod ShadowCasterLod { get; set; }

    /// <summary>
    /// How the instance takes part in baked global illumination (Godot's <c>gi_mode</c>; ADR 0170): <see cref="GIMode.Static"/>
    /// (default) instances occlude and bounce light in a <see cref="LightProbeVolume"/> bake; every lit surface, static or
    /// not, samples the probes.
    /// </summary>
    [Export]
    public GIMode GIMode { get; set; } = GIMode.Static;

    /// <summary>
    /// Replaces the node's local bounds (the mesh's, or a multimesh's over all its instances) for frustum culling, shadow
    /// caster culling and the visibility range (Godot's <c>custom_aabb</c>); null: the computed bounds. Levels of detail
    /// share one so they switch at the same distance (ADR 0158). Not saved.
    /// </summary>
    public Aabb? CustomAabb { get; set; }

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

    /// <summary>Last frame's model matrix for motion vectors (ADR 0163), tracked while the depth prepass draws the node.</summary>
    internal MotionHistory Motion;

    protected override void ReleaseRenderResources()
    {
        RenderServer?.ReleaseGeometry(this); // also the instance buffer of a MultiMeshInstance3D
        base.ReleaseRenderResources();
    }
}

/// <summary>How a <see cref="GeometryInstance3D"/> takes part in baked lighting (Godot's <c>GIMode</c>).</summary>
public enum GIMode : byte
{
    /// <summary>Not in the bake (it still receives the probes' light).</summary>
    Disabled,

    /// <summary>Occludes and bounces light in the bake.</summary>
    Static,

    /// <summary>Moves: not in the bake; receives the probes' light.</summary>
    Dynamic,
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
    public DrawingColor Modulate
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    } = DrawingColor.White;

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
