using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The log cabin at the glade's west edge (<see cref="ValleyLayout.CabinCentre"/>), standing on its levelled pad: the shell
/// modelled in Blender by a script (<c>Tools/cabin/build_cabin.py</c> → <see cref="ModelPath"/>) with its materials made
/// PBR, the Poly Haven furniture at the model's <c>Place_&lt;id&gt;</c> sockets, collision (the shell as triangles: wood
/// underfoot, the hearth and chimney rock; boxes round the larger furniture), the fire in the hearth
/// (<see cref="CabinFire"/>), smoke from the chimney, the hinged door (<see cref="CabinDoor"/>) and, in the game, the
/// room's lighting zone (<see cref="CabinInterior"/>). The model's empties name the places: <c>Fire</c>, <c>ChimneyTop</c>,
/// <c>CandleFlame</c> (the mantel candle's wick), <c>Interior</c> (on the floor; its scale is the half width, the wall height
/// and the half depth), <c>R9</c> (the reference shot) and <c>Door</c> (the door's mesh, its origin on the hinge).
/// </summary>
public sealed class ForestCabin : Node3D
{
    public const string ModelPath = ForestAssets.CabinModel;

    /// <summary>The model's root node (the glTF instance).</summary>
    public Node3D? Model { get; private set; }

    public CabinDoor? Door { get; private set; }

    public CabinFire? Fire { get; private set; }

    public CabinInterior? Interior { get; private set; }

    /// <summary>The furniture placed at the sockets.</summary>
    public List<Node3D> Furniture { get; } = [];

