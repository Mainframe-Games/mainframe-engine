using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// The Tiles 2D hero: a top-down <see cref="CharacterBody2D"/> (floating motion) walked with the move actions (WASD /
/// arrows / left stick), or along a loop of waypoints when nobody steers. Its <see cref="AnimatedSprite2D"/> child plays
/// idle or run from the velocity and faces the way it walks. It reports the tile under its feet: the "surface" custom data
/// of the ground layer's cell (footstep sounds would read it).
/// </summary>
public sealed class TileHero2D : CharacterBody2D
{
    // Along the level's paths (cells), out to the far corner and back: the hero passes trees, bushes, walls and water.
    private static readonly Vector2[] Waypoints = Cells([(13, 9), (13, 6), (19, 6), (19, 1), (22, 1), (22, 5), (26, 5), (26, 9), (30, 9), (30, 13),
        (34, 13), (34, 17), (34, 13), (30, 13), (30, 9), (26, 9), (26, 5), (22, 5), (22, 1), (19, 1), (19, 6), (13, 6)]);

    private AnimatedSprite2D? _sprite;
    private TileMapLayer? _ground;
    private int _next = 1;
    private string _surface = "";

    /// <summary>Walk speed in pixels per second.</summary>
    [Export(Range = "0,400,1")]
    public float Speed { get; set; } = 70f;

    /// <summary>Follows the waypoints when no move action is held.</summary>
    [Export]
    public bool Autopilot { get; set; } = true;

    /// <summary>The ground cell's "surface" custom data under the hero ("" off the map).</summary>
    public string Surface => _surface;

    protected override void OnReady()
    {
        _sprite = GetNodeOrNull<AnimatedSprite2D>("Sprite");
        _ground = GetNodeOrNull<TileMapLayer>("../../Ground");
    }

    protected override void OnPhysicsProcess(float delta)
    {
        var map = Tree?.Input.Map;
        var input = map?.HasAction("move_left") == true ? Input.GetVector("move_left", "move_right", "move_up", "move_down") : Vector2.Zero;
        Vector2 direction;
        if (input != Vector2.Zero || !Autopilot)
        {
            direction = input;
        }
        else
        {
            var target = Waypoints[_next];
            var to = target - Position;
            if (to.Length() < 4f)
            {
                _next = (_next + 1) % Waypoints.Length;
                to = Waypoints[_next] - Position;
            }

            direction = to.LengthSquared() > 0 ? Vector2.Normalize(to) : Vector2.Zero;
        }

        Velocity = direction * Speed;
        MoveAndSlide();
        if (_sprite is { } sprite)
        {
            var moving = GetRealVelocity().LengthSquared() > 25f;
            var animation = moving ? "run" : "idle";
            if (sprite.Animation != animation)
                sprite.Play(animation);
            if (MathF.Abs(Velocity.X) > 1f)
                sprite.FlipH = Velocity.X < 0;
        }

        UpdateSurface();
    }

    private static Vector2[] Cells(ReadOnlySpan<(int X, int Y)> cells)
    {
        var points = new Vector2[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            points[i] = new Vector2(cells[i].X * TilesArt.Cell + TilesArt.Cell / 2, cells[i].Y * TilesArt.Cell + 11);
        return points;
    }

    private void UpdateSurface()
    {
        if (_ground is not { TileSet: { } set } ground)
            return;
        var layer = set.GetCustomDataLayerByName("surface");
        var data = ground.GetCellTileData(ground.LocalToMap(ground.ToLocal(GlobalPosition)));
        var surface = data is not null && layer >= 0 ? data.GetCustomString(layer) : "";
        if (!ReferenceEquals(surface, _surface))
            _surface = surface;
    }
}
