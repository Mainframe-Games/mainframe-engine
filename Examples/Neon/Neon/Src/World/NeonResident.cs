using MainframeEngine;

namespace Neon;

/// <summary>
/// The woman on the loft's bed (<c>Tools/apartment/build_resident.py</c>): a skinned model that loops its idle (she breathes,
/// turns her head, shifts her raised knee, flexes a foot). The model is imported with its skeleton, textures and clip; this
/// node gives its surfaces PBR shading by material name and starts the clip. She is not part of the probe bake and has no
/// collision: the bed's box covers her.
/// </summary>
public sealed class NeonResident : Node3D
{
    public const string ModelPath = "Assets/Art/Apartment/resident.gltf";

    /// <summary>The binary buffer (LFS): she is skipped when it is a pointer.</summary>
    public const string BufferPath = "Assets/Art/Apartment/resident.bin";

    /// <summary>The name <see cref="ModelPath"/>'s clip imports under (the <c>-loop</c> suffix sets its loop mode).</summary>
    public const string IdleClip = "idle";

    /// <summary>Her animation player, or null when the art is not checked out.</summary>
    public AnimationPlayer? Player { get; private set; }

    /// <summary>Her skeleton, or null when the art is not checked out.</summary>
    public Skeleton3D? Skeleton { get; private set; }

    protected override void OnReady()
    {
        if (!ArtIsHere())
            return;
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        Node model;
        try
        {
            model = scene.Instantiate<Node>();
        }
        finally
        {
            scene.Release();
        }

        AddChild(model);
        Skeleton = model.FindChildren<Skeleton3D>(owned: false).FirstOrDefault();
        Player = model.FindChildren<AnimationPlayer>(owned: false).FirstOrDefault();
        foreach (var instance in model.FindChildren<MeshInstance3D>(owned: false))
        {
            instance.GIMode = GIMode.Disabled; // she moves: not part of the bake
            if (instance.Mesh is { } mesh)
                for (var s = 0; s < mesh.SurfaceCount; s++)
                    if (mesh.GetSurface(s) is { Material: { } material } surface)
                        surface.Material = ToPbr(material);
        }

        Player?.Play(IdleClip);
    }

    private static bool ArtIsHere()
    {
        var path = AssetDatabase.Current.ToAbsolutePath(BufferPath);
        if (!File.Exists(path))
            return false;
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[7];
        return file.Read(head) < 7 || !head.SequenceEqual("version"u8);
    }

    private static Material ToPbr(Material source)
    {
        if (source is not StandardMaterial3D imported)
            return source;
        return new StandardMaterial3D
        {
            ResourceName = imported.ResourceName,
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = imported.AlbedoColor,
            AlbedoTexture = imported.AlbedoTexture,
            NormalTexture = imported.NormalTexture,
            Transparency = imported.Transparency,
            AlphaCutoff = imported.AlphaCutoff,
            DoubleSided = imported.DoubleSided,
            Metallic = 0f,
            Roughness = imported.ResourceName switch
            {
                "Skin" => 0.52f,
                "Lingerie" => 0.52f,
                "Stocking" => 0.6f,
                "Hair" => 0.62f,
                "Eye" => 0.1f,
                _ => 0.6f,
            },
            EmissionColor = imported.EmissionColor,
            EmissionEnergy = imported.EmissionEnergy,
        };
    }
}
