using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// A projected texture (Godot's <c>Decal</c>, G6.5): albedo, normal, ORM and emission projected along the node's local −Y
/// through a box of <see cref="Size"/> onto opaque <see cref="StandardMaterial3D"/> surfaces (Blinn-Phong and PBR) and
/// terrain, before they are lit. Forward decals: each view applies the <see cref="ShaderLimits.MaxDecals"/> nearest
/// visible decals, and every surface instance tests only the decals its bounds touch.
/// </summary>
/// <remarks>
/// Textures are packed into one shared atlas (rebuilt when a decal shows a texture that is not in it yet). Transparent
/// surfaces, particles, foliage, water, spatial shaders, <see cref="MultiMeshInstance3D"/> instances, Spine and the sky
/// receive none. Godot's <c>cull_mask</c> is not built (3D visuals have no render layers).
/// </remarks>
[EditorIcon("sticker")]
public class Decal : Node3D
{
    private World3D? _world;

    /// <summary>The projection box in local units: X and Z span the texture, Y is the projection depth (Godot's <c>size</c>).</summary>
    [Export]
    public Vector3 Size { get; set; } = new(2, 2, 2);

    /// <summary>The colour texture (sRGB, alpha = coverage); null: only the other maps apply.</summary>
    [ExportGroup("Textures")]
    [Export]
    public Texture2D? TextureAlbedo { get; set; }

    /// <summary>A tangent-space normal map in the decal's plane (X right, Z down the texture).</summary>
    [Export]
    public Texture2D? TextureNormal { get; set; }

    /// <summary>Occlusion (R), roughness (G) and metallic (B), glTF's packing; PBR surfaces only.</summary>
    [Export]
    public Texture2D? TextureOrm { get; set; }

    /// <summary>The emission texture (sRGB), added after lighting.</summary>
    [Export]
    public Texture2D? TextureEmission { get; set; }

    /// <summary>Multiplies <see cref="TextureEmission"/> (Godot's <c>emission_energy</c>).</summary>
    [ExportGroup("Parameters")]
    [Export(Range = "0,16,0.01")]
    public float EmissionEnergy { get; set; } = 1f;

    /// <summary>Tints the albedo and emission and scales the coverage (alpha); sRGB-authored.</summary>
    [Export]
    public DrawingColor Modulate { get; set; } = DrawingColor.White;

    /// <summary>How much the albedo replaces the surface's (Godot's <c>albedo_mix</c>; the normal, ORM and emission still apply at 0).</summary>
    [Export(Range = "0,1,0.01")]
    public float AlbedoMix { get; set; } = 1f;

    /// <summary>
    /// Fades the decal on surfaces turned away from its projection (Godot's <c>normal_fade</c>): 0 projects onto every
    /// angle, higher values keep it to surfaces facing up the projection axis.
    /// </summary>
    [Export(Range = "0,0.999,0.001")]
    public float NormalFade { get; set; }

    /// <summary>The curve of the fade towards the box's top face (+Y; Godot's <c>upper_fade</c>); 0 is no fade.</summary>
    [Export(Range = "0,1,0.01")]
    public float UpperFade { get; set; } = 0.3f;

    /// <summary>The curve of the fade towards the box's bottom face (−Y; Godot's <c>lower_fade</c>); 0 is no fade.</summary>
    [Export(Range = "0,1,0.01")]
    public float LowerFade { get; set; } = 0.3f;

    /// <summary>Fades the decal out with distance from the camera (Godot's <c>distance_fade_enabled</c>).</summary>
    [ExportGroup("Distance fade")]
    [Export]
    public bool DistanceFadeEnabled { get; set; }

    /// <summary>Distance from the camera where the fade starts.</summary>
    [Export(Range = "0,4096,0.01")]
    public float DistanceFadeBegin { get; set; } = 40f;

    /// <summary>Length of the fade: past <see cref="DistanceFadeBegin"/> + this the decal is not drawn.</summary>
    [Export(Range = "0,4096,0.01")]
    public float DistanceFadeLength { get; set; } = 10f;

    /// <summary>
    /// The decal's opacity at <paramref name="distance"/> from the camera: 1 without a distance fade, 0 at or past the
    /// fade's end.
    /// </summary>
    public float DistanceFadeAt(float distance)
    {
        if (!DistanceFadeEnabled)
            return 1f;
        if (distance <= DistanceFadeBegin)
            return 1f;
        if (!(DistanceFadeLength > 0f))
            return 0f;
        return Math.Clamp(1f - (distance - DistanceFadeBegin) / DistanceFadeLength, 0f, 1f);
    }

    /// <summary>The world matrix of the projection box: the unit box [−0.5, 0.5]³ scaled to <see cref="Size"/>, then placed.</summary>
    public Matrix4x4 BoxMatrix => Matrix4x4.CreateScale(SafeSize) * ModelMatrix;

    /// <summary>The projection box's world bounds.</summary>
    public Aabb WorldBounds => new Aabb(new Vector3(-0.5f), new Vector3(0.5f)).Transform(BoxMatrix);

    // A zero extent would make the box singular: keep a sliver.
    private Vector3 SafeSize => Vector3.Max(Vector3.Abs(Size), new Vector3(1e-4f));

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.AddDecal(this);
    }

    protected override void OnExitTree()
    {
        _world?.RemoveDecal(this);
        _world = null;
        base.OnExitTree();
    }
}
