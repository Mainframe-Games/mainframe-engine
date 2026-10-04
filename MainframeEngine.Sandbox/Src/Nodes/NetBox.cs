using System.Drawing;
using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Sandbox;

/// <summary>
/// The network demo's box (<c>Content/Scenes/NetBox.mscene</c>, which holds its <see cref="BoxMesh"/>): on the server
/// it orbits the origin and spins; its transform and colour are replicated, and clients show them interpolated.
/// </summary>
/// <remarks>Each box gets its own <see cref="StandardMaterial3D"/> on entering the tree, carrying <see cref="Color"/>.</remarks>
public sealed class NetBox : MeshInstance3D
{
    private StandardMaterial3D? _material;

    /// <summary>The box's albedo colour (replicated through <see cref="NetColor"/>).</summary>
    public Color Color
    {
        get;
        set
        {
            field = value;
            _material?.AlbedoColor = value;
        }
    } = Color.White;

    /// <summary>Orbit radius (server only; not replicated).</summary>
    [Export]
    public float OrbitRadius { get; set; } = 2.5f;

    /// <summary>Orbit speed in radians per second (server only).</summary>
    [Export]
    public float OrbitSpeed { get; set; } = 0.8f;

    /// <summary>Angle on the orbit, in radians (server only); set it to place the box.</summary>
    public float Phase
    {
        get => _angle;
        set => _angle = value;
    }

    [Replicated(Interpolate = true)]
    public Vector3 NetPosition
    {
        get => Position;
        set => Position = value;
    }

    [Replicated(Interpolate = true)]
    public Quaternion NetRotation
    {
        get => Rotation;
        set => Rotation = value;
    }

    [Replicated]
    public Color NetColor
    {
        get => Color;
        set => Color = value;
    }

    /// <summary>The number of the last server ping received (shown in the log).</summary>
    public int Pings { get; private set; }

    /// <summary>Server → clients (and the server itself) effect: the box pulses (local scale, not replicated).</summary>
    [Rpc(RpcMode.Server, CallLocal = true)]
    public void Ping(int number)
    {
        Pings = number;
        _pulse = 0.3f;
    }

    private float _angle;
    private float _pulse;

    protected override void OnEnterTree()
    {
        if (MaterialOverride is null)
            MaterialOverride = _material = new StandardMaterial3D { AlbedoColor = Color };
        base.OnEnterTree();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_pulse > 0)
        {
            _pulse = MathF.Max(0, _pulse - gameTime.DeltaTime);
            Scale = new Vector3(1 + _pulse);
        }

        // Only the server simulates; clients receive the result.
        if (Multiplayer is not { IsServer: true })
            return;
        _angle += OrbitSpeed * gameTime.DeltaTime;
        NetPosition = new Vector3(MathF.Cos(_angle) * OrbitRadius, 1 + 0.5f * MathF.Sin(_angle * 2), MathF.Sin(_angle) * OrbitRadius);
        NetRotation = Quaternion.CreateFromYawPitchRoll(_angle * 2, _angle, 0);
    }
}
