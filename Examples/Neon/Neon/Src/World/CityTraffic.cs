using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Neon;

/// <summary>
/// The traffic over <see cref="CityLayout"/>'s loops, in two kinds:
/// <list type="bullet">
/// <item>the crowd: cars both ways on every street loop and hover cars on the far sky lanes, one <see cref="MultiMesh"/>
/// instance per vehicle whose transform is sampled from its loop's baked <see cref="Curve3D"/> each frame (no node per
/// vehicle; positions are a pure function of the time since the city was built);</item>
/// <item>the close ones, on <see cref="Path3D"/>s with <see cref="PathFollow3D"/> followers moving by their
/// <see cref="PathFollow3D.Speed"/> (ADR 0215): the four-car train (oriented on the rail), the two airships and the hover
/// cars of the lane that passes the balcony, each carrying its sound (the train's rumble, the airships' drone, a
/// Doppler whoosh as a car passes the listener, <see cref="FlyBy"/>).</item>
/// </list>
/// Nothing allocates per frame. <see cref="MapPositions"/> feeds the computer's traffic map.
/// </summary>
public sealed class CityTraffic : Node3D
{
    public const string BeamShader = "Assets/Shaders/beam.gdshader";

    private readonly List<Lane> _lanes = [];
    private readonly List<Fleet> _fleets = [];
    private readonly List<PathFollow3D> _followers = [];
    private PathFollow3D? _trainHead;
    private readonly List<PathFollow3D> _airships = [];
    private bool _sounds;
    private double _time;

    /// <summary>The loop whose hover cars are path followers (it passes the balcony).</summary>
    public const string BalconyLane = "Sky_Boulevard";

    /// <summary>The city whose layout the traffic follows (set before it enters the tree).</summary>
    public CityLayout? Layout { get; set; }

    /// <summary>How many vehicles move (all kinds).</summary>
    public int VehicleCount { get; private set; }

    /// <summary>The followers (train cars, airships, the balcony lane's hover cars).</summary>
    public IReadOnlyList<PathFollow3D> Followers => _followers;

    protected override void OnReady()
    {
        if (_fleets.Count == 0)
            Build();
    }

