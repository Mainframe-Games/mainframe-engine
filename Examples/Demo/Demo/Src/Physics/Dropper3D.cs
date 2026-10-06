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

    public RigidBody3D Drop(Vector3 at)
    {
        var body = new RigidBody3D { Name = $"Crate{_spawned}", Mass = 2f, Position = at, CollisionLayer = CrateLayer, CollisionMask = CollisionLayers.All };
        body.AddChild(new CollisionShape3D { Name = "Shape", Shape = s_shape });
        body.AddChild(new MeshInstance3D { Name = "Visual", Mesh = s_mesh, MaterialOverride = s_materials[_spawned % s_materials.Length] });
        _spawned++;
        AddChild(body);
        return body;
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click
            || GetViewport()?.ActiveCamera3D is not { } camera || GetWorld3D() is not { } world)
            return;
        var pixel = click.Position * (Tree!.Servers.Get<UiServer>()?.PixelScale ?? 1f);
        var from = camera.ProjectRayOrigin(pixel);
        var to = from + camera.ProjectRayNormal(pixel) * 200f;
        if (world.DirectSpaceState.RayCast(from, to, out var hit))
            Drop(hit.Position + new Vector3(0, 4f, 0));
    }
}
