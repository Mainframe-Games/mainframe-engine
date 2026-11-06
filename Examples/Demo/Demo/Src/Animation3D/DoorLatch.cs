using System.Numerics;
using MainframeEngine;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// The workshop door's latch in the Animation 3D scene (G1a): its <see cref="AnimationMethodAttribute"/> methods are
/// called by the workshop animation's method track, before the door swings and after it closes; the indicator lamp
/// turns green while unlocked.
/// </summary>
public sealed class DoorLatch : Node3D
{
    private static readonly Color Locked = Color.FromArgb(239, 68, 68);
    private static readonly Color Open = Color.FromArgb(74, 222, 128);

    /// <summary>Times the door was unlocked (tests).</summary>
    public int Unlocks { get; private set; }

    public bool IsUnlocked { get; private set; }

    [AnimationMethod]
    public void Unlock()
    {
        IsUnlocked = true;
        Unlocks++;
        Paint(Open);
    }

    [AnimationMethod]
    public void Lock()
    {
        IsUnlocked = false;
        Paint(Locked);
    }

    private void Paint(Color color)
    {
        if (GetNodeOrNull<OmniLight3D>("Indicator") is { } light)
            light.Color = new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);
        if (GetNodeOrNull<MeshInstance3D>("Bulb") is { MaterialOverride: StandardMaterial3D material })
            material.EmissionColor = color;
    }

    /// <summary>The latch's look in its locked state.</summary>
    public static void Build(Node root, DoorLatch latch)
    {
        DemoBuild.Add(root, latch, new OmniLight3D { Name = "Indicator", Color = new Vector3(Locked.R / 255f, Locked.G / 255f, Locked.B / 255f), Energy = 1.2f, Range = 1.5f, Position = new Vector3(0, 0.05f, 0.1f) });
        DemoBuild.Add(root, latch, new MeshInstance3D
        {
            Name = "Bulb",
            Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Locked, EmissionColor = Locked, EmissionEnergy = 2f },
        });
    }
}
