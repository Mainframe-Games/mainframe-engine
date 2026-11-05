using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Paths 2D scene's controls: a speed multiplier for every follower and the guard, and pausing them.</summary>
public sealed class Paths2DPanel : UiDocument
{
    private readonly List<(PathFollow2D Follow, float Speed)> _followers = [];
    private PathGuard2D? _guard;
    private float _guardSpeed;
    private float _scale = 1f;
    private bool _paused;

    public Paths2DPanel()
    {
        Source = "Assets/Paths2D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>Multiplies the followers' and the guard's speeds; the panel's slider is bound to it.</summary>
    public float SpeedScale
    {
        get => _scale;
        set
        {
            _scale = value;
            Apply();
        }
    }

    /// <summary>Stops every follower and the guard; the panel's checkbox is bound to it.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Apply();
        }
    }

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        foreach (var path in new[] { "Track", "Arc" })
            foreach (var child in scene.GetNode(path).Children)
                if (child is PathFollow2D follow)
                    _followers.Add((follow, follow.Speed));
        _guard = scene.GetNode<PathGuard2D>("Guard");
        _guardSpeed = _guard.Speed;
        CreateDataModel("paths2d")
            .Bind("speed", this, static d => d.SpeedScale, static (d, v) => d.SpeedScale = v)
            .Bind("paused", this, static d => d.Paused, static (d, v) => d.Paused = v);
    }

    private void Apply()
    {
        var scale = _paused ? 0f : _scale;
        foreach (var (follow, speed) in _followers)
            follow.Speed = speed * scale;
        if (_guard is not null)
            _guard.Speed = _guardSpeed * scale;
    }

    protected override void OnExitTree()
    {
        _followers.Clear();
        _guard = null;
        base.OnExitTree();
    }
}
