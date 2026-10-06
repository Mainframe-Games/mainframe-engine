using System.Drawing;
using System.Text.Json;

namespace MainframeEngine.Serialization;

using Color = System.Drawing.Color;

/// <summary>
/// Upgrades scene entries of node types that no longer exist into their replacements, so older <c>.mscene</c> files
/// keep loading (the type-level counterpart of <see cref="SerializedMigrationAttribute"/>, which can only migrate
/// properties of a type that still exists). An upgrade creates the replacement node, consumes the properties it
/// translated from the bag, and leaves the rest (transform, name-independent common properties) to be applied to
/// the new node as usual. Saving writes the new type.
/// </summary>
/// <remarks>
/// M3 removed <c>Box3d</c> and <c>Quad</c> (lit flat-colour shapes with a pipeline per instance): they become
/// <see cref="MeshInstance3D"/>s with a <see cref="BoxMesh"/> / back-facing <see cref="QuadMesh"/> and a
/// <see cref="StandardMaterial3D"/> carrying their <c>Color</c> (the quad culls nothing, as before).
/// </remarks>
public static class RemovedNodeTypes
{
    private static readonly Dictionary<string, Func<PropertyBag, Node>> Upgrades = new(StringComparer.Ordinal)
    {
        ["Box3d"] = static bag => new MeshInstance3D
        {
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = TakeColor(bag) },
        },
        // The old quad lay in XY facing -Z with culling disabled (no back-face lighting flip).
        ["Quad"] = static bag => new MeshInstance3D
        {
            Mesh = new QuadMesh { FlipFaces = true },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = TakeColor(bag), CullMode = CullMode.Disabled },
        },
    };

    /// <summary>Removed type names that load as their replacement.</summary>
    public static IReadOnlyCollection<string> Names => Upgrades.Keys;

    /// <summary>
    /// Registers an upgrade for a removed node type (games can retire their own types the same way). The function
    /// must remove the properties it consumed from the bag.
    /// </summary>
    public static void Register(string typeName, Func<PropertyBag, Node> upgrade)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(upgrade);
        Upgrades[typeName] = upgrade;
    }

    /// <summary>
    /// Creates the replacement for an entry of removed type <paramref name="typeName"/>; <paramref name="props"/>
    /// keeps the properties still to apply. False when the type is not a known removed type.
    /// </summary>
    public static bool TryUpgrade(string typeName, JsonElement props, out Node? node, out PropertyBag remaining)
    {
        remaining = new PropertyBag(props) { TypeName = typeName };
        if (!Upgrades.TryGetValue(typeName, out var upgrade))
        {
            node = null;
            return false;
        }

        node = upgrade(remaining);
        return true;
    }

    private static Color TakeColor(PropertyBag bag)
    {
        if (!bag.TryGet("Color", out var value))
            return Color.White;
        bag.Remove("Color");
        var c = value.EnumerateArray().Select(e => e.GetSingle()).ToArray();
        static int B(float v) => (int)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
        return c.Length >= 4 ? Color.FromArgb(B(c[3]), B(c[0]), B(c[1]), B(c[2])) : Color.White;
    }
}
