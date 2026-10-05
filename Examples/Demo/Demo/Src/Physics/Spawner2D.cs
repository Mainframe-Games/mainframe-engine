using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>Rains circles and boxes into the funnel; click spawns one at the cursor; Reset clears and refills.</summary>
public sealed class Spawner2D : Node2D
{
    private const int RainCount = 40;

    private static readonly CircleShape2D s_circle = new() { Radius = 16f };
    private static readonly RectangleShape2D s_box = new() { Size = new Vector2(30, 30) };
    private static readonly Vector4[] s_colors =
    [
        new(0.91f, 0.36f, 0.25f, 1), new(0.98f, 0.8f, 0.08f, 1), new(0.27f, 0.55f, 0.94f, 1), new(0.2f, 0.83f, 0.6f, 1),
    ];

    private int _spawned;
    private int _rain;

    /// <summary>Rain a first batch of bodies when the node becomes ready (off in tests that spawn their own).</summary>
    [Export] public bool SpawnStack { get; set; } = true;

    public int Count => ChildCount;

    protected override void OnReady()
    {
        if (SpawnStack)
            ResetBodies();
    }

    public void ResetBodies()
    {
        // Freed now (not queued) so Count is right immediately; reset runs from a UI event, never mid-traversal.
        foreach (var child in Children.ToArray())
            child.Free();
        _rain = RainCount;
    }

    public RigidBody2D Spawn(Vector2 at, bool circle)
    {
        var color = s_colors[_spawned % s_colors.Length];
        var outline = new Vector4(0, 0, 0, 0.35f);
        var body = new RigidBody2D { Name = $"Body{_spawned}", Position = at };
        body.AddChild(new CollisionShape2D { Name = "Shape", Shape = circle ? s_circle : s_box });
        body.AddChild(circle
            ? new DemoShape2D { Name = "Visual", Kind = DemoShapeKind.Circle, Radius = 16f, Color = color, Outline = outline, OutlineWidth = 2f }
            : new DemoShape2D
            {
                Name = "Visual", Kind = DemoShapeKind.Rect, Size = new Vector2(30, 30), Color = color,
                ColorBottom = color * new Vector4(0.7f, 0.7f, 0.7f, 1), Outline = outline, OutlineWidth = 2f,
            });
        _spawned++;
        AddChild(body);
        return body;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_rain <= 0)
            return;
        _rain--;
        Spawn(new Vector2(Random.Shared.NextSingle() * 500 - 250, -420), circle: _spawned % 2 == 0);
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click)
            Spawn(CanvasInput.WindowToCanvas(this, click.Position), circle: _spawned % 2 == 0);
    }
}
