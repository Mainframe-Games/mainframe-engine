using Color = System.Drawing.Color;
using System.Drawing;
using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>Spawns crates: a stack at start, one at the clicked floor point, ten on "rain". Reset restores the stack.</summary>
public sealed class Dropper3D : Node3D
{
    private static readonly BoxShape3D s_shape = new() { Size = new Vector3(0.8f) };
    private static readonly BoxMesh s_mesh = new() { Size = new Vector3(0.8f) };
    private static readonly StandardMaterial3D[] s_materials =
    [
        new() { AlbedoColor = Color.FromArgb(232, 93, 63) }, new() { AlbedoColor = Color.FromArgb(250, 204, 21) },
        new() { AlbedoColor = Color.FromArgb(70, 140, 240) }, new() { AlbedoColor = Color.FromArgb(52, 211, 153) },
    ];

    /// <summary>Crates live on layer 2 so the Pusher (which only sweeps the floor) plows into them instead of stopping at them; they still collide with everything.</summary>
    public const uint CrateLayer = 1u << 1;

    private int _spawned;

    public int Count => ChildCount;

    protected override void OnReady() => ResetBodies();

    public void ResetBodies()
    {
        // Freed now (not queued) so Count is right immediately; reset runs from a UI event, never mid-traversal.
        foreach (var child in Children.ToArray())
            child.Free();
        for (var layer = 0; layer < 5; layer++)
            for (var i = 0; i < 5 - layer; i++)
                Drop(new Vector3(-1.8f + i * 0.85f + layer * 0.42f, 0.4f + layer * 0.82f, 0));
    }

    public RigidBody3D Drop(Vector3 at) => Spawn(at, Quaternion.Identity, Vector3.Zero, _spawned % s_materials.Length);

    /// <summary>The crates' state for a save game.</summary>
    public CrateSave Snapshot()
    {
        var crates = new List<CrateState>(ChildCount);
        foreach (var child in Children)
        {
            if (child is RigidBody3D body && body.GetNodeOrNull<MeshInstance3D>("Visual") is { } visual)
                crates.Add(new CrateState(body.Position, body.Rotation, body.LinearVelocity, Array.IndexOf(s_materials, visual.MaterialOverride)));
        }

        return new CrateSave(crates, _spawned);
    }

    /// <summary>Replaces the crates with a loaded save's.</summary>
    public void Restore(CrateSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        foreach (var child in Children.ToArray())
            child.Free();
        _spawned = 0;
        foreach (var crate in save.Crates)
            Spawn(crate.Position, crate.Rotation, crate.Velocity, crate.Color);
        _spawned = Math.Max(_spawned, save.Spawned);
    }

    private RigidBody3D Spawn(Vector3 at, Quaternion rotation, Vector3 velocity, int color)
    {
        var body = new RigidBody3D
        {
            Name = $"Crate{_spawned}",
            Mass = 2f,
            Position = at,
            Rotation = rotation,
            LinearVelocity = velocity,
            CollisionLayer = CrateLayer,
            CollisionMask = CollisionLayers.All,
        };
        body.AddChild(new CollisionShape3D { Name = "Shape", Shape = s_shape });
        body.AddChild(new MeshInstance3D { Name = "Visual", Mesh = s_mesh, MaterialOverride = s_materials[Math.Clamp(color, 0, s_materials.Length - 1)] });
        _spawned++;
        AddChild(body);
        return body;
    }

    /// <summary>Ten crates over the stack (the "rain" action and button).</summary>
    public void Rain()
    {
        for (var i = 0; i < 10; i++)
            Drop(new Vector3(Random.Shared.NextSingle() * 6 - 3, 8 + i, Random.Shared.NextSingle() * 4 - 2));
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        var map = Tree?.Input.Map;
        if (inputEvent.IsActionPressed("rain", map))
        {
            Rain();
            return;
        }

        // "drop" from a gamepad (RT) drops over the middle of the floor; a click drops where it points.
        if (inputEvent.IsActionPressed("drop", map) && inputEvent is not InputEventMouseButton)
        {
            Drop(new Vector3(Random.Shared.NextSingle() * 4 - 2, 6f, Random.Shared.NextSingle() * 2 - 1));
            return;
        }

        // Without a project input map (tests, tools) the left button drops.
        if (inputEvent is not InputEventMouseButton { Pressed: true } click
            || !(map?.HasAction("drop") == true ? click.IsActionPressed("drop", map) : click.Button == MouseButton.Left)
            || GetViewport()?.ActiveCamera3D is not { } camera || GetWorld3D() is not { } world)
            return;
        var pixel = click.Position * (Tree!.Servers.Get<UiServer>()?.PixelScale ?? 1f);
        var from = camera.ProjectRayOrigin(pixel);
        var to = from + camera.ProjectRayNormal(pixel) * 200f;
        if (world.DirectSpaceState.RayCast(from, to, out var hit))
            Drop(hit.Position + new Vector3(0, 4f, 0));
    }
}
