using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// A render interface without a GPU (headless servers, unit tests): hands out handles and counts what RmlUi asks for,
/// so layout, data binding and input all work and can be tested without Vulkan.
/// </summary>
public sealed class NullUiRenderer : RmlRenderInterface
{
    private ulong _next;
    private readonly HashSet<ulong> _geometry = [];
    private readonly HashSet<ulong> _textures = [];

    public int LiveGeometry => _geometry.Count;
    public int LiveTextures => _textures.Count;

    /// <summary>Geometry draws since <see cref="BeginFrame"/>.</summary>
    public int DrawsThisFrame { get; private set; }

    public void BeginFrame() => DrawsThisFrame = 0;

    protected override ulong CompileGeometry(ReadOnlySpan<RmlVertex> vertices, ReadOnlySpan<int> indices)
    {
        var handle = ++_next;
        _geometry.Add(handle);
        return handle;
    }

    protected override void RenderGeometry(ulong geometry, Vector2 translation, ulong texture) => DrawsThisFrame++;

    protected override void ReleaseGeometry(ulong geometry) => _geometry.Remove(geometry);

    protected override ulong LoadTexture(string source, out int width, out int height)
    {
        width = height = 1;
        var handle = ++_next;
        _textures.Add(handle);
        return handle;
    }

    protected override ulong GenerateTexture(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var handle = ++_next;
        _textures.Add(handle);
        return handle;
    }

    protected override void ReleaseTexture(ulong texture) => _textures.Remove(texture);

    protected override void EnableScissorRegion(bool enable)
    {
    }

    protected override void SetScissorRegion(int x, int y, int width, int height)
    {
    }
}
