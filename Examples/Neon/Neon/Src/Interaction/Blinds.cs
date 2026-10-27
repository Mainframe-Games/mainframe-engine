using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Motorised venetian blinds over a stretch of the window wall: a row of slats hanging from this node (the top of the
/// window, its −Z facing into the room). Closed, the slats hang flat and spread down to <see cref="Drop"/>; open, they
/// turn level and stack up under the head rail. The move is eased over <see cref="Seconds"/>; nothing allocates per frame.
/// A <see cref="BlindsPanel"/> on a wall drives a set of them.
/// </summary>
public sealed class Blinds : Node3D
{
    private const float SlatPitch = 0.07f;
    private const float StackPitch = 0.012f;
    private readonly List<Node3D> _slats = [];
    private float _openness = 1f;

    /// <summary>The blinds' width, metres along X.</summary>
    [Export(Range = "0.2,20,0.01")] public float Width { get; set; } = 2f;

    /// <summary>How far the closed blinds hang below the head rail, metres.</summary>
    [Export(Range = "0.2,10,0.01")] public float Drop { get; set; } = 2.6f;

    /// <summary>Seconds for a full open or close.</summary>
    [Export(Range = "0.1,20,0.1")] public float Seconds { get; set; } = 3.5f;

    /// <summary>True when open (or opening).</summary>
    [Export] public bool IsOpen { get; set; } = true;

    /// <summary>The move, 0 (closed) to 1 (open).</summary>
    public float Openness => _openness;

    /// <summary>The slats' material (shared by the apartment's blinds).</summary>
    public Material? SlatMaterial { get; set; }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (_slats.Count > 0)
            return;
        var rail = new MeshInstance3D
        {
            Name = "HeadRail",
            Mesh = new BoxMesh { Size = new Vector3(Width + 0.04f, 0.05f, 0.06f) },
            MaterialOverride = SlatMaterial,
            Position = new Vector3(0f, -0.025f, 0f),
        };
        AddChild(rail);
        var mesh = new BoxMesh { Size = new Vector3(Width, 0.004f, SlatPitch * 1.08f) };
        var count = Math.Max(2, (int)(Drop / SlatPitch));
        for (var i = 0; i < count; i++)
        {
            var slat = new MeshInstance3D { Name = $"Slat{i}", Mesh = mesh, MaterialOverride = SlatMaterial };
            AddChild(slat);
            _slats.Add(slat);
        }

        _openness = IsOpen ? 1f : 0f;
        Place();
    }

    /// <summary>Opens or closes the blinds (eased).</summary>
    public void SetOpen(bool open) => IsOpen = open;

    protected override void OnProcess(in GameTime gameTime)
    {
        var target = IsOpen ? 1f : 0f;
        if (_openness == target)
            return;
        var step = gameTime.DeltaTime / MathF.Max(Seconds, 1e-3f);
        _openness = target > _openness ? MathF.Min(_openness + step, 1f) : MathF.Max(_openness - step, 0f);
        Place();
    }

    private void Place()
    {
        var e = DoorBase.SmoothStep(_openness);
        // Closed: hanging (turned 80° from level, nearly flat against the glass) and spread; open: level and stacked.
        var turn = float.DegreesToRadians(80f * (1f - e));
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, turn);
        for (var i = 0; i < _slats.Count; i++)
        {
            var y = -0.06f - i * (SlatPitch + (StackPitch - SlatPitch) * e);
            _slats[i].Position = new Vector3(0f, y, 0f);
            _slats[i].Rotation = rotation;
        }
    }
}

/// <summary>A wall panel that opens or closes a set of <see cref="Blinds"/> together.</summary>
public sealed class BlindsPanel : Appliance
{
    /// <summary>The blinds it drives.</summary>
    public List<Blinds> Blinds { get; } = [];

    public override string Verb => IsOn ? Verbs.CloseBlinds : Verbs.OpenBlinds;

    /// <summary>On = open. Starts in the state of its first blinds.</summary>
    protected override void OnSwitched(bool on)
    {
        foreach (var blinds in Blinds)
            blinds.SetOpen(on);
    }
}
