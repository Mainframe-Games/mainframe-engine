using System.Numerics;
using System.Text.Json;
using MainframeEngine.Trees;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// Imports Ez Tree's JSON (its presets and the files its web app saves with <b>Save Preset</b>) as a
/// <see cref="TreeOptions"/>, so a tree tuned in the browser imports with the same shape. Keys missing from the JSON keep
/// Ez Tree's defaults, as <c>options.copy</c> does; numbers are read as doubles, unchanged.
/// </summary>
/// <remarks>
/// Mapping: <c>"0"</c>…<c>"3"</c> under each <c>branch.&lt;x&gt;</c> → <see cref="TreeOptions.Level"/>[0..3];
/// <c>"deciduous"</c>/<c>"evergreen"</c> → <see cref="TreeType"/>; <c>"single"</c>/<c>"double"</c> →
/// <see cref="TreeBillboard"/>; integer tints (<c>0xRRGGBB</c>) → opaque colours; <c>bark.type</c>/<c>leaves.type</c> →
/// <see cref="TreeOptions.BarkTexture"/>/<see cref="TreeOptions.LeafTexture"/>; <c>alphaTest</c> →
/// <see cref="TreeOptions.LeafAlphaCutoff"/>. <c>bark.flatShading</c> and the trellis are not ported (a trellis that is
/// enabled is reported and ignored). The engine-only fields keep their defaults (<see cref="TreeOptions.Scale"/> 0.3).
/// </remarks>
public static class EzTreeJson
{
    /// <summary>Reads Ez Tree JSON text.</summary>
    public static TreeOptions Read(string json, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return Read(document.RootElement, name);
    }

    /// <summary>Reads an Ez Tree JSON object.</summary>
    public static TreeOptions Read(JsonElement root, string? name = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Ez Tree options must be a JSON object.");

        var options = new TreeOptions { ResourceName = name ?? string.Empty };
        var levels = TreeLevel.EzTreeDefaults();

        if (Number(root, "seed") is { } seed)
            options.Seed = checked((int)seed);
        if (String(root, "type") is { } type)
            options.Type = ParseType(type);

        if (Object(root, "bark") is { } bark)
        {
            if (String(bark, "type") is { } barkType)
                options.BarkTexture = barkType;
            if (Number(bark, "tint") is { } tint)
                options.BarkTint = Tint(tint);
            if (Bool(bark, "textured") is { } textured)
                options.BarkTextured = textured;
            if (Object(bark, "textureScale") is { } textureScale)
                options.BarkTextureScale = new Vector2(
                    (float)(Number(textureScale, "x") ?? options.BarkTextureScale.X),
                    (float)(Number(textureScale, "y") ?? options.BarkTextureScale.Y));
        }

        if (Object(root, "branch") is { } branch)
        {
            if (Number(branch, "levels") is { } levelCount)
                options.Levels = (int)levelCount;

            ReadLevels(branch, "angle", levels, static (l, v) => l.Angle = v);
            ReadLevels(branch, "children", levels, static (l, v) => l.Children = (int)v);
            ReadLevels(branch, "gnarliness", levels, static (l, v) => l.Gnarliness = v);
            ReadLevels(branch, "length", levels, static (l, v) => l.Length = v);
            ReadLevels(branch, "radius", levels, static (l, v) => l.Radius = v);
            ReadLevels(branch, "sections", levels, static (l, v) => l.Sections = (int)v);
            ReadLevels(branch, "segments", levels, static (l, v) => l.Segments = (int)v);
            ReadLevels(branch, "start", levels, static (l, v) => l.Start = v);
            ReadLevels(branch, "taper", levels, static (l, v) => l.Taper = v);
            ReadLevels(branch, "twist", levels, static (l, v) => l.Twist = v);

            if (Object(branch, "force") is { } force)
            {
                if (Object(force, "direction") is { } direction)
                    options.GrowthDirection = new Vector3(
                        (float)(Number(direction, "x") ?? 0),
                        (float)(Number(direction, "y") ?? 1),
                        (float)(Number(direction, "z") ?? 0));
                if (Number(force, "strength") is { } strength)
                    options.GrowthForce = strength;
            }
        }

        if (Object(root, "leaves") is { } leaves)
        {
            if (String(leaves, "type") is { } leafType)
                options.LeafTexture = leafType;
            if (String(leaves, "billboard") is { } billboard)
                options.LeafBillboard = ParseBillboard(billboard);
            if (Number(leaves, "angle") is { } angle)
                options.LeafAngle = angle;
            if (Number(leaves, "count") is { } count)
                options.LeafCount = (int)count;
            if (Number(leaves, "start") is { } start)
                options.LeafStart = start;
            if (Number(leaves, "size") is { } size)
                options.LeafSize = size;
            if (Number(leaves, "sizeVariance") is { } variance)
                options.LeafSizeVariance = variance;
            if (Number(leaves, "tint") is { } tint)
                options.LeafTint = Tint(tint);
            if (Number(leaves, "alphaTest") is { } alphaTest)
                options.LeafAlphaCutoff = (float)alphaTest;
            if (Bool(leaves, "roundedNormals") is { } rounded)
                options.LeafRoundedNormals = rounded;
        }

        if (Object(root, "trellis") is { } trellis && Bool(trellis, "enabled") == true)
            Log.Warning($"[Trees] '{name ?? "Ez Tree JSON"}' enables Ez Tree's trellis, which is not ported; it is ignored.");

        options.Level = levels;
        return options;
    }

    /// <summary>Ez Tree's integer colour (<c>0xRRGGBB</c>) as an opaque colour.</summary>
    public static DrawingColor Tint(double value)
    {
        var rgb = (int)value & 0xFFFFFF;
        return DrawingColor.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    }

    private static void ReadLevels(JsonElement branch, string key, TreeLevel[] levels, Action<TreeLevel, double> set)
    {
        if (Object(branch, key) is not { } values)
            return;
        foreach (var property in values.EnumerateObject())
        {
            if (int.TryParse(property.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level)
                && level is >= 0 and < TreeParams.LevelCount && property.Value.ValueKind == JsonValueKind.Number)
                set(levels[level], property.Value.GetDouble());
        }
    }

    private static TreeType ParseType(string value) => value.ToLowerInvariant() switch
    {
        "deciduous" => TreeType.Deciduous,
        "evergreen" => TreeType.Evergreen,
        _ => throw new JsonException($"Unknown Ez Tree type '{value}' (deciduous or evergreen)."),
    };

    private static TreeBillboard ParseBillboard(string value) => value.ToLowerInvariant() switch
    {
        "single" => TreeBillboard.Single,
        "double" => TreeBillboard.Double,
        _ => throw new JsonException($"Unknown Ez Tree billboard '{value}' (single or double)."),
    };

    private static JsonElement? Object(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static double? Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? String(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
