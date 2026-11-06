namespace MainframeEngine.Editor;

/// <summary>
/// What a node made in the editor (Add Node) starts with beyond its constructor, which must stay cheap and free of side
/// effects: a <see cref="Path2D"/> gets an inline <see cref="Curve2D"/> so the curve tool can draw at once, and a
/// <see cref="Path3D"/> an inline <see cref="Curve3D"/> with up vectors (G13), and a <see cref="Tree3D"/> an inline copy of
/// the <see cref="DefaultTreePreset"/> preset, so it grows at once and its options can be edited (G8b.3).
/// </summary>
public static class NewNodeDefaults
{
    /// <summary>The preset a new <see cref="Tree3D"/> starts from.</summary>
    public const string DefaultTreePreset = "Oak Medium";

    public static void Apply(Node node)
    {
        switch (node)
        {
            case Path2D { Curve: null } path2D:
                path2D.Curve = new Curve2D();
                break;
            case Path3D { Curve: null } path3D:
                path3D.Curve = new Curve3D { UpVectorEnabled = true };
                break;
            case Tree3D { Options: null, Preset.Length: 0, BakedMesh: null } tree:
                tree.Options = TreePresets.Load(DefaultTreePreset);
                tree.Preset = tree.Options.ResourceName;
                break;
        }
    }
}
