namespace MainframeEngine.Editor;

/// <summary>
/// Connects a signal of a scene node to a method of another (the Signals tab). The connection is
/// <see cref="ConnectFlags.Persist"/>, so the scene saves it; undo disconnects it.
/// </summary>
public sealed class ConnectSignalAction : IEditorAction
{
    public ConnectSignalAction(Node source, string signal, Node target, string method, ConnectFlags flags = ConnectFlags.None)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentException.ThrowIfNullOrEmpty(signal);
        ArgumentException.ThrowIfNullOrEmpty(method);
        Signal = signal;
        Method = method;
        Flags = flags & ~ConnectFlags.Persist;
    }

    public Node Source { get; }
    public string Signal { get; }
    public Node Target { get; }
    public string Method { get; }

    /// <summary>Extra flags (deferred, one shot); <see cref="ConnectFlags.Persist"/> is always added.</summary>
    public ConnectFlags Flags { get; }

    public string Name => $"Connect {Source.Name}.{Signal}";

    public void Do() => Source.Connect(Signal, Target, Method, Flags | ConnectFlags.Persist);

    public void Undo() => Source.Disconnect(Signal, Target, Method);
}

/// <summary>Removes a persisted connection (the Signals tab's disconnect button); undo connects it again with its flags.</summary>
public sealed class DisconnectSignalAction : IEditorAction
{
    public DisconnectSignalAction(SignalConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Source = connection.Source;
        Signal = connection.Signal;
        Target = connection.Target;
        Method = connection.Method;
        Flags = connection.Flags;
    }

    public Node Source { get; }
    public string Signal { get; }
    public Node Target { get; }
    public string Method { get; }
    public ConnectFlags Flags { get; }

    public string Name => $"Disconnect {Source.Name}.{Signal}";

    public void Do() => Source.Disconnect(Signal, Target, Method);

    // The Signals tab only lists connections the edited scene owns (no origin, or the scene itself), so reconnecting
    // without an origin saves them the same way.
    public void Undo() => Source.Connect(Signal, Target, Method, Flags);
}
