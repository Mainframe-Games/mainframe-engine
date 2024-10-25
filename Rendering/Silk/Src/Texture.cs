using Silk.NET.Assimp;
using Silk.NET.OpenGL;
using StbImageSharp;
using File = System.IO.File;

namespace Mainframe.Silk;

public class Texture : IDisposable
{
    private readonly uint _handle;
    private readonly GL _gl;

    public string Path { get; set; }
    public TextureType Type { get; }
    public int Width { get; }
    public int Height { get; }

    public unsafe Texture(GL gl, string path, TextureType type = TextureType.None)
    {
        //Saving the gl instance.
        _gl = gl;
        Path = path;
        Type = type;

        //Generating the opengl handle;
        _handle = _gl.GenTexture();
        Bind();

        // Load the image from memory.
        ImageResult result = ImageResult.FromMemory(
            File.ReadAllBytes(path),
            ColorComponents.RedGreenBlueAlpha
        );
        
        Width = result.Width;
        Height = result.Height;

        fixed (byte* ptr = result.Data)
        {
            // Create our texture and upload the image data.
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba,
                (uint)result.Width,
                (uint)result.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ptr
            );
        }

        SetParameters();
    }

    private void SetParameters()
    {
        //Setting some texture perameters so the texture behaves as expected.
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge
        );
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge
        );
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.LinearMipmapLinear
        );
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Linear
        );
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 8);

        //Generating mipmaps.
        _gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    public void Bind(TextureUnit textureSlot = TextureUnit.Texture0)
    {
        //When we bind a texture we can choose which textureslot we can bind it to.
        _gl.ActiveTexture(textureSlot);
        _gl.BindTexture(TextureTarget.Texture2D, _handle);
    }

    public void Dispose()
    {
        //In order to dispose we need to delete the opengl handle for the texure.
        _gl.DeleteTexture(_handle);
    }
}
