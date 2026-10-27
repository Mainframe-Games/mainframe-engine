using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Neon;

/// <summary>
/// The walk-in shower: <c>interact</c> on its mixer valve runs the water (streaks from the head down to the tray) and
/// the steam, and slowly mists the shower's glass (<see cref="Mist"/>, eased up over about 20 s and back down when the
/// water stops). This node sits on the shower head; the valve's box is at <see cref="ValvePosition"/>.
/// </summary>
public sealed class Shower : Appliance
{
    private GpuParticles3D? _water;
    private GpuParticles3D? _steam;
    private StandardMaterial3D? _glass;
    private DrawingColor _clearGlass;

    /// <summary>Metres from the head down to the tray.</summary>
    [Export(Range = "0.5,4,0.01")] public float Drop { get; set; } = 2.25f;

    /// <summary>How misted the glass is, 0 (clear) to 1.</summary>
    public float Mist { get; private set; }

    /// <summary>The shower glass's material (this shower's own copy): it mists while the water runs.</summary>
    public StandardMaterial3D? Glass
    {
        get => _glass;
        set
        {
            _glass = value;
            if (value is not null)
                _clearGlass = value.AlbedoColor;
        }
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (_water is not null)
            return;
        AddChild(_water = NeonParticles.Water("Water", Drop, 0.12f, 900, 1.6f));
        AddChild(_steam = NeonParticles.Steam(new Vector3(0.6f, 0.05f, 0.6f)));
        _steam.Position = new Vector3(0f, -Drop, 0f);
    }

    protected override void OnSwitched(bool on)
    {
        if (_water is not null)
            _water.Emitting = on;
        if (_steam is not null)
            _steam.Emitting = on;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        // Mist up over ~20 s while running, clear over ~12 s after.
        var target = IsOn ? 1f : 0f;
        if (Mist == target)
            return;
        var rate = IsOn ? 1f / 20f : 1f / 12f;
        Mist = IsOn ? MathF.Min(Mist + rate * gameTime.DeltaTime, 1f) : MathF.Max(Mist - rate * gameTime.DeltaTime, 0f);
        if (_glass is not null)
        {
            var m = Mist * Mist * (3f - 2f * Mist);
            _glass.AlbedoColor = DrawingColor.FromArgb(
                (int)(_clearGlass.A + (170 - _clearGlass.A) * m),
                (int)(_clearGlass.R + (215 - _clearGlass.R) * m),
                (int)(_clearGlass.G + (220 - _clearGlass.G) * m),
                (int)(_clearGlass.B + (228 - _clearGlass.B) * m));
            _glass.Roughness = 0.04f + 0.5f * m;
        }
    }
}

/// <summary>A tap over a basin: <c>interact</c> runs a thin stream of water.</summary>
public sealed class Tap : Appliance
{
    private GpuParticles3D? _water;

    /// <summary>Metres from the spout down to the basin.</summary>
    [Export(Range = "0.05,1,0.01")] public float Drop { get; set; } = 0.3f;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (_water is null)
            AddChild(_water = NeonParticles.Water("Water", Drop, 0.008f, 220, 0.6f));
    }

    protected override void OnSwitched(bool on)
    {
        if (_water is not null)
            _water.Emitting = on;
    }
}
