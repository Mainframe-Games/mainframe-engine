using System.Globalization;
using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Editor;

/// <summary>Invariant-culture formatting and parsing of property values for the inspector's text fields.</summary>
public static class ValueText
{
    /// <summary>A number with up to 4 decimals, no trailing zeros ("1", "0.25", "-3.1416").</summary>
    public static string Number(float value) =>
        float.IsFinite(value) ? Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    public static string Number(double value) =>
        double.IsFinite(value) ? Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Any supported value as text (vectors as "x, y, z").</summary>
    public static string Format(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        float f => Number(f),
        double d => Number(d),
        string s => s,
        NodePath p => p.Path,
        Vector2 v => $"{Number(v.X)}, {Number(v.Y)}",
        Vector3 v => $"{Number(v.X)}, {Number(v.Y)}, {Number(v.Z)}",
        Vector4 v => $"{Number(v.X)}, {Number(v.Y)}, {Number(v.Z)}, {Number(v.W)}",
        Quaternion q => $"{Number(q.X)}, {Number(q.Y)}, {Number(q.Z)}, {Number(q.W)}",
        DrawingColor c => ColorHex(c),
        Transform3D t => $"origin ({Format(t.Origin)})",
        Transform2D t => $"origin ({Format(t.Origin)})",
        Resource r => Resource(r),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>A resource slot's label: the file name for external resources, "TypeName (inline)" otherwise, "None" when empty.</summary>
    public static string Resource(Resource? resource) => resource switch
    {
        null => "None",
        { ResourcePath: { } path } => $"{Path.GetFileName(path)} ({resource.GetType().Name})",
        MissingResource missing => $"{missing.OriginalType} (missing type)",
        _ => $"{resource.GetType().Name} (inline)",
    };

    public static bool TryParseFloat(string text, out float value)
    {
        value = 0;
        if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || !float.IsFinite(f))
            return false;
        value = f;
        return true;
    }

    /// <summary>
    /// <c>#rrggbbaa</c> (alpha omitted when opaque) for <see cref="System.Drawing.Color"/> and 0..1 vector colours
    /// (components above 1 — HDR — are clamped in the hex form only).
    /// </summary>
    public static string ColorHex(object? value)
    {
        var rgba = ToRgba(value);
        var r = ToByte(rgba.X);
        var g = ToByte(rgba.Y);
        var b = ToByte(rgba.Z);
        var a = ToByte(rgba.W);
        return a == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}{a:x2}");
    }

    /// <summary>A colour value as 0..1 RGBA.</summary>
    public static Vector4 ToRgba(object? value) => value switch
    {
        DrawingColor c => new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f),
        Vector3 v => new Vector4(v, 1f),
        Vector4 v => v,
        _ => new Vector4(0, 0, 0, 1),
    };

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c> (the <c>#</c> is optional) into 0..1 RGBA.</summary>
    public static bool TryParseColorHex(string text, out Vector4 rgba)
    {
        rgba = default;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 && s.Length != 8)
            return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
            return false;
        if (s.Length == 6)
            bits = (bits << 8) | 0xFF;
        rgba = new Vector4((bits >> 24) / 255f, ((bits >> 16) & 0xFF) / 255f, ((bits >> 8) & 0xFF) / 255f, (bits & 0xFF) / 255f);
        return true;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);
}
