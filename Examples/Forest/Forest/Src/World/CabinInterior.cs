using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The cabin's room as a lighting zone (the game only). The valley's probe volume bakes at 2 m and keeps 85 % of the sky
/// light under any roof (ADR 0170's <c>SkyOcclusion</c> 0.15), so a sealed room would still read as daylit; while the
/// active camera is inside (the player's, a shot's or the free camera), this eases the <see cref="WorldEnvironment"/>'s
/// sky ambient and volumetric haze down over <see cref="BlendSeconds"/>, so the fire is the room's light and the windows
/// glow, and eases them back on the way out. The zone is a box over the room (this node's space, ± <see cref="HalfExtents"/>);
/// the environment's values are captured when the camera steps in, so the pause menu's settings stay theirs. Nothing
/// allocates per frame.
/// </summary>
public sealed class CabinInterior : Node3D
{
    /// <summary>The sky ambient's share inside.</summary>
    [Export(Range = "0,1,0.01")] public float AmbientInside { get; set; } = 0.22f;

    /// <summary>The volumetric fog's share inside (the room's air is clear; the beams through the windows stay faint).</summary>
    [Export(Range = "0,1,0.01")] public float FogInside { get; set; } = 0.3f;

    /// <summary>How long the change takes.</summary>
    [Export(Range = "0.05,5,0.05")] public float BlendSeconds { get; set; } = 0.6f;

    /// <summary>The room's half extents (the zone's box, centred on this node), metres.</summary>
    public Vector3 HalfExtents { get; set; } = new(3f, 1.4f, 2.5f);

    /// <summary>How far inside the camera is, 0 (out) to 1 (in), eased.</summary>
    public float Blend { get; private set; }

    /// <summary>True while the active camera is in the room.</summary>
    public bool CameraInside { get; private set; }

    private WorldEnvironment? _environment;
    private float _ambient, _fog;

    protected override void OnReady()
    {
        if (Tree?.Root.FindChildren<WorldEnvironment>(owned: false) is [var environment, ..])
            _environment = environment;
    }

    /// <summary>Whether world point <paramref name="point"/> lies in the room's box.</summary>
    public bool Contains(Vector3 point)
    {
        var local = GlobalTransform.AffineInverse().TransformPoint(point);
        return MathF.Abs(local.X) <= HalfExtents.X && MathF.Abs(local.Y) <= HalfExtents.Y && MathF.Abs(local.Z) <= HalfExtents.Z;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_environment is not { } environment)
            return;
        var inside = GetViewport()?.ActiveCamera3D is { } camera && Contains(camera.GlobalPosition);
        if (inside && !CameraInside && Blend <= 0f)
        {
            _ambient = environment.AmbientEnergy;
            _fog = environment.VolumetricFogDensity;
        }

        CameraInside = inside;
        var target = inside ? 1f : 0f;
        if (Blend == target)
            return;
        var step = gameTime.DeltaTime / MathF.Max(BlendSeconds, 1e-3f);
        Blend = target > Blend ? MathF.Min(Blend + step, 1f) : MathF.Max(Blend - step, 0f);
        var t = ValleyNoise.SmoothStep(0f, 1f, Blend);
        environment.AmbientEnergy = _ambient * float.Lerp(1f, AmbientInside, t);
        environment.VolumetricFogDensity = _fog * float.Lerp(1f, FogInside, t);
    }
}
