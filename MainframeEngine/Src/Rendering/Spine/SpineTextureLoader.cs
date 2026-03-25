using Spine;

namespace MainframeEngine;

public class SpineTextureLoader : TextureLoader
{
    private readonly IRenderer _renderer;

    public List<Texture> Textures { get; } = [];

    public SpineTextureLoader(IRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Load(AtlasPage page, string path)
    {
        var texture = new Texture(_renderer.GetGL(), path);
        page.rendererObject = texture;
        page.width = texture.Width;
        page.height = texture.Height;
        Textures.Add(texture);
    }

    public void Unload(object texture) { }
}
