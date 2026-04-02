using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace MainframeEngine.Utils;

/// <summary>
/// Provides extension methods for the <see cref="System.Drawing.Color"/> struct,
/// adding functionality for conversions and integrations with ImGui.
/// </summary>
public static class ColorExtensions
{
    /// <summary>
    /// Converts a <see cref="Color"/> to an unsigned integer representation
    /// compatible with ImGui's color format.
    /// </summary>
    /// <param name="color">The color to convert.</param>
    /// <returns>An unsigned integer representing the color in ImGui's format.</returns>
    public static uint ToImColor(this Color color)
    {
        var v4 = new Vector4(color.R, color.G, color.B, color.A) / 255f;
        return ImGui.ColorConvertFloat4ToU32(v4);
    }
}