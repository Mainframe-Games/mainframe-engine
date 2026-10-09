using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// One texture layer of a <see cref="TerrainSplatMaterial3D"/> (ADR 0156): a PBR texture set tiled in world space and
/// painted through the terrain's splat weights (the layer's index in <see cref="TerrainSplatMaterial3D.Layers"/> is its
/// splat channel: layers 0–3 in <c>splat-0.png</c>, 4–7 in <c>splat-1.png</c>). Saved inline with the material.
/// </summary>
/// <remarks>
/// <para><b>Height</b> (for height blending) is packed into the alpha of the albedo array: <see cref="Height"/>'s red
/// channel when set; else the albedo's own alpha when it has one (any texel below 255, the common "albedo + height"
/// packing); else the ORM's occlusion (crevices are low); else 0.5 (plain weight blending).</para>
/// <para>Missing textures pack as white albedo (so <see cref="Tint"/> alone colours the layer), a flat normal and an ORM
/// of occlusion 1, roughness 0.9, metallic 0. Setters raise <see cref="Resource.Changed"/>.</para>
/// </remarks>
[EditorIcon("stack-2")]
public sealed class TerrainLayer : Resource
{
    private int _version = 1;

    /// <summary>Changes whenever a property changes.</summary>
    public int Version => _version;

    /// <summary>A display name (the editor's layer list).</summary>
    [Export]
    public string Name { get; set => Set(ref field, value ?? ""); } = "";

    /// <summary>
    /// A game key for the surface (footsteps, friction): <see cref="Terrain3D.SurfaceTagAt"/> returns the tag of the
    /// strongest layer under a point.
    /// </summary>
    [Export]
    public string Tag { get; set => Set(ref field, value ?? ""); } = "";

    /// <summary>Base colour (sRGB); its alpha is the layer's height when it is not fully opaque.</summary>
    [ExportGroup("Textures")]
    [Export]
    public Texture2D? Albedo { get; set => SetReference(ref field, value); }

    /// <summary>Tangent-space normal map (OpenGL/glTF convention: +Y up), linear.</summary>
    [Export]
    public Texture2D? Normal { get; set => SetReference(ref field, value); }

    /// <summary>Occlusion (R), roughness (G), metallic (B): glTF's and Godot's ORM packing, linear.</summary>
    [Export]
    public Texture2D? Orm { get; set => SetReference(ref field, value); }

    /// <summary>Optional height map (R, linear; higher is higher), packed into the albedo array's alpha.</summary>
    [Export]
    public Texture2D? Height { get; set => SetReference(ref field, value); }

    /// <summary>World metres covered by one repeat of the textures.</summary>
    [ExportGroup("Look")]
    [Export(Range = "0.25,64,0.05")]
    public float TilingMeters { get; set => Set(ref field, value); } = 4f;

    /// <summary>
    /// How far the layer's height lets it win against its neighbours where weights blend: 0 = hard height cut-off
    /// (gravel only between the cobbles), 1 = a soft weight blend.
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float HeightBlendContrast { get; set => Set(ref field, value); } = 0.2f;

    /// <summary>Multiplies the albedo (sRGB).</summary>
    [Export]
    public DrawingColor Tint { get; set => Set(ref field, value); } = DrawingColor.White;

    /// <summary>Scales the normal map's slopes (0 = flat).</summary>
    [Export(Range = "0,4,0.01")]
    public float NormalScale { get; set => Set(ref field, value); } = 1f;

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        Touch();
    }

    private void SetReference(ref Texture2D? storage, Texture2D? value)
    {
        if (ReferenceEquals(storage, value))
            return;
        storage = value;
        Touch();
    }

    private void Touch()
    {
        _version++;
        EmitChanged();
    }
}
