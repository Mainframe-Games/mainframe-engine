using System.Numerics;
using MainframeEngine;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>Shows its parent <see cref="Path3D"/> in the game (paths draw only in the editor): a small cube every <see cref="Spacing"/> metres.</summary>
public sealed class PathMarkers3D : Node3D
{
    [Export(Range = "0.1,20,0.1")] public float Spacing { get; set; } = 1f;

    [Export] public Color Color { get; set; } = Color.FromArgb(120, 180, 255);

    protected override void OnReady()
    {
        if (Parent is not Path3D { Curve: { } curve })
            return;
        var mesh = new BoxMesh { Size = new Vector3(0.12f) };
        var material = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Color, EmissionColor = Color, EmissionEnergy = 0.6f };
        var length = curve.GetBakedLength();
        for (var d = 0f; d < length; d += Spacing)
            AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = curve.SampleBaked(d, cubic: true) });
    }
}
