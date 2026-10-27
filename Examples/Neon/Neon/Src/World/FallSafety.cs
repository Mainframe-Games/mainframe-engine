using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The safety net: a wide box below the apartment. A <see cref="FirstPersonController"/> that ever gets out (through a
/// gap in the collision, a physics glitch) and falls into it is put back at <see cref="RespawnPosition"/>, still,
/// facing where it was. The scene puts the respawn point at <see cref="NeonScene.Spawn"/> (the apartment's <c>Spawn</c>).
/// </summary>
public sealed class FallSafety : Area3D
{
    /// <summary>Where a falling player is put back (feet, world space).</summary>
    [Export] public Vector3 RespawnPosition { get; set; }

    /// <summary>How many times a player was put back.</summary>
    public int Respawns { get; private set; }

    /// <summary>The safety net under the apartment: 400 × 400 m, 10 m deep, its top <paramref name="depth"/> below the floor.</summary>
    public static FallSafety Create(Vector3 respawn, float depth = 8f)
    {
        var area = new FallSafety { Name = "FallSafety", RespawnPosition = respawn, Position = new Vector3(0f, -depth - 5f, 0f), Monitorable = false };
        area.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(400f, 10f, 400f) } });
        return area;
    }

    protected override void OnReady() => BodyEntered += OnBodyEntered;

    private void OnBodyEntered(Node3D body)
    {
        if (body is not FirstPersonController player)
            return;
        Respawns++;
        Log.Info($"[Neon] The player fell out at {player.GlobalPosition}: back to the spawn point.");
        player.Velocity = Vector3.Zero;
        player.GlobalPosition = RespawnPosition + new Vector3(0f, 0.02f, 0f);
    }
}
