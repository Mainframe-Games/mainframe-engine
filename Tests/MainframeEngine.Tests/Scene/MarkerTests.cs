using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary>#108: Godot's position-only <see cref="Marker3D"/> and <see cref="Marker2D"/>.</summary>
public sealed class MarkerTests
{
    [Fact]
    public void DefaultsMatchGodot()
    {
        Assert.Equal(0.25f, new Marker3D().GizmoExtents);
        Assert.Equal(10f, new Marker2D().GizmoExtents);
    }

    [Fact]
    public void MarkersRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Waypoints" };
        root.AddChild(new Marker3D { Name = "A", Position = new Vector3(1, 2, 3), GizmoExtents = 0.5f });
        root.AddChild(new Node3D { Name = "NotAMarker" });
        var flat = new Node2D { Name = "Flat" };
        flat.AddChild(new Marker2D { Name = "Spawn", Position = new Vector2(40, 8), GizmoExtents = 4f });
        root.AddChild(flat);
        foreach (var node in new Node[] { root.GetNode("A"), root.GetNode("NotAMarker"), flat, flat.GetNode("Spawn") })
            node.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();

        var markers = copy.Children.OfType<Marker3D>().ToList();
        var a = Assert.Single(markers);
        Assert.Equal(new Vector3(1, 2, 3), a.Position);
        Assert.Equal(0.5f, a.GizmoExtents);
        var spawn = Assert.IsType<Marker2D>(copy.GetNode("Flat/Spawn"));
        Assert.Equal(new Vector2(40, 8), spawn.Position);
        Assert.Equal(4f, spawn.GizmoExtents);
        copy.Free();
        root.Free();
    }
}
