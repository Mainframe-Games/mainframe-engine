using Silk.NET.OpenGL;
using Spine;
using Texture = Mainframe.Silk.Texture;

namespace SilkSpine;

internal class SpineSilkTextureLoader(GL gl) : TextureLoader
{
    public List<Texture> Textures { get; } = [];

    public void Load(AtlasPage page, string path)
    {
        var texture = new Texture(gl, path);
        page.rendererObject = texture;
        page.width = texture.Width;
        page.height = texture.Height;

        Textures.Add(texture);
    }

    public void Unload(object texture) { }
}
