using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// Canvas pipelines declare only the vertex attributes their vertex shader reads: a translated shader whose fragment stage
/// ignores the colour stops reading location 2 (Slang drops unread inputs), and an unread attribute is a validation
/// warning (ADR 0144).
/// </summary>
public sealed class CanvasVertexLayoutTests
{
    [Fact]
    public void AttributesFollowTheShadersInputs()
    {
        Span<VertexInputAttributeDescription> into = stackalloc VertexInputAttributeDescription[CanvasVertexLayout.AttributeCount];

        var all = CanvasVertexLayout.Select(new HashSet<int> { 0, 1, 2 }, into);
        Assert.Equal(3, all);
        Assert.Equal([0u, 1u, 2u], into[..all].ToArray().Select(a => a.Location));
        Assert.Equal(16u, into[2].Offset);

        var noColour = CanvasVertexLayout.Select(new HashSet<int> { 0, 1 }, into);
        Assert.Equal(2, noColour);
        Assert.Equal([0u, 1u], into[..noColour].ToArray().Select(a => a.Location));
        Assert.Equal(8u, into[1].Offset);
    }
}
