namespace MainframeEngine;

public class Node : IDisposable 
{
    public Node? Parent { get; set; }

    protected static IRenderer Renderer { get; private set; } = null!;
    protected static ShadowSystem? ShadowSystem { get; private set; }

    // TODO: is there a better way to handle this?
    public static void Initialize(IRenderer renderer, ShadowSystem? shadows = null)
    {
        Renderer = renderer;
        ShadowSystem = shadows;
    }
    
    public virtual void Dispose() { }

    public void AddChild(in Node node)
    {
        Parent = node;
    }
    
    public void RemoveChild(in Node node)
    {
        Parent = null;
    }
}