using System.Reflection;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>Options for <see cref="Node.Connect"/> (Godot's name).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Godot API name.")]
[Flags]
public enum ConnectFlags
{
    None = 0,

    /// <summary>The handler runs at the end of the frame (with <see cref="Node.CallDeferred(Action)"/>).</summary>
    Deferred = 1,

    /// <summary>The connection is saved with the scene (editor connections).</summary>
    Persist = 2,

    /// <summary>The connection is removed after the first emission.</summary>
    OneShot = 4,
}

/// <summary>A named connection made with <see cref="Node.Connect"/> (code connections with <c>+=</c> are not tracked).</summary>
public sealed class SignalConnection
{
    internal SignalConnection(Node source, SignalInfo signal, Node target, MethodInfo method, ConnectFlags flags)
    {
        Source = source;
        SignalInfo = signal;
        Target = target;
        MethodInfo = method;
        Flags = flags;
    }

    public Node Source { get; }
    public string Signal => SignalInfo.Name;
    public Node Target { get; }
    public string Method => MethodInfo.Name;
    public ConnectFlags Flags { get; }

    /// <summary>Scene root whose file created this connection (null for connections made in code).</summary>
    public Node? OriginScene { get; internal set; }

    internal SignalInfo SignalInfo { get; }
    internal MethodInfo MethodInfo { get; }
    internal Delegate Handler { get; set; } = null!;
}

public partial class Node
{
    private List<SignalConnection>? _outgoing;
    private List<SignalConnection>? _incoming;

    /// <summary>Emitted after <see cref="OnEnterTree"/>.</summary>
    [Signal] public event Action? TreeEntered;

    /// <summary>Emitted after <see cref="OnReady"/>.</summary>
    [Signal] public event Action? Ready;

    /// <summary>Emitted after <see cref="OnExitTree"/>, while the node is still in the tree.</summary>
    [Signal] public event Action? TreeExiting;

    /// <summary>Emitted once the node has left the tree.</summary>
    [Signal] public event Action? TreeExited;

    /// <summary>Emitted when <see cref="Name"/> changes.</summary>
    [Signal] public event Action? Renamed;

    /// <summary>Emitted on the parent after a child (at any depth below it, one level at a time) enters the tree.</summary>
    [Signal] public event Action<Node>? ChildEnteredTree;

    /// <summary>Emitted on the parent before a child leaves the tree.</summary>
    [Signal] public event Action<Node>? ChildExitingTree;

    /// <summary>
    /// Connects <paramref name="signal"/> (a <see cref="SignalAttribute"/> event of this node's type) to
    /// <paramref name="method"/> on <paramref name="target"/>: a public method, or a non-public one marked
    /// <see cref="SignalHandlerAttribute"/>, whose parameters match the signal's. Connections are removed when
    /// either node is freed. Use <see cref="ConnectFlags.Persist"/> for connections that are saved with the
    /// scene. Binding uses reflection once, at connect time; emitting is a plain delegate call.
    /// </summary>
    public SignalConnection Connect(string signal, Node target, string method, ConnectFlags flags = ConnectFlags.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(signal);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ThrowIfFreed();
        target.ThrowIfFreed();

        var info = TypeRegistry.GetRequired(GetType()).FindSignal(signal)
                   ?? throw new ArgumentException($"{GetType().Name} has no [Signal] named '{signal}'.", nameof(signal));
        if (FindConnection(signal, target, method) is not null)
            throw new InvalidOperationException($"'{Name}.{signal}' is already connected to '{target.Name}.{method}'.");

        var methodInfo = FindHandler(target.GetType(), method, info)
                         ?? throw new ArgumentException(
                             $"{target.GetType().Name} has no public or [SignalHandler] method '{method}' matching " +
                             $"signal '{signal}' ({string.Join(", ", info.ParameterTypes.Select(t => t.Name))}).",
                             nameof(method));

        var connection = new SignalConnection(this, info, target, methodInfo, flags);
        connection.Handler = (flags & (ConnectFlags.Deferred | ConnectFlags.OneShot)) != 0
            ? info.CreateForwarder(args => Dispatch(connection, args))
            : Delegate.CreateDelegate(info.DelegateType, target, methodInfo);

        info.Add(this, connection.Handler);
        (_outgoing ??= []).Add(connection);
        (target._incoming ??= []).Add(connection);
        return connection;
    }

    /// <summary>Removes a connection made with <see cref="Connect"/>; returns false when there was none.</summary>
    public bool Disconnect(string signal, Node target, string method)
    {
        var connection = FindConnection(signal, target, method);
        if (connection is null)
            return false;
        RemoveConnection(connection);
        return true;
    }

    public bool IsConnected(string signal, Node target, string method) => FindConnection(signal, target, method) is not null;

    /// <summary>Connections made with <see cref="Connect"/> from this node's signals.</summary>
    public IReadOnlyList<SignalConnection> GetSignalConnections() => (IReadOnlyList<SignalConnection>?)_outgoing ?? [];

    /// <summary>Connections made with <see cref="Connect"/> that target this node.</summary>
    public IReadOnlyList<SignalConnection> GetIncomingConnections() => (IReadOnlyList<SignalConnection>?)_incoming ?? [];

    private SignalConnection? FindConnection(string signal, Node target, string method)
    {
        if (_outgoing is null)
            return null;
        foreach (var c in _outgoing)
            if (ReferenceEquals(c.Target, target)
                && string.Equals(c.Signal, signal, StringComparison.Ordinal)
                && string.Equals(c.Method, method, StringComparison.Ordinal))
                return c;
        return null;
    }

    private static void RemoveConnection(SignalConnection connection)
    {
        connection.SignalInfo.Remove(connection.Source, connection.Handler);
        connection.Source._outgoing?.Remove(connection);
        connection.Target._incoming?.Remove(connection);
    }

    private void DisconnectAllSignals()
    {
        while (_outgoing is { Count: > 0 })
            RemoveConnection(_outgoing[^1]);
        while (_incoming is { Count: > 0 })
            RemoveConnection(_incoming[^1]);
    }

    private static void Dispatch(SignalConnection connection, object?[] args)
    {
        if ((connection.Flags & ConnectFlags.OneShot) != 0)
            RemoveConnection(connection);

        if ((connection.Flags & ConnectFlags.Deferred) != 0 && connection.Target.Tree is { } tree)
            tree.CallDeferred(static state =>
            {
                var (c, a) = ((SignalConnection, object?[]))state!;
                if (IsInstanceValid(c.Target))
                    c.MethodInfo.Invoke(c.Target, a);
            }, (connection, args));
        else
            connection.MethodInfo.Invoke(connection.Target, args);
    }

    private static MethodInfo? FindHandler(Type type, string name, SignalInfo signal)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        foreach (var method in type.GetMethods(flags))
        {
            if (!string.Equals(method.Name, name, StringComparison.Ordinal) || method.ReturnType != typeof(void))
                continue;
            if (!method.IsPublic && method.GetCustomAttribute<SignalHandlerAttribute>() is null)
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length != signal.ParameterTypes.Count)
                continue;
            var match = true;
            for (var i = 0; i < parameters.Length && match; i++)
                match = parameters[i].ParameterType.IsAssignableFrom(signal.ParameterTypes[i]);
            if (match)
                return method;
        }

        return null;
    }
}
