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

    // The four point lights with cube shadows (the engine's limit): one per main room.
    private static readonly HashSet<string> ShadowedLights =
        new(StringComparer.Ordinal) { "Light_Living_1", "Light_Computer_1", "Light_Bath_1", "Light_Loft_1" };

    // Small accent lights: their own colour (null = the room's) and energy.
    private static readonly Dictionary<string, (Vector3?, float)> AccentLights = new(StringComparer.Ordinal)
    {
        ["Light_Living_3"] = (null, 2.5f),
        ["Light_Living_5"] = (new Vector3(1f, 0.55f, 0.3f), 2f),
        ["Light_Bath_2"] = (null, 2f),
        ["Light_Computer_2"] = (new Vector3(0.2f, 1f, 0.6f), 1.8f),
        ["Light_Computer_3"] = (new Vector3(0.7f, 0.25f, 1f), 2.2f),
        ["Light_Loft_2"] = (new Vector3(1f, 0.45f, 0.6f), 2.2f),
        ["Light_Bed2_2"] = (new Vector3(1f, 0.15f, 0.6f), 3f),
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

    public Shower? Shower { get; private set; }

    /// <summary>The taps by room (<c>Kitchen</c>, <c>Bath</c>).</summary>
    public Dictionary<string, Tap> Taps { get; } = new(StringComparer.Ordinal);

    public Tv? Tv { get; private set; }

    public Stereo? Stereo { get; private set; }

    public ComputerSeat? Seat { get; private set; }

    /// <summary>The blinds by socket name (<c>Blinds_Living_1</c>, ...).</summary>
    public Dictionary<string, Blinds> Blinds { get; } = new(StringComparer.Ordinal);

    /// <summary>The blinds' wall panels by room.</summary>
    public Dictionary<string, BlindsPanel> BlindsPanels { get; } = new(StringComparer.Ordinal);

    /// <summary>The monitors' screens (own materials: the computer's feeds go on them), in order.</summary>
    public List<StandardMaterial3D> MonitorScreens { get; } = [];

    /// <summary>The computer's screens and terminal.</summary>
    public ComputerRig? Computer { get; private set; }

    /// <summary>How many vehicles the city's traffic is moving (the terminal's <c>traffic</c>), when it runs.</summary>
    public Func<int>? TrafficCount { get; set; }

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
        var fixtures = new Dictionary<string, StandardMaterial3D>(StringComparer.Ordinal); // by name: one per room
        var shell = new List<(string Name, ArrayMesh Mesh, Transform3D Transform)>();
        var furniture = new List<(string Name, Aabb Bounds)>();
        var doors = new List<Node3D>();
        var meshes = new Dictionary<string, MeshInstance3D>(StringComparer.Ordinal);
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
                meshes.TryAdd(name, instance);
                // The fixtures' glow is this apartment's own (the switches change it; the imported meshes are shared).
                if (name.StartsWith("LED_", StringComparison.Ordinal) && mesh.SurfaceCount > 0 && mesh.GetSurface(0).Material is StandardMaterial3D led)
                {
                    if (!fixtures.TryGetValue(led.ResourceName ?? "", out var own))
                        fixtures[led.ResourceName ?? ""] = own = (StandardMaterial3D)led.Duplicate();
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
        MakeInteractables(meshes);
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
                 name.StartsWith("Screen_", StringComparison.Ordinal) || name.StartsWith("Decor_", StringComparison.Ordinal) ||
                 name.StartsWith("Glow_", StringComparison.Ordinal));
    }

    /// <summary>The footstep surface of a shell mesh (or a collider named after one).</summary>
    internal static string SurfaceOf(string name) =>
        name.StartsWith("Tile_", StringComparison.Ordinal) ? "tile"
        : name.StartsWith("Planks_", StringComparison.Ordinal) ? "wood"
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
            var accent = AccentLights.TryGetValue(name, out var a) ? a : ((Vector3?)null, 0f);
            var light = new OmniLight3D
            {
                Name = "Light",
                Color = accent.Item1 ?? LightColors.GetValueOrDefault(RoomOf(name), Vector3.One),
                Energy = accent.Item2 > 0 ? accent.Item2 : 4f,
                Range = range,
                Scale = new Vector3(1f / MathF.Max(range, 1e-3f)),
                CastsShadows = ShadowedLights.Contains(name),
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

    /// <summary>
    /// The shower (head and valve), the taps, the TV and stereo, the computer's seat and screens, and the blinds with
    /// their wall panels, at the model's sockets. Screens, the stereo's display and the shower's glass get their own
    /// materials (they change at run time).
    /// </summary>
    private void MakeInteractables(Dictionary<string, MeshInstance3D> meshes)
    {
        StandardMaterial3D? Own(string mesh)
        {
            if (!meshes.TryGetValue(mesh, out var instance) || instance.Mesh is not { SurfaceCount: > 0 } m ||
                m.GetSurface(0).Material is not StandardMaterial3D shared)
                return null;
            var own = (StandardMaterial3D)shared.Duplicate();
            instance.MaterialOverride = own;
            return own;
        }

        Vector3 At(string socket) => Sockets.TryGetValue(socket, out var node) ? ToApartment(node).Origin : Vector3.Zero;

        if (Sockets.ContainsKey("Shower"))
        {
            var head = At("Shower");
            var shower = new Shower { Name = "Shower", Position = head, Drop = head.Y - 0.01f, Glass = Own("Glass_Shower") };
            shower.AddBody(new Vector3(0.22f, 0.3f, 0.2f), At("Valve_Shower") - head);
            AddChild(shower);
            Shower = shower;
        }

        foreach (var (room, drop) in new[] { ("Kitchen", 0.27f), ("Bath", 0.2f) })
        {
            if (!Sockets.ContainsKey("Tap_" + room))
                continue;
            var tap = new Tap { Name = "Tap_" + room, Position = At("Tap_" + room), Drop = drop };
            tap.AddBody(new Vector3(0.3f, 0.32f, 0.36f), new Vector3(0f, 0.02f, -0.06f));
            AddChild(tap);
            Taps[room] = tap;
        }

        if (Sockets.TryGetValue("Tv", out var tvSocket))
        {
            var tv = new Tv { Name = "Tv", Transform = ToApartment(tvSocket), Screen = Own("Screen_Tv") };
            tv.AddBody(new Vector3(2.4f, 1.3f, 0.12f));
            AddChild(tv);
            Tv = tv;
        }

        if (Sockets.ContainsKey("Stereo"))
        {
            var stereo = new Stereo { Name = "Stereo", Position = At("Stereo"), Display = Own("Decor_StereoDisplay") };
            stereo.AddBody(new Vector3(0.45f, 0.22f, 0.6f), new Vector3(-0.02f, 0f, -0.02f));
            AddChild(stereo);
            Stereo = stereo;
        }

        for (var i = 1; meshes.ContainsKey($"Screen_Monitor_{i}"); i++)
            if (Own($"Screen_Monitor_{i}") is { } screen)
                MonitorScreens.Add(screen);

        if (Sockets.TryGetValue("Seat", out var seatSocket))
        {
            var seat = new ComputerSeat { Name = "ComputerSeat", Transform = ToApartment(seatSocket) };
            seat.AddBody(new Vector3(0.8f, 1.3f, 0.8f), new Vector3(0f, 0.65f, 0f));
            AddChild(seat);
            Seat = seat;
        }

        if (MonitorScreens.Count > 0)
        {
            var rig = new ComputerRig { Name = "Computer", Seat = Seat };
            rig.Monitors.AddRange(MonitorScreens);
            AddChild(rig);
            rig.Setup(new TerminalHost(this), Stereo);
            if (Seat is not null)
                Seat.KeyboardCaptured = true;
            Computer = rig;
        }

        if (Tv is not null)
            AddChild(new TvFeed { Name = "TvFeed", Tv = Tv });
        ConnectTraffic();

        var slats = new StandardMaterial3D
        {
            ResourceName = "Blind slats",
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = DrawingColor.FromArgb(255, 34, 34, 40),
            Metallic = 0.6f,
            Roughness = 0.45f,
            DoubleSided = true,
        };
        foreach (var (name, socket) in Sockets)
        {
            if (!name.StartsWith("Blinds_", StringComparison.Ordinal))
                continue;
            ToApartment(socket).Decompose(out var top, out _, out var scale);
            var blinds = new Blinds { Name = name, Position = top, Width = MathF.Abs(scale.X) * 2f, Drop = MathF.Abs(scale.Y), SlatMaterial = slats };
            AddChild(blinds);
            Blinds[name] = blinds;
        }

        foreach (var (name, socket) in Sockets)
        {
            if (!name.StartsWith("Panel_Blinds_", StringComparison.Ordinal))
                continue;
            var room = name["Panel_Blinds_".Length..];
            var panel = new BlindsPanel { Name = name, Transform = ToApartment(socket), IsOn = true };
            panel.AddBody(new Vector3(0.18f, 0.24f, 0.18f));
            foreach (var (blindsName, blinds) in Blinds)
                if (RoomOf(blindsName) == room)
                    panel.Blinds.Add(blinds);
            AddChild(panel);
            BlindsPanels[room] = panel;
        }
    }

    /// <summary>
    /// The city's traffic (a sibling <see cref="NeonCity"/>, built before this node in the scene) feeds the terminal's
    /// <c>traffic</c> count and the computer's map.
    /// </summary>
    private void ConnectTraffic()
    {
        if (Parent?.GetNodeOrNull<CityTraffic>("City/Traffic") is not { } traffic)
            return;
        TrafficCount = () => traffic.VehicleCount;
        if (Computer?.Map is { } map)
            map.Positions = traffic.MapPositions;
    }

    /// <summary>The terminal's commands on this apartment.</summary>
    private sealed class TerminalHost(NeonApartment apartment) : ITerminalHost
    {
        private readonly string[] _rooms = [.. apartment.Switches.Keys.Select(r => r.ToLowerInvariant()).Order()];

        public IReadOnlyList<string> Rooms => _rooms;

        public int TrafficCount => apartment.TrafficCount?.Invoke() ?? 0;

        public bool SetLights(string room, bool on)
        {
            foreach (var (name, lightSwitch) in apartment.Switches)
                if (string.Equals(name, room, StringComparison.OrdinalIgnoreCase))
                {
                    lightSwitch.SetOn(on);
                    return true;
                }

            return false;
        }

        public void SetBlinds(bool open)
        {
            foreach (var panel in apartment.BlindsPanels.Values)
                panel.SetOn(open);
        }

        public int Music(int track)
        {
            if (apartment.Stereo is not { } stereo)
                return -1;
            if (track == -2)
                track = stereo.IsOn ? (stereo.Track + 1) % stereo.TrackCount : 0;
            stereo.Play(track);
            return stereo.IsOn ? stereo.Track : -1;
        }

        public void SetTv(bool on)
        {
            if (apartment.Tv is { } tv && tv.IsOn != on)
                tv.Interact();
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
        // The imported meshes are cached and shared by every apartment (tests build several at once): convert once.
        lock (ConversionLock)
            ConvertSurfaces(mesh, converted);
    }

    private static readonly Lock ConversionLock = new();

    private static void ConvertSurfaces(ArrayMesh mesh, Dictionary<Material, Material> converted)
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

    /// <summary>
    /// The importer reads colour, normal maps, emission and alpha: each surface's material becomes PBR. A textured set
    /// gets its packed ORM by name (<c>*_Color.jpg</c> → <c>*_ORM.png</c>: AO, roughness, metallic) and keeps its
    /// colour as a tint; flat materials get a roughness and metalness by name; the glass becomes a clear, two-sided pane.
    /// </summary>
    private static StandardMaterial3D ToPbr(StandardMaterial3D source)
    {
        var name = source.ResourceName ?? "";
        if (name.StartsWith("Glass", StringComparison.Ordinal))
            return new StandardMaterial3D
            {
                ResourceName = name,
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = DrawingColor.FromArgb(34, 170, 190, 205),
                Roughness = 0.04f,
                Metallic = 0f,
                Transparency = AlphaMode.Blend,
                DoubleSided = true,
            };

        Texture2D? orm = null;
        if (source.AlbedoTexture?.ResourcePath is { } albedo && albedo.Contains("_Color.", StringComparison.Ordinal))
        {
            var candidate = albedo[..albedo.LastIndexOf("_Color.", StringComparison.Ordinal)] + "_ORM.png";
            if (ResourceLoader.Exists(candidate))
                orm = ResourceLoader.Load<Texture2D>(candidate);
        }

        return new StandardMaterial3D
        {
            ResourceName = name,
            ShadingMode = ShadingMode.Pbr,
            AlbedoTexture = source.AlbedoTexture,
            AlbedoColor = source.AlbedoColor,
            NormalTexture = source.NormalTexture,
            OrmTexture = orm,
            AmbientOcclusion = 1f,
            Metallic = orm is not null ? 1f : name is "Chrome" or "Mirror" ? 1f : 0f,
            Roughness = orm is not null ? 1f : name switch
            {
                "Chrome" => 0.15f,
                "Mirror" => 0.04f,
                "Screen" => 0.12f,
                "Porcelain" => 0.2f,
                "Black" or "Door" => 0.45f,
                _ when name.StartsWith("Poster_", StringComparison.Ordinal) => 0.6f,
                _ when name.StartsWith("LED_", StringComparison.Ordinal) || name.StartsWith("Glow_", StringComparison.Ordinal) => 0.4f,
                _ => 0.8f,
            },
            EmissionColor = source.EmissionColor,
            EmissionEnergy = source.EmissionEnergy,
            EmissionTexture = source.EmissionTexture,
            // The shell's slabs and walls are seen from both sides; textured sets are lit from the front only.
            DoubleSided = source.AlbedoTexture is null,
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
