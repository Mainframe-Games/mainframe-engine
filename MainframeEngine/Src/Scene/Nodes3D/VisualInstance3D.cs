using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Base of 3D nodes the <see cref="RenderServer"/> draws (Godot's <c>VisualInstance3D</c>). Registers with its
/// viewport's <see cref="World3D"/> while inside a tree and creates its GPU objects through the render server
/// the first time it enters a tree that has one (or lazily at the first draw). GPU objects are released when
/// the node is freed, or by the server at shutdown.
/// </summary>
/// <remarks>
/// Subclasses override <see cref="InitializeRenderResources"/> / <see cref="ReleaseRenderResources"/> and the
/// draw methods; the server calls <see cref="Draw"/> inside the main render pass and the shadow methods inside
/// <see cref="ShadowSystem.RenderShadows{TState}"/>. The draw methods stay public so tree-less code (examples)
/// can still drive a node by hand.
/// </remarks>
public abstract class VisualInstance3D : Node3D, IRenderResourceOwner
{
    private World3D? _world;
    private RenderServer? _renderServer;
    private int _renderPriority;

    /// <summary>Whether the node is drawn into shadow maps.</summary>
    [Export]
    public bool CastShadows { get; set; } = true;

    /// <summary>Draw order within the world: lower first; equal priorities draw in tree-entry order.</summary>
    [Export]
    public int RenderPriority
    {
        get => _renderPriority;
        set
        {
            if (_renderPriority == value)
                return;
            _renderPriority = value;
            _world?.ResortVisual(this);
        }
    }

    /// <summary>
    /// True for nodes the render server draws in batches (<see cref="GeometryInstance3D"/>) instead of calling
    /// <see cref="Draw"/>.
    /// </summary>
    internal virtual bool IsBatched => false;

    /// <summary>The server that owns this node's GPU objects, once created.</summary>
    protected RenderServer? RenderServer => _renderServer;

    public bool HasRenderResources => _renderServer is not null;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.AddVisual(this);
        if (Tree?.Servers.Render is { } server)
            EnsureRenderResources(server);
    }

    protected override void OnExitTree()
    {
        _world?.RemoveVisual(this);
        _world = null;
        base.OnExitTree();
    }

    /// <summary>Creates the GPU objects through <paramref name="server"/> if not done yet.</summary>
    public void EnsureRenderResources(RenderServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (_renderServer is not null)
            return;
        _renderServer = server;
        server.Track(this);
        InitializeRenderResources(server);
    }

    /// <summary>Create pipelines, buffers and descriptor sets here.</summary>
    protected virtual void InitializeRenderResources(RenderServer server)
    {
    }

    /// <summary>Destroy what <see cref="InitializeRenderResources"/> created.</summary>
    protected virtual void ReleaseRenderResources()
    {
    }

    /// <summary>Draws the node in the main render pass.</summary>
    public virtual void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
    }

    /// <summary>Draws the node into a directional/spot shadow map.</summary>
    public virtual void DrawShadow2D(in CommandBuffer cb)
    {
    }

    /// <summary>Draws the node into one face of a point-light shadow cube.</summary>
    public virtual void DrawShadowPoint(in CommandBuffer cb, in Vector3 lightPos, in float lightRange)
    {
    }

    void IRenderResourceOwner.ReleaseRenderResourcesForShutdown() => ReleaseRenderResourcesNow();

    private void ReleaseRenderResourcesNow()
    {
        if (_renderServer is null)
            return;
        var server = _renderServer;
        ReleaseRenderResources();
        server.Untrack(this);
        _renderServer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ReleaseRenderResourcesNow();
        base.Dispose(disposing);
    }
}
