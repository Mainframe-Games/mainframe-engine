using Spine;
using StbImageSharp;

namespace MainframeEngine;

public class SpineTextureLoader : TextureLoader
{
    private readonly IRenderer _renderer;

    /// <summary>OpenGL textures — populated when backend is OpenGL.</summary>
    public List<Texture> GlTextures { get; } = [];

    /// <summary>Raw RGBA pixel data + dimensions — populated when backend is Vulkan.</summary>
    public List<(byte[] Pixels, int Width, int Height)> VkImageData { get; } = [];

    public SpineTextureLoader(IRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Load(AtlasPage page, string path)
    {
        if (_renderer.Backend == RenderingBackend.OpenGL)
        {
            var texture = new Texture(_renderer.GetGL(), path);
            page.rendererObject = texture;
            page.width = texture.Width;
            page.height = texture.Height;
            GlTextures.Add(texture);
        }
        else
        {
            // For Vulkan: load raw pixels now (for dimension info + later GPU upload).
            // page.rendererObject stores the index into VkImageData so SpineRenderer
            // can look up which descriptor set to bind per-batch.
            using var stream = File.OpenRead(path);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            int idx = VkImageData.Count;
            VkImageData.Add((img.Data, img.Width, img.Height));
            page.rendererObject = idx;
            page.width = img.Width;
            page.height = img.Height;
        }
    }

    public void Unload(object texture) { }
}
