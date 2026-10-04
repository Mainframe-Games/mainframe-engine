using Spine;
using StbImageSharp;

namespace MainframeEngine;

public class SpineTextureLoader : TextureLoader
{
    /// <summary>
    /// Raw RGBA pixel data + dimensions per atlas page, populated for the Vulkan GPU upload. The pixel
    /// arrays are released (<see cref="ReleasePixelData"/>) once the textures are on the GPU; the
    /// dimensions stay.
    /// </summary>
    public List<(byte[] Pixels, int Width, int Height)> VkImageData { get; } = [];

    public void Load(AtlasPage page, string path)
    {
        ArgumentNullException.ThrowIfNull(page);
        // For Vulkan: load raw pixels now (for dimension info + later GPU upload).
        // page.rendererObject stores the index into VkImageData so SpineRenderer
        // can look up which descriptor set to bind per-batch.
        using var stream = File.OpenRead(ContentPaths.Resolve(path));
        var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        int idx = VkImageData.Count;
        VkImageData.Add((img.Data, img.Width, img.Height));
        page.rendererObject = idx;
        page.width = img.Width;
        page.height = img.Height;
    }

    public void Unload(object texture) { }

    /// <summary>Drops the CPU copies of the pixels (after upload); a 2k atlas page is 16 MB.</summary>
    public void ReleasePixelData()
    {
        for (int i = 0; i < VkImageData.Count; i++)
            VkImageData[i] = ([], VkImageData[i].Width, VkImageData[i].Height);
    }
}
