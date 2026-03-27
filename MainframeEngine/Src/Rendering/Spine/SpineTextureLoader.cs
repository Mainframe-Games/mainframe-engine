using Spine;
using StbImageSharp;

namespace MainframeEngine;

public class SpineTextureLoader : TextureLoader
{
    /// <summary>Raw RGBA pixel data + dimensions — populated for Vulkan GPU upload.</summary>
    public List<(byte[] Pixels, int Width, int Height)> VkImageData { get; } = [];

    public void Load(AtlasPage page, string path)
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

    public void Unload(object texture) { }
}
