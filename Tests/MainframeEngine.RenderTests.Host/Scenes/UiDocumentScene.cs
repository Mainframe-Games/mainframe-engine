namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// A UI-only scene (no camera, so the frame behind the UI is the tonemapped clear colour): one <see cref="UiLayer"/>
/// showing one document. Used for the effects (clip masks, transforms, filters, gradients), text and widget goldens.
/// </summary>
public class UiDocumentScene(HostOptions host, string source) : RenderTestGame(host)
{
    protected UiDocument Document { get; private set; } = null!;

    protected override void LoadScene()
    {
        var layer = new UiLayer { Name = "Ui" };
        Document = CreateDocument(source);
        layer.AddChild(Document);
        Tree.ChangeScene(layer);
    }

    protected virtual UiDocument CreateDocument(string path) => new() { Name = "Document", Source = path, AutoFocus = false };

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}
