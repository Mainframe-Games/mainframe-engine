using System.Numerics;
using MainframeEngine.Networking;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public class Node : IDisposable
{
    public string Name { get; set; } = string.Empty;
    public NodeId Id { get; } = NodeId.GetNext();

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

    public virtual void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
    }

    public virtual void DrawShadow2D(in CommandBuffer cb)
    {
    }

    public virtual void OnUpdate(in GameTime gameTime)
    {
    }

    public virtual void DrawShadowPoint(in CommandBuffer cb, in Vector3 lightPos, in float lightRange)
    {
    }


}