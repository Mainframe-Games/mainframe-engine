using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Neon;

/// <summary>
/// The loft apartment, modelled in Blender by a script (<c>Tools/apartment/build_apartment.py</c> →
/// <see cref="ModelPath"/>): this node instantiates the model, makes its materials PBR, gives it collision and turns its
/// named nodes into the game's (as the Forest's <c>ForestCabin</c> does):
/// <list type="bullet">
/// <item>the shell as triangles per footstep surface (metal for the railings, else concrete; the glass is solid), a box
/// per <c>Furn_*</c> mesh, a box per <c>Collider_*</c> empty: the walkable slabs (a capsule sliding over triangles
/// catches on their internal edges), the stairs' ramp and the balcony's invisible 3 m walls;</item>
/// <item><c>Door_*</c> meshes become <see cref="Door"/>s, the balcony's <see cref="SlidingDoor"/> and the locked
/// <see cref="FrontDoor"/>;</item>
/// <item><c>Light_&lt;Room&gt;_&lt;n&gt;</c> empties become <see cref="OmniLight3D"/>s (the empty's scale is the range) and
/// <c>Switch_&lt;Room&gt;</c> empties <see cref="LightSwitch"/>es over those lights and the <c>LED_&lt;Room&gt;</c>
/// fixture material (this apartment's copy, as the switches change it);</item>
/// <item>the other empties (<c>Zone_*</c>, <c>Spawn</c>, <c>Seat</c>, <c>Shower</c>, <c>Tap_*</c>, <c>Tv</c>,
/// <c>Monitor_*</c>, ...) are kept by name in <see cref="Sockets"/> for the later interactables.</item>
/// </list>
/// It is a <c>[Tool]</c>: the editor shows the same apartment, without the switches and doors' behaviour mattering.
/// </summary>
[Tool]
public sealed class NeonApartment : Node3D
{
    public const string ModelPath = "Assets/Art/Apartment/apartment.gltf";

    /// <summary>The model's binary buffer (LFS): tests that need the model skip when it is a pointer.</summary>
    public const string BufferPath = "Assets/Art/Apartment/apartment.bin";

    private static readonly Dictionary<string, Vector3> LightColors = new(StringComparer.Ordinal)
    {
        ["Living"] = new(1f, 0.72f, 0.5f),
        ["Bath"] = new(0.85f, 0.92f, 1f),
        ["Computer"] = new(0.62f, 0.3f, 1f),
        ["Loft"] = new(1f, 0.4f, 0.7f),
        ["Bed2"] = new(0.35f, 0.85f, 1f),
        ["Balcony"] = new(1f, 0.55f, 0.25f),
    };

    private bool _built;

    /// <summary>The model's root node (the glTF instance).</summary>
    public Node3D? Model { get; private set; }

    /// <summary>The model's empties by name (sockets for interactables, zones, the spawn point).</summary>
    public Dictionary<string, Node3D> Sockets { get; } = new(StringComparer.Ordinal);

    /// <summary>The doors by their model name (<c>Door_Bath</c>, <c>Door_Balcony</c>, ...).</summary>
    public Dictionary<string, Node3D> Doors { get; } = new(StringComparer.Ordinal);

    /// <summary>The light switches by room (<c>Living</c>, <c>Bath</c>, ...).</summary>
    public Dictionary<string, LightSwitch> Switches { get; } = new(StringComparer.Ordinal);

    public List<OmniLight3D> Lights { get; } = [];

    public FrontDoor? FrontDoor => Doors.GetValueOrDefault("Door_Front") as FrontDoor;

    /// <summary>Where the player starts (feet) in this node's parent's space, and the yaw in degrees.</summary>
    public (Vector3 Position, float YawDegrees) Spawn
    {
        get
        {
            if (!Sockets.TryGetValue("Spawn", out var spawn))
                return (Position, 0f);
            var local = ToApartment(spawn);
            var forward = Vector3.TransformNormal(-Vector3.UnitZ, local.ToMatrix4x4());
            return (Transform.TransformPoint(local.Origin), float.RadiansToDegrees(MathF.Atan2(-forward.X, -forward.Z)));
        }
    }

