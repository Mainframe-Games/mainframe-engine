using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The canvas vertex attributes (<see cref="CanvasVertex"/>: 0 position, 1 uv, 2 colour). A pipeline declares only the
/// ones its vertex shader reads: Slang drops unread inputs (a translated shader whose fragment stage ignores the colour
/// stops reading location 2), and an attribute the shader does not read is a validation warning (ADR 0144).
/// </summary>
internal static class CanvasVertexLayout
{
    public const int AttributeCount = 3;

    /// <summary>Writes the attributes whose location is in <paramref name="shaderInputs"/> to <paramref name="into"/>; returns how many.</summary>
    public static int Select(IReadOnlySet<int> shaderInputs, Span<VertexInputAttributeDescription> into)
    {
        var count = 0;
        if (shaderInputs.Contains(0))
            into[count++] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
        if (shaderInputs.Contains(1))
            into[count++] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 8);
        if (shaderInputs.Contains(2))
            into[count++] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32A32Sfloat, 16);
        return count;
    }
}
