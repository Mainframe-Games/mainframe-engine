namespace MainframeEngine;

/// <summary>
/// An engine server: the owner of the GPU, physics, audio or UI objects behind a family of nodes (Godot's
/// servers). Nodes hold editable state and reach their server through <see cref="SceneTree.Servers"/>; the
/// server holds the expensive objects. Servers are created by <see cref="Engine"/> after the renderer and
/// disposed, in reverse registration order, after the scene tree has been freed.
/// </summary>
public interface IServer : IDisposable;

/// <summary>A server that runs once per frame, after process and transform sync (audio, UI layout).</summary>
public interface IFrameServer : IServer
{
    void Process(in GameTime gameTime);
}

/// <summary>A server that steps with the fixed physics tick, after <see cref="Node.OnPhysicsProcess"/> (physics).</summary>
public interface IFixedStepServer : IServer
{
    void FixedStep(float delta);
}

/// <summary>
/// The servers available to a <see cref="SceneTree"/>, one instance per server type. M2 ships
/// <see cref="RenderServer"/>; the physics (M6), audio (M7) and UI (M8) servers register the same way:
/// <code>
/// Servers.Register(new PhysicsServer3D(...));            // engine startup
/// var physics = node.Tree!.Servers.Get&lt;PhysicsServer3D&gt;(); // from a node, in OnEnterTree
/// </code>
/// Implement <see cref="IFixedStepServer"/> or <see cref="IFrameServer"/> to be ticked by the tree.
/// </summary>
public sealed class ServerRegistry : IDisposable
{
    private readonly List<IServer> _servers = [];
    private IFrameServer[] _frameServers = [];
    private IFixedStepServer[] _fixedStepServers = [];
    private bool _disposed;

    /// <summary>The render server, if a renderer exists (null in headless trees and unit tests).</summary>
    public RenderServer? Render { get; private set; }

    public IReadOnlyList<IServer> All => _servers;

    internal ReadOnlySpan<IFrameServer> FrameServers => _frameServers;

    internal ReadOnlySpan<IFixedStepServer> FixedStepServers => _fixedStepServers;

    /// <summary>Adds <paramref name="server"/>; only one server per concrete type.</summary>
    public void Register(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var existing in _servers)
            if (existing.GetType() == server.GetType())
                throw new InvalidOperationException($"A {server.GetType().Name} is already registered.");

        _servers.Add(server);
        if (server is RenderServer render)
            Render = render;
        Rebuild();
    }

    /// <summary>Removes <paramref name="server"/> without disposing it.</summary>
    public bool Unregister(IServer server)
    {
        if (!_servers.Remove(server))
            return false;
        if (ReferenceEquals(server, Render))
            Render = null;
        Rebuild();
        return true;
    }

    public T? Get<T>() where T : class, IServer
    {
        foreach (var server in _servers)
            if (server is T typed)
                return typed;
        return null;
    }

    public T GetRequired<T>() where T : class, IServer =>
        Get<T>() ?? throw new InvalidOperationException($"No {typeof(T).Name} is registered.");

    /// <summary>Disposes every server in reverse registration order.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = _servers.Count - 1; i >= 0; i--)
            _servers[i].Dispose();
        _servers.Clear();
        Render = null;
        Rebuild();
    }

    private void Rebuild()
    {
        _frameServers = [.. _servers.OfType<IFrameServer>()];
        _fixedStepServers = [.. _servers.OfType<IFixedStepServer>()];
    }
}