    /// <summary>The apartment, built now (tests and tools; in a scene it builds when ready).</summary>
    public static NeonApartment Create()
    {
        var apartment = new NeonApartment { Name = "Apartment" };
        apartment.Build();
        return apartment;
    }

    protected override void OnReady()
    {
        if (!_built)
            Build();
        if (Tree?.EditMode != true && GameHost.UserArgs.Count > 0)
            NeonDev.Apply(this, GameHost.UserArgs);
    }

    private void Build()
    {
        _built = true;
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        Node3D model;
        try
        {
            model = scene.Instantiate<Node3D>();
        }
        finally
        {
            scene.Release();
        }

        model.Name = "Model";
        Model = model;
        AddChild(model);

        var materials = new Dictionary<Material, Material>();
        var fixtures = new Dictionary<Material, StandardMaterial3D>();
        var shell = new List<(string Name, ArrayMesh Mesh, Transform3D Transform)>();
        var furniture = new List<(string Name, Aabb Bounds)>();
        var doors = new List<Node3D>();
        Walk(model, Transform3D.Identity);

        void Walk(Node node, Transform3D parent)
        {
            var transform = node is Node3D n3 ? parent * n3.Transform : parent;
            var name = node.Name;
            if (node is Node3D door && name.StartsWith("Door_", StringComparison.Ordinal))
                doors.Add(door);
            else if (node is Node3D socket and not MeshInstance3D && node.Children.Count == 0)
                Sockets[name] = socket;

            if (node is MeshInstance3D { Mesh: ArrayMesh mesh } instance)
            {
                ConvertMaterials(mesh, materials);
                // The fixtures' glow is this apartment's own (the switches change it; the imported meshes are shared).
                if (name.StartsWith("LED_", StringComparison.Ordinal) && mesh.SurfaceCount > 0 && mesh.GetSurface(0).Material is StandardMaterial3D led)
                {
                    if (!fixtures.TryGetValue(led, out var own))
                        fixtures[led] = own = (StandardMaterial3D)led.Duplicate();
                    instance.MaterialOverride = own;
                }

                if (name.StartsWith("Furn_", StringComparison.Ordinal))
                    furniture.Add((name, mesh.Bounds.Transform(transform.ToMatrix4x4())));
                else if (Collides(node))
                    shell.Add((name, mesh, transform));
            }

            foreach (var child in node.Children)
                Walk(child, transform);
        }

        // The shell's triangles by footstep surface, but for the meshes whose collision is boxes (Collider_<mesh>_<n>).
        var boxed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Sockets.Keys)
            if (name.StartsWith("Collider_", StringComparison.Ordinal))
                boxed.Add(ColliderTarget(name));
        var faces = new Dictionary<string, List<Vector3>>(StringComparer.Ordinal);
        foreach (var (name, mesh, transform) in shell)
        {
            if (boxed.Contains(name))
                continue;
            var surface = SurfaceOf(name);
            if (!faces.TryGetValue(surface, out var list))
                faces[surface] = list = [];
            AddTriangles(mesh, transform, list);
        }

        foreach (var (surface, list) in faces)
            AddBody($"Shell_{surface}", surface, list);
        foreach (var (name, bounds) in furniture)
            AddBox(name + "_body", "wood", bounds.Center, Quaternion.Identity, bounds.Size * 0.98f);
        foreach (var (name, socket) in Sockets)
            if (name.StartsWith("Collider_", StringComparison.Ordinal))
            {
                ToApartment(socket).Decompose(out var position, out var rotation, out var scale);
                AddBox(name, SurfaceOf(ColliderTarget(name)), position, rotation, Vector3.Abs(scale) * 2f);
            }

