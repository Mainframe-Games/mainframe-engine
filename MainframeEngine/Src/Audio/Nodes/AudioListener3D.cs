namespace MainframeEngine;

/// <summary>
/// Overrides where 3D sound is heard from (Godot's <c>AudioListener3D</c>). The listener looks down its −Z axis like a
/// camera. Without a current listener, the viewport's active <see cref="Camera3D"/> is the listener.
/// </summary>
[EditorIcon("ear", Family = EditorIconFamily.Audio)]
public class AudioListener3D : Node3D
{
    private bool _current;
    private AudioServer? _server;

    /// <summary>Makes this the listener (only one is current; the others lose the flag).</summary>
    [Export]
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            _server?.MakeListenerCurrent(this, value);
        }
    }

    /// <summary>True while 3D audio is heard from this node.</summary>
    public bool IsCurrent => _server is not null && ReferenceEquals(_server.CurrentListener, this);

    public void MakeCurrent() => Current = true;

    public void ClearCurrent() => Current = false;

    internal void ClearCurrentFlag() => _current = false;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _server = Tree?.Servers.Get<AudioServer>();
        _server?.RegisterListener(this);
    }

    protected override void OnExitTree()
    {
        _server?.UnregisterListener(this);
        _server = null;
        base.OnExitTree();
    }
}
