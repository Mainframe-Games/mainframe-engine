using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>A looping emitter orbits the listener: panning, distance attenuation models, low-pass and Doppler.</summary>
public static class Audio3DScene
{
    public static Node Build()
    {
        var root = new Node3D { Name = "audio_3d" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0, 13, 15.5f) });
        camera.LookAt(Vector3.Zero);
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            AmbientColor = new Vector3(0.34f, 0.4f, 0.58f),
        });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.1f, CastsShadows = true, RotationDegrees = new Vector3(-50, 30, 0) });
        Add(root, root, new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(200, 200) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(58, 68, 96) },
        });

        var listener = Add(root, root, new AudioListener3D { Name = "Listener", Current = true, Position = new Vector3(0, 1, 0) });
        Add(root, listener, new MeshInstance3D
        {
            Name = "Head",
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(226, 232, 240) },
        });
        Add(root, listener, new DistanceRing { Name = "Range", Radius = 12f });
        Add(root, listener, new DistanceRing { Name = "OrbitPath", Radius = 6f, Color = new Vector4(1f, 1f, 1f, 0.22f) });

        var orbit = Add(root, root, new Orbiter { Name = "Orbit", Radius = 6f, Position = new Vector3(0, 1, 0) });
        Add(root, orbit, new MeshInstance3D
        {
            Name = "Speaker",
            Mesh = new BoxMesh { Size = new Vector3(0.8f, 1.1f, 0.8f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromArgb(37, 99, 235),
                EmissionColor = Color.FromArgb(37, 99, 235),
                EmissionEnergy = 0.6f,
            },
        });
        Add(root, orbit, new AudioPlayer3D
        {
            Name = "Emitter",
            Stream = new AudioStream { File = "Content/Audio/ambient_hum.ogg", LoadMode = AudioLoadMode.Stream, Loop = true },
            Bus = "SFX",
            VolumeDb = -6f,
            Autoplay = true,
            AttenuationModel = AttenuationModel.Inverse,
            UnitSize = 2f,
            MaxDistance = 12f,
            LowPassAtMaxDistance = 1200f,
            DopplerTracking = true,
        });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Audio3DPanel { Name = "Panel" });
        return root;
    }
}