        foreach (var door in doors)
            MakeDoor(door);
        MakeLights(fixtures.Values);
    }

    // ── Collision ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a mesh is part of the shell's triangle collision (doors, furniture and decoration are not).</summary>
    private static bool Collides(Node node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Name.StartsWith("Door_", StringComparison.Ordinal))
                return false;
        var name = node.Name;
        return !(name.StartsWith("Stairs", StringComparison.Ordinal) || name.StartsWith("Rug_", StringComparison.Ordinal) ||
                 name.StartsWith("LED_", StringComparison.Ordinal) || name.StartsWith("Plate_", StringComparison.Ordinal) ||
                 name.StartsWith("Screen_", StringComparison.Ordinal) || name.StartsWith("Decor_", StringComparison.Ordinal));
    }

    /// <summary>The footstep surface of a shell mesh (or a collider named after one).</summary>
    internal static string SurfaceOf(string name) =>
        name.StartsWith("Tile_", StringComparison.Ordinal) ? "tile"
        : name.StartsWith("Mezzanine", StringComparison.Ordinal) || name.StartsWith("Stairs", StringComparison.Ordinal) ||
          name.StartsWith("Railing_", StringComparison.Ordinal) ? "metal"
        : "concrete";

    /// <summary>What a <c>Collider_&lt;name&gt;[_&lt;n&gt;]</c> box is for: <c>&lt;name&gt;</c> (a mesh it replaces, or a name).</summary>
    internal static string ColliderTarget(string collider)
    {
        var name = collider["Collider_".Length..];
        var last = name.LastIndexOf('_');
        return last > 0 && last < name.Length - 1 && name[(last + 1)..].All(char.IsAsciiDigit) ? name[..last] : name;
    }

    private static void AddTriangles(ArrayMesh mesh, Transform3D transform, List<Vector3> faces)
    {
        var matrix = transform.ToMatrix4x4();
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            var positions = surface.Positions;
            foreach (var index in surface.Indices)
                faces.Add(Vector3.Transform(positions[index], matrix));
        }
    }

    private void AddBody(string name, string surface, List<Vector3> faces)
    {
        if (faces.Count == 0)
            return;
        var body = new SurfaceBody3D { Name = name, Surface = surface };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Faces = [.. faces] } });
        AddChild(body);
    }

    private void AddBox(string name, string surface, Vector3 centre, Quaternion rotation, Vector3 size)
    {
        var body = new SurfaceBody3D { Name = name, Surface = surface, Transform = Transform3D.FromTrs(centre, rotation, Vector3.One) };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.Max(size, new Vector3(0.02f)) } });
        AddChild(body);
    }

    // ── Doors, lights, switches ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A door node takes the door mesh's place (its transform, on the hinge) and the mesh moves under its leaf, with a
    /// box over it that moves with it. The front door keeps its mesh in place and only gets the box.
    /// </summary>
    private void MakeDoor(Node3D mesh)
    {
        var name = mesh.Name;
        var parent = mesh.Parent!;
        Node3D door = name switch
        {
            "Door_Front" => new FrontDoor(),
            "Door_Balcony" => new SlidingDoor(),
            _ => new Door(),
        };
        door.Transform = mesh.Transform;
        mesh.Name = "Mesh";
        door.Name = name;
        parent.AddChild(door);
        parent.RemoveChild(mesh);
        mesh.Transform = Transform3D.Identity;
        var holder = door is DoorBase leafed ? leafed.Leaf : door;
        holder.AddChild(mesh);

        var bounds = Aabb.Empty;
        foreach (var instance in mesh.FindChildren<MeshInstance3D>(owned: false).Append(mesh as MeshInstance3D))
            if (instance?.Mesh is { } m)
                bounds = bounds.Merge(m.Bounds.Transform(RelativeTo(instance, holder).ToMatrix4x4()));
        if (door is DoorBase swinging)
            swinging.AddBody(bounds, door is SlidingDoor ? "metal" : "wood");
        else
        {
            var body = new SurfaceBody3D { Name = "Body", Surface = "wood", CollisionLayer = NeonLayers.World | NeonLayers.Interact };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.Max(bounds.Size, new Vector3(0.05f)) }, Position = bounds.Center });
            door.AddChild(body);
        }

        Doors[name] = door;
    }

    private void MakeLights(IEnumerable<StandardMaterial3D> fixtures)
    {
        foreach (var (name, socket) in Sockets)
        {
            if (!name.StartsWith("Light_", StringComparison.Ordinal))
                continue;
            // The socket's scale is the range: the light goes under it with the scale undone.
            var range = MathF.Abs(socket.Scale.X);
            var light = new OmniLight3D
            {
                Name = "Light",
                Color = LightColors.GetValueOrDefault(RoomOf(name), Vector3.One),
                Energy = 1.6f,
                Range = range,
                Scale = new Vector3(1f / MathF.Max(range, 1e-3f)),
                CastsShadows = name == "Light_Living_1",
            };
            socket.AddChild(light);
            Lights.Add(light);
        }

        foreach (var (name, socket) in Sockets)
        {
            if (!name.StartsWith("Switch_", StringComparison.Ordinal))
                continue;
            var room = RoomOf(name);
            var lightSwitch = new LightSwitch { Name = name, Transform = ToApartment(socket) };
            lightSwitch.AddBody(new Vector3(0.16f, 0.2f, 0.16f));
            foreach (var light in Lights)
                if (RoomOf(light.Parent!.Name) == room)
                    lightSwitch.Lights.Add(light);
            foreach (var fixture in fixtures)
                if (fixture.ResourceName == "LED_" + room)
                    lightSwitch.AddFixture(fixture);
            AddChild(lightSwitch);
            Switches[room] = lightSwitch;
        }
    }

    /// <summary>The room in a <c>Prefix_Room[_n]</c> name.</summary>
    internal static string RoomOf(string name)
    {
        var start = name.IndexOf('_') + 1;
        var end = name.IndexOf('_', start);
        return end < 0 ? name[start..] : name[start..end];
    }

    // ── Materials ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The importer reads colours, emission and alpha only: each surface's material becomes PBR, with a roughness by
    /// material name (the graybox's materials are flat colours) and the glass a clear, two-sided pane. Shared per
    /// source material.
    /// </summary>
    internal static void ConvertMaterials(ArrayMesh mesh, Dictionary<Material, Material> converted)
    {
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            if (surface.Material is not StandardMaterial3D source || source.ShadingMode == ShadingMode.Pbr)
                continue;
            if (!converted.TryGetValue(source, out var pbr))
                converted[source] = pbr = ToPbr(source);
            surface.Material = pbr;
        }
    }

    private static StandardMaterial3D ToPbr(StandardMaterial3D source)
    {
        var name = source.ResourceName ?? "";
        if (name.StartsWith("Glass", StringComparison.Ordinal))
            return new StandardMaterial3D
            {
                ResourceName = name,
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = DrawingColor.FromArgb(40, 170, 190, 205),
                Roughness = 0.04f,
                Metallic = 0f,
                Transparency = AlphaMode.Blend,
                DoubleSided = true,
            };

        return new StandardMaterial3D
        {
            ResourceName = name,
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = source.AlbedoColor,
            Metallic = name is "Steel" or "Grate" ? 0.6f : 0f,
            Roughness = name switch
            {
                "Steel" => 0.4f,
                "Grate" => 0.55f,
                "Screen" => 0.15f,
                "Tile" or "Porcelain" => 0.25f,
                "Leather" => 0.45f,
                _ when name.StartsWith("LED_", StringComparison.Ordinal) => 0.4f,
                _ => 0.8f,
            },
            EmissionColor = source.EmissionColor,
            EmissionEnergy = source.EmissionEnergy,
            DoubleSided = true, // the shell's slabs and walls are seen from both sides
        };
    }

    // ── Transforms ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A model node's transform in this node's space.</summary>
    private Transform3D ToApartment(Node3D node) => RelativeTo(node, this);

    private static Transform3D RelativeTo(Node3D node, Node3D ancestor)
    {
        var transform = Transform3D.Identity;
        for (Node? n = node; n is not null && !ReferenceEquals(n, ancestor); n = n.Parent)
            if (n is Node3D n3)
                transform = n3.Transform * transform;
        return transform;
    }
}
