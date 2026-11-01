using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The rain: three <see cref="GpuParticles3D"/> streak emitters in world space. The near one follows the camera along X in
/// a box that starts at the balcony's edge (the ceiling's overhang covers the balcony up to z = 8.1), so it is dense just
/// past the railing and seen through the window wall but never falls indoors; a breeze pushes the drops away from the
/// building. The far one fills the view over the boulevard with longer streaks, and drips run off the overhang's edge.
/// <see cref="Density"/> is the share of the drops that fall (the menu's Rain slider); a light rain by default, thin and
/// dim, so the city reads through it. Allocation-free per frame (it moves two nodes).
/// </summary>
public sealed class NeonRain : Node3D
{
    /// <summary>Where the rain may start: past the overhang over the balcony (scene z).</summary>
    public const float StartZ = 8.3f;

    /// <summary>Past this (scene z) the eye is out on the balcony, right under the drips: they stop rather than fall through the view.</summary>
    public const float DripsEndZ = 6.2f;

    /// <summary>The default <see cref="Density"/>: 1 is a downpour.</summary>
    public const float DefaultDensity = 0.3f;

    private GpuParticles3D? _near;
    private GpuParticles3D? _far;
    private GpuParticles3D? _drips;
    private float _density = DefaultDensity;

    /// <summary>The share of the drops that fall, 0 (dry) to 1 (a downpour).</summary>
    public float Density
    {
        get => _density;
        set
        {
            _density = Math.Clamp(value, 0f, 1f);
            ApplyDensity();
        }
    }

    protected override void OnReady()
    {
        _near = Streaks("Near", 5000, 1.5f, new Vector3(14f, 0.5f, 9f), new Vector2(0.008f, 0.42f), 11f, 0.2f);
        _far = Streaks("Far", 9000, 2.4f, new Vector3(70f, 1f, 60f), new Vector2(0.012f, 0.62f), 13f, 0.1f);
        _drips = Streaks("Drips", 160, 0.9f, new Vector3(7f, 0.02f, 0.03f), new Vector2(0.006f, 0.1f), 2f, 0.3f);
        _drips.Position = new Vector3(0f, 6.0f, 8.08f);
        _drips.ProcessMaterial!.Gravity = ParticleProcessMaterial.DefaultGravity3D;
        _drips.ProcessMaterial.Direction = -Vector3.UnitY;
        ApplyDensity();
        Follow();
    }

    private void ApplyDensity()
    {
        if (_near is null || _far is null || _drips is null)
            return;
        _near.AmountRatio = _far.AmountRatio = _density;
        _drips.AmountRatio = MathF.Min(_density * 2f, 1f);
        _near.Visible = _far.Visible = _density > 0f;
    }

    protected override void OnProcess(in GameTime gameTime) => Follow();

    private void Follow()
    {
        if (GetViewport()?.ActiveCamera3D is not { } camera || _near is null || _far is null)
            return;
        var eye = camera.GlobalPosition;
        _near.Position = new Vector3(eye.X, eye.Y + 8f, StartZ + 9f);
        _far.Position = new Vector3(eye.X, eye.Y + 18f, StartZ + 25f + 60f);
        if (_drips is not null)
            _drips.Visible = _density > 0f && eye.Z < DripsEndZ;
    }

    private GpuParticles3D Streaks(string name, int amount, float lifetime, Vector3 box, Vector2 size, float speed, float alpha)
    {
        var particles = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = lifetime,
            Preprocess = lifetime,
            DrawOrder = GpuParticles3D.DrawOrderEnum.Index,
            VisibilityAabb = new Aabb(new Vector3(-box.X - 4f, -speed * lifetime - 4f, -box.Z - 4f), new Vector3(box.X + 4f, 4f, box.Z + 8f)),
            CastShadows = false,
            DrawPass1 = new QuadMesh { Size = size },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Unshaded,
                Transparency = AlphaMode.Blend,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BillboardMode.Particles,
            },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = box,
                Direction = Vector3.Normalize(new Vector3(0.05f, -1f, 0.1f)),
                Spread = 1.5f,
                InitialVelocityMin = speed * 0.9f,
                InitialVelocityMax = speed * 1.1f,
                Gravity = new Vector3(0f, -2f, 0.4f),
                Color = new Vector4(0.62f, 0.66f, 0.85f, alpha),
                ParticleFlagAlignY = true,
            },
        };
        AddChild(particles);
        return particles;
    }
}