    /// <summary>Builds the fleets now (it builds itself when ready otherwise).</summary>
    public void Build()
    {
        if (Layout is null || _fleets.Count > 0)
            return;
        var vehicles = CityKit.Load(CityKit.VehiclesPath);
        var rail = CityKit.Load(CityKit.RailPath);
        var rng = new Random(Layout.Seed + 7);

        // Ground cars: one lane each way per street loop, a car every 22–40 m.
        var cars = AddFleet("Cars", vehicles.GetValueOrDefault("Car_Ground"));
        foreach (var loop in Layout.StreetLoops)
        {
            var curve = loop.CreateCurve();
            var length = curve.GetBakedLength();
            var lane = loop.Width * 0.25f;
            foreach (var direction in (ReadOnlySpan<float>)[1f, -1f])
                for (var at = Range(rng, 0f, 20f); at < length - 20f; at += Range(rng, 22f, 40f))
                    AddVehicle(cars, curve, at, direction * Range(rng, 0.85f, 1.15f) * loop.Speed, lane, 0f, 1f);
        }

        // Hover cars on the sky lanes, a few metres apart in height.
        var hover = AddFleet("HoverCars", vehicles.GetValueOrDefault("Car_Hover"));
        foreach (var loop in Layout.SkyLanes)
        {
            if (loop.Name == BalconyLane)
            {
                AddBalconyLane(loop, vehicles.GetValueOrDefault("Car_Hover"), rng);
                continue;
            }

            var curve = loop.CreateCurve();
            var length = curve.GetBakedLength();
            foreach (var direction in (ReadOnlySpan<float>)[1f, -1f])
                for (var at = Range(rng, 0f, 60f); at < length - 60f; at += Range(rng, 70f, 140f))
                    AddVehicle(hover, curve, at, direction * Range(rng, 0.8f, 1.2f) * loop.Speed, 3.5f, Range(rng, -3f, 3f), 1f);
        }

        // The train: four cars nose to tail on the rail's path, the lead car first.
        if (Layout.RailLoop.Points.Count > 2 && rail.GetValueOrDefault("Train_Car") is { } trainCar)
        {
            var path = AddPath("Rail", Layout.RailLoop.CreateCurve(upVectors: true));
            for (var car = 3; car >= 0; car--)
            {
                var follow = AddFollower(path, $"TrainCar{car}", trainCar, 40f + car * 20.5f, Layout.RailLoop.Speed, PathRotationMode.Oriented);
                follow.VOffset = 0.2f;
                if (car == 3)
                    _trainHead = follow;
            }
        }

        // The airships, slow, their screens playing adverts.
        if (vehicles.GetValueOrDefault("Airship") is { } airship)
            for (var i = 0; i < Layout.AirshipLoops.Count; i++)
            {
                var loop = Layout.AirshipLoops[i];
                var path = AddPath("AirshipRoute" + i, loop.CreateCurve());
                _airships.Add(AddFollower(path, "Airship" + i, airship, Range(rng, 0f, path.Curve!.GetBakedLength()), loop.Speed, PathRotationMode.Y));
            }

        foreach (var fleet in _fleets)
        {
            if (fleet.Mesh is null || fleet.Lanes.Count == 0)
                continue;
            fleet.MultiMesh = new MultiMesh { Mesh = fleet.Mesh, InstanceCount = fleet.Lanes.Count };
            // Moving: not part of the baked lighting (it would make every bake stale).
            AddChild(new MultiMeshInstance3D { Name = fleet.Name, Multimesh = fleet.MultiMesh, CastShadows = false, GIMode = GIMode.Disabled });
            VehicleCount += fleet.Lanes.Count;
        }

        VehicleCount += _followers.Count;
        ConvertMaterials(_fleets.Select(f => f.Mesh).Concat(_followers.Select(f => f.GetNodeOrNull<MeshInstance3D>("Body")?.Mesh)).OfType<ArrayMesh>().Distinct());
        Place();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_sounds)
            AddSounds(); // on the first frame: the audio (later in the scene) is ready by then
        Advance(gameTime.DeltaTime);
    }

    /// <summary>
    /// Writes up to <c>into.Length</c> vehicles' positions on the computer's map (0..1 across the city, x east, y south):
    /// the street cars first. Returns how many it wrote. Allocation-free.
    /// </summary>
    public int MapPositions(Span<Vector2> into)
    {
        var count = 0;
        foreach (var fleet in _fleets)
        {
            if (fleet.MultiMesh is not { } multimesh)
                continue;
            for (var i = 0; i < fleet.Lanes.Count && count < into.Length; i++)
                into[count++] = ToMap(multimesh.GetInstanceTransform(i).Origin);
        }

        foreach (var follower in _followers)
            if (count < into.Length)
                into[count++] = ToMap(follower.GlobalPosition);
        return count;
    }

    private static Vector2 ToMap(Vector3 position) =>
        new((position.X + CityLayout.GridExtent) / (2f * CityLayout.GridExtent), (position.Z + CityLayout.GridExtent) / (2f * CityLayout.GridExtent));

    // The hover cars that pass the balcony: followers both ways, a few metres apart in height, each with a whoosh.
    private void AddBalconyLane(CityLoop loop, ArrayMesh? mesh, Random rng)
    {
        if (mesh is null)
            return;
        var path = AddPath("BalconyLane", loop.CreateCurve());
        var length = path.Curve!.GetBakedLength();
        var n = 0;
        foreach (var direction in (ReadOnlySpan<float>)[1f, -1f])
            for (var at = Range(rng, 0f, 60f); at < length - 60f; at += Range(rng, 80f, 150f))
            {
                var follow = AddFollower(path, $"HoverCar{n++}", mesh, at, direction * Range(rng, 0.8f, 1.2f) * loop.Speed, PathRotationMode.Y);
                follow.HOffset = 3.5f * direction; // keep right of the direction of travel
                follow.VOffset = Range(rng, -3f, 3f);
                if (direction < 0)
                    follow.GetNode<Node3D>("Body").RotationDegrees = new Vector3(0f, 180f, 0f);
            }
    }

    private Path3D AddPath(string name, Curve3D curve)
    {
        var path = new Path3D { Name = name, Curve = curve };
        AddChild(path);
        return path;
    }

    private PathFollow3D AddFollower(Path3D path, string name, ArrayMesh mesh, float progress, float speed, PathRotationMode mode)
    {
        var follow = new PathFollow3D { Name = name, Speed = speed, RotationMode = mode, Loop = true };
        follow.AddChild(new MeshInstance3D { Name = "Body", Mesh = mesh, CastShadows = false, GIMode = GIMode.Disabled });
        path.AddChild(follow);
        follow.Progress = progress;
        _followers.Add(follow);
        return follow;
    }

    private void AddSounds()
    {
        _sounds = true;
        if (_trainHead is not null)
            NeonAudio.CreateLoop(NeonSound.TrainRumble, _trainHead).Play();
        foreach (var ship in _airships)
            NeonAudio.CreateLoop(NeonSound.AirshipDrone, ship).Play(fromSeconds: Range(new Random(ship.Name.Length), 0f, 4f));
        foreach (var follower in _followers)
            if (follower.Name.StartsWith("HoverCar", StringComparison.Ordinal))
                follower.AddChild(new FlyBy { Name = "FlyBy" });
    }

    /// <summary>Moves every vehicle on by <paramref name="seconds"/> (allocation-free).</summary>
    public void Advance(float seconds)
    {
        _time += seconds;
        Place();
    }

    private void Place()
    {
        foreach (var fleet in _fleets)
        {
            if (fleet.MultiMesh is not { } multimesh)
                continue;
            for (var i = 0; i < fleet.Lanes.Count; i++)
            {
                var lane = _lanes[fleet.Lanes[i]];
                var offset = lane.Curve.WrapOffset(lane.Start + (float)(_time * lane.Speed));
                var position = lane.Curve.SampleBaked(offset, cubic: true);
                var tangent = lane.Curve.SampleBakedTangent(offset);
                var forward = Vector3.Normalize(new Vector3(tangent.X, 0f, tangent.Z)) * MathF.Sign(lane.Speed);
                var right = Vector3.Cross(forward, Vector3.UnitY);
                // Right-hand traffic: keep right of the direction of travel.
                position += right * lane.Side + new Vector3(0f, lane.Height, 0f);
                var basis = new Basis(right * lane.Scale, Vector3.UnitY * lane.Scale, -forward * lane.Scale);
                multimesh.SetInstanceTransform(i, new Transform3D(basis, position));
            }
        }
    }

    private Fleet AddFleet(string name, ArrayMesh? mesh)
    {
        var fleet = new Fleet(name, mesh);
        _fleets.Add(fleet);
        return fleet;
    }

    private void AddVehicle(Fleet fleet, Curve3D curve, float start, float speed, float side, float height, float scale)
    {
        fleet.Lanes.Add(_lanes.Count);
        _lanes.Add(new Lane(curve, start, speed, side, height, scale));
    }

    /// <summary>The kit's lamps glow (the importer reads emission colours, not their strength); the screens play adverts.</summary>
    private static void ConvertMaterials(IEnumerable<ArrayMesh> meshes)
    {
        var converted = new Dictionary<string, Material>(StringComparer.Ordinal);
        foreach (var mesh in meshes)
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                var name = surface.Material?.ResourceName ?? "";
                if (!converted.TryGetValue(name, out var material))
                    converted[name] = material = Convert(name, surface.Material);
                surface.Material = material;
            }

        static Material Convert(string name, Material? source)
        {
            if (name is "Beam_Head" or "Beam_Tail")
            {
                var beam = new ShaderMaterial { ResourceName = name, Shader = ResourceLoader.Load<Shader>(BeamShader) };
                beam.SetShaderParameter("color", name == "Beam_Head" ? new Vector4(1f, 0.88f, 0.7f, 1f) : new Vector4(1f, 0.06f, 0.04f, 1f));
                beam.SetShaderParameter("energy", name == "Beam_Head" ? 1.4f : 1.1f);
                return beam;
            }

            if (name == "Screen")
            {
                var screen = new ShaderMaterial { ResourceName = "AirshipScreen", Shader = ResourceLoader.Load<Shader>(NeonCity.ScreenShader) };
                screen.SetShaderParameter("words", ResourceLoader.Load<Texture2D>(CityKit.WordsPath));
                screen.SetShaderParameter("seed", 0.61f);
                screen.SetShaderParameter("aspect", 30f / 9f);
                screen.SetShaderParameter("energy", 3f);
                return screen;
            }

            (DrawingColor Colour, float Energy)? glow = name switch
            {
                "Lamp_Head" => (DrawingColor.FromArgb(255, 255, 245, 225), 14f),
                "Lamp_Tail" => (DrawingColor.FromArgb(255, 255, 20, 16), 10f),
                "Lamp" => (DrawingColor.FromArgb(255, 255, 240, 220), 10f),
                "Glow" => (DrawingColor.FromArgb(255, 40, 210, 255), 8f),
                "Windows" => (DrawingColor.FromArgb(255, 255, 196, 130), 3f),
                _ => null,
            };
            if (glow is { } g)
                return new StandardMaterial3D { ResourceName = name, ShadingMode = ShadingMode.Pbr, AlbedoColor = DrawingColor.FromArgb(255, 10, 10, 10), EmissionColor = g.Colour, EmissionEnergy = g.Energy, Roughness = 0.4f };
            return new StandardMaterial3D
            {
                ResourceName = name,
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = (source as StandardMaterial3D)?.AlbedoColor ?? DrawingColor.FromArgb(255, 30, 30, 34),
                Metallic = name is "Body" or "Steel" ? 0.7f : 0f,
                Roughness = name == "Glass" ? 0.05f : 0.3f,
            };
        }
    }

    private static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    /// <summary>
    /// A hover car's pass-by: when the car comes within <see cref="Near"/> metres of the listening camera it plays the
    /// whoosh (with Doppler) once, and re-arms past <see cref="Far"/>.
    /// </summary>
    public sealed class FlyBy : Node3D
    {
        private AudioPlayer3D? _whoosh;
        private bool _armed = true;

        public float Near { get; set; } = 70f;

        public float Far { get; set; } = 110f;

        /// <summary>How many times it played.</summary>
        public int Passes { get; private set; }

        protected override void OnReady()
        {
            _whoosh = NeonAudio.CreatePlayer(NeonSound.FlyingCar, this);
            _whoosh.DopplerTracking = true;
        }

        protected override void OnProcess(in GameTime gameTime)
        {
            if (GetViewport()?.ActiveCamera3D is not { } camera)
                return;
            var distance = Vector3.Distance(camera.GlobalPosition, GlobalPosition);
            if (_armed && distance < Near)
            {
                _armed = false;
                Passes++;
                _whoosh?.Play();
            }
            else if (!_armed && distance > Far)
            {
                _armed = true;
            }
        }
    }

    private readonly record struct Lane(Curve3D Curve, float Start, float Speed, float Side, float Height, float Scale);

    private sealed class Fleet(string name, ArrayMesh? mesh)
    {
        public string Name { get; } = name;

        public ArrayMesh? Mesh { get; } = mesh;

        public List<int> Lanes { get; } = [];

        public MultiMesh? MultiMesh { get; set; }
    }
}
