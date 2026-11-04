using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// A code-created <see cref="Texture2D"/> whose pixels change every frame (<see cref="Texture2D.SetPixels(ReadOnlySpan{byte})"/>,
/// as a video preview does): one 64×64 sprite filling a 64×64 canvas, set to pure red, green and blue in turn, one colour
/// per frame. The canvas renderer writes the new pixels into the same GPU image (<see cref="GpuTexture.Update2D"/>,
/// ADR 0241), so the captures cycle the three colours, validation stays clean and steady frames allocate nothing.
/// </summary>
public sealed class CanvasTextureUpdateScene(HostOptions host) : RenderTestGame(host)
{
    private const int Size = 64;
    private readonly byte[][] _colours = [Fill(255, 0, 0), Fill(0, 255, 0), Fill(0, 0, 255)];
    private Texture2D? _texture;
    private int _next;

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = new Vector4(0, 0, 0, 1);
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Ignore;
        Root.ContentScaleSize = new Vector2(Size, Size);
        _texture = Texture2D.FromPixels(Size, Size, _colours[0]);
        var root = new Node2D { Name = "Canvas", TextureFilter = CanvasTextureFilter.Nearest };
        root.AddChild(new Sprite2D { Name = "Frame", Texture = _texture, Centered = false });
        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _texture!.SetPixels(_colours[_next]);
        _next = (_next + 1) % _colours.Length;
    }

    protected override void DisposeScene()
    {
    }

    private static byte[] Fill(byte r, byte g, byte b)
    {
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
        return pixels;
    }
}
