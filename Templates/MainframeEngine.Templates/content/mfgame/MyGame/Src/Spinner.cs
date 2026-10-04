using System.Numerics;
using MainframeEngine;

namespace MyGame;

/// <summary>
/// A sample node: a mesh that spins. [Export] properties are saved in scenes and shown in the editor's inspector;
/// the "spin_faster"/"spin_slower" actions come from the input map in project.mfproj.
/// </summary>
public class Spinner : MeshInstance3D
{
    /// <summary>Rotation speed around Y, in degrees per second.</summary>
    [Export(Range = "-360,360,1")]
    public float DegreesPerSecond { get; set; } = 45f;

    protected override void OnProcess(in GameTime gameTime)
    {
        DegreesPerSecond += Input.GetAxis("spin_slower", "spin_faster") * 90f * gameTime.DeltaTime;
        RotationDegrees += new Vector3(0, DegreesPerSecond * gameTime.DeltaTime, 0);
    }
}