    /// <summary>Furniture too small to stand in the way: no collision.</summary>
    private static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "wooden_lantern_01", "wooden_candlestick", "wooden_bowl_01", "carved_wooden_plate", "pot_enamel_01", "wooden_axe",
        "book_encyclopedia_set_01", "throw_pillows_01", "wicker_basket_01", "wooden_bucket_01",
    };

    /// <summary>
    /// The cabin at its place: <paramref name="padLevel"/> is the pad's height (the model's y = 0). With
    /// <paramref name="gameplay"/>, the door is usable and the room's lighting zone runs.
    /// </summary>
    public static ForestCabin Create(float padLevel, bool gameplay)
    {
        var cabin = new ForestCabin
        {
            Name = "Cabin",
            Position = new Vector3(ValleyLayout.CabinCentre.X, padLevel, ValleyLayout.CabinCentre.Y),
            RotationDegrees = new Vector3(0f, ValleyLayout.CabinYawDegrees, 0f),
        };
        cabin.Build(gameplay);
        return cabin;
    }

    private void Build(bool gameplay)
    {
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
        var shell = new List<Vector3>();
        var stone = new List<Vector3>();
        var sockets = new List<Node3D>();
        Node3D? door = null, fire = null, chimney = null, interior = null, candle = null;
        Walk(model, Transform3D.Identity);

        void Walk(Node node, Transform3D parent)
        {
            var transform = node is Node3D n3 ? parent * n3.Transform : parent;
            var name = node.Name;
            if (node is Node3D place && name.StartsWith("Place_", StringComparison.Ordinal))
                sockets.Add(place);
            else if (node is Node3D d && IsNamed(name, "Door"))
                door = d;
            else if (node is Node3D f && IsNamed(name, "Fire"))
                fire = f;
            else if (node is Node3D c && IsNamed(name, "ChimneyTop"))
                chimney = c;
            else if (node is Node3D i && IsNamed(name, "Interior"))
                interior = i;
            else if (node is Node3D k && IsNamed(name, "CandleFlame"))
                candle = k;

            if (node is MeshInstance3D { Mesh: ArrayMesh mesh } instance)
            {
                var glass = name.StartsWith("Glass", StringComparison.Ordinal);
                ConvertMaterials(mesh, materials, shell: true);
                if (glass)
                    instance.CastShadows = false; // the sun comes in through the windows
                instance.GIMode = GIMode.Static; // the probe bake darkens what it can under the roof
                // Collision: everything solid but the door (it has its own box), the glass and the rugs.
                if (!glass && !IsUnderName(node, "Door") && !name.StartsWith("Rug", StringComparison.Ordinal))
                {
                    var rock = name.StartsWith("Fireplace", StringComparison.Ordinal) || name.StartsWith("Chimney", StringComparison.Ordinal) ||
                               name.StartsWith("Footing", StringComparison.Ordinal);
                    AddTriangles(mesh, transform, rock ? stone : shell);
                }
            }

            foreach (var child in node.Children)
                Walk(child, transform);
        }

        AddBody("ShellBody", "wood", shell);
        AddBody("HearthBody", "rock", stone);
        if (door is not null)
            AddEntryRamp(door.Position);

        foreach (var socket in sockets)
            PlaceFurniture(socket, materials);

        if (fire is not null)
        {
            Fire = CabinFire.Create();
            fire.AddChild(Fire);
        }

        candle?.AddChild(CabinFire.CreateCandle());

        if (chimney is not null)
        {
            // The breeze (the scene's wind, +X and a little +Z) in the cabin's space.
            var wind = Vector3.Transform(new Vector3(1f, 0f, 0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -float.DegreesToRadians(ValleyLayout.CabinYawDegrees)));
            chimney.AddChild(ForestParticles.ChimneySmoke(wind));
        }

        if (door is not null)
            Door = MakeDoor(door, gameplay);

        // The model's Interior empty stands on the floor; its scale is the half width, the wall height and the half depth.
        if (gameplay && interior is not null)
        {
            var extents = Vector3.Abs(interior.Scale);
            Interior = new CabinInterior
            {
                Name = "Interior",
                Position = interior.Position + new Vector3(0f, extents.Y * 0.5f, 0f),
                HalfExtents = new Vector3(extents.X, extents.Y * 0.5f, extents.Z),
            };
            AddChild(Interior);
        }
    }

    /// <summary>A Blender name with or without its <c>.001</c>-style suffix.</summary>
    private static bool IsNamed(string name, string expected) =>
        name == expected || name.StartsWith(expected + ".", StringComparison.Ordinal) || name.StartsWith(expected + "_", StringComparison.Ordinal);

    private static bool IsUnderName(Node node, string name)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (IsNamed(n.Name, name))
                return true;
        return false;
    }

    /// <summary>
    /// The importer reads only Blinn-Phong colour and normal maps: each surface's material becomes PBR with the texture
    /// set's packed ORM (the cabin's <c>*_Color.jpg</c> → <c>*_ORM.png</c>, Poly Haven's <c>*_diff_*</c> → <c>*_arm_*</c>);
    /// the glass becomes a clear, faintly green, two-sided pane. The cabin's own surfaces (<paramref name="shell"/>) are
    /// two-sided: its roof and floor are single sheets, and the shadow pass culls back faces, so a roof whose slopes both
    /// face away from the low sun would cast nothing into the room. Shared per source material.
    /// </summary>
    internal static void ConvertMaterials(ArrayMesh mesh, Dictionary<Material, Material> converted, bool shell = false)
    {
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            if (surface.Material is not StandardMaterial3D source || source.ShadingMode == ShadingMode.Pbr)
                continue;
            if (!converted.TryGetValue(source, out var pbr))
            {
                converted[source] = pbr = ToPbr(source);
                if (shell && pbr is StandardMaterial3D standard)
                    standard.DoubleSided = true;
            }
            surface.Material = pbr;
        }
    }

    private static StandardMaterial3D ToPbr(StandardMaterial3D source)
    {
        if ((source.ResourceName ?? "").StartsWith("Glass", StringComparison.Ordinal))
            return new StandardMaterial3D
            {
                ResourceName = "Glass",
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = DrawingColor.FromArgb(46, 200, 214, 204),
                Roughness = 0.05f,
                Metallic = 0f,
                Transparency = AlphaMode.Blend,
                DoubleSided = true,
            };

        Texture2D? orm = null;
        if (source.AlbedoTexture?.ResourcePath is { } albedo)
        {
            var candidate = albedo.Contains("_Color.", StringComparison.Ordinal)
                ? albedo[..albedo.LastIndexOf("_Color.", StringComparison.Ordinal)] + "_ORM.png"
                : albedo.Replace("_diff_", "_arm_", StringComparison.Ordinal);
            candidate = Path.ChangeExtension(candidate, candidate.EndsWith("_ORM.png", StringComparison.Ordinal) ? ".png" : ".jpg");
            if (candidate != albedo && ResourceLoader.Exists(candidate))
                orm = ResourceLoader.Load<Texture2D>(candidate);
        }

        return new StandardMaterial3D
        {
            ResourceName = source.ResourceName ?? "",
            ShadingMode = ShadingMode.Pbr,
            AlbedoTexture = source.AlbedoTexture,
            AlbedoColor = source.AlbedoTexture is null ? source.AlbedoColor : DrawingColor.White,
            NormalTexture = source.NormalTexture,
            OrmTexture = orm,
            Metallic = 0f,
            Roughness = orm is null ? 0.8f : 1f,
            AmbientOcclusion = 1f,
            EmissionColor = source.EmissionColor,
            EmissionEnergy = source.EmissionEnergy,
            EmissionTexture = source.EmissionTexture,
            Transparency = source.Transparency,
            AlphaCutoff = source.AlphaCutoff,
            DoubleSided = source.Transparency == AlphaMode.Cutout,
        };
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

    /// <summary>
    /// A ramp over the stone steps up to the door (the model's steps: 0.15 m to z 3.59, 0.30 m to z 3.21, the 0.462 m
    /// threshold at z 2.86; the ramp clears each nosing, ≈ 23°, from the ground 1.13 m out): the player's capsule does not
    /// climb the steps' 15 cm risers. <paramref name="hinge"/> is the door's hinge (its width runs −x).
    /// </summary>
    private void AddEntryRamp(Vector3 hinge)
    {
        const float wall = 2.86f, run = 1.13f, rise = 0.475f, thickness = 0.1f, width = 1.5f;
        var angle = MathF.Atan2(rise, run);
        var length = MathF.Sqrt(run * run + rise * rise);
        var normal = new Vector3(0f, MathF.Cos(angle), MathF.Sin(angle));
        var middle = new Vector3(hinge.X - 0.44f, rise * 0.5f, wall + run * 0.5f) - normal * (thickness * 0.5f);
        var body = new SurfaceBody3D { Name = "EntryRamp", Surface = "rock", Position = middle, RotationDegrees = new Vector3(float.RadiansToDegrees(angle), 0f, 0f) };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, thickness, length) } });
        AddChild(body);
    }

    /// <summary>The Poly Haven model named by the socket (<c>Place_&lt;id&gt;[.001]</c>) at the socket, with collision boxes for the larger pieces.</summary>
    private void PlaceFurniture(Node3D socket, Dictionary<Material, Material> materials)
    {
        var id = socket.Name["Place_".Length..];
        var dot = id.IndexOf('.');
        if (dot >= 0)
            id = id[..dot];
        if (ForestAssets.FindProp(id) is not { } prop)
        {
            Log.Warning($"[Forest] Cabin: no prop '{id}' for socket '{socket.Name}'.");
            return;
        }

        var node = ForestAssets.InstantiateProp(prop, materialOverride: false);
        foreach (var instance in node.FindChildren<MeshInstance3D>(owned: false))
            if (instance.Mesh is ArrayMesh mesh)
                ConvertMaterials(mesh, materials);
        socket.AddChild(node);
        Furniture.Add(node);
        if (Small.Contains(id))
            return;

        // Boxes in the socket's space, slightly inside the scans; the body in the cabin's space, never scaled (the scale
        // goes into the boxes).
        SocketToCabin(socket).Decompose(out var translation, out var rotation, out var scale);
        var body = new SurfaceBody3D { Name = socket.Name + "_body", Surface = "wood", Transform = Transform3D.FromTrs(translation, rotation, Vector3.One) };
        foreach (var (mesh, transform) in ForestAssets.LoadPropMeshes(prop))
        {
            var bounds = mesh.Bounds.Transform(transform.ToMatrix4x4());
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = Vector3.Max(bounds.Size * scale * 0.9f, new Vector3(0.08f)) },
                Position = bounds.Center * scale,
            });
        }

        AddChild(body);
    }

    private Transform3D SocketToCabin(Node3D socket)
    {
        var transform = socket.Transform;
        for (var n = socket.Parent; n is not null && !ReferenceEquals(n, this); n = n.Parent)
            if (n is Node3D n3)
                transform = n3.Transform * transform;
        return transform;
    }

    /// <summary>
    /// The door: a <see cref="CabinDoor"/> hinge takes the door node's place (its transform), the door's meshes move under
    /// it, and a box the size of the door swings with them.
    /// </summary>
    private static CabinDoor MakeDoor(Node3D door, bool gameplay)
    {
        var parent = door.Parent!;
        // The model's door opens inwards with a negative turn about its hinge.
        var hinge = new CabinDoor { Name = "DoorHinge", Transform = door.Transform, Interactive = gameplay, OpenDegrees = -100f };
        parent.AddChild(hinge);
        parent.RemoveChild(door);
        door.Transform = Transform3D.Identity;
        hinge.AddChild(door);

        var bounds = Aabb.Empty;
        foreach (var instance in door.FindChildren<MeshInstance3D>(owned: false).Append(door as MeshInstance3D))
            if (instance?.Mesh is { } mesh)
                bounds = bounds.Merge(mesh.Bounds.Transform(RelativeTo(instance, hinge).ToMatrix4x4()));
        if (bounds.Size.LengthSquared() > 0f)
        {
            hinge.Middle = bounds.Center;
            var body = new SurfaceBody3D { Name = "DoorBody", Surface = "wood" };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.Max(bounds.Size, new Vector3(0.06f)) }, Position = bounds.Center });
            hinge.AddChild(body);
        }

        return hinge;
    }

    private static Transform3D RelativeTo(Node3D node, Node3D ancestor)
    {
        var transform = Transform3D.Identity;
        for (Node? n = node; n is not null && !ReferenceEquals(n, ancestor); n = n.Parent)
            if (n is Node3D n3)
                transform = n3.Transform * transform;
        return transform;
    }
}
