using System.Numerics;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The World UI scene's gate terminal (G11.6): a document on a <see cref="UiPanel3D"/> whose button opens and closes
/// the gate (it slides up and down). The panel takes the mouse ray, or a crosshair at the screen centre clicked with the
/// <c>use</c> action (<see cref="WorldUiPanel"/> switches them with Tab).
/// </summary>
public sealed class WorldUiTerminal : UiDocument
{
    private const float OpenLift = 3.2f;
    private RmlDataModel? _model;
    private Node3D? _gate;
    private float _gateY;

    public WorldUiTerminal()
    {
        Source = "Assets/WorldUi/terminal.rml";
        AutoFocus = false;
    }

    /// <summary>The scene's gate, raised while <see cref="Open"/>.</summary>
    public NodePath Gate { get; set; } = new("../../../Gate");

    public bool Open
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            _model?.Dirty("open");
        }
    }

    public bool Crosshair
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            _model?.Dirty("crosshair");
        }
    }

    /// <summary>Times the button was pressed.</summary>
    public int Presses { get; private set; }

    protected override void OnReady()
    {
        _gate = GetNodeOrNull<Node3D>(Gate);
        _gateY = _gate?.Position.Y ?? 0f;
        _model = CreateDataModel("terminal")
            .Bind("open", this, static t => t.Open)
            .Bind("crosshair", this, static t => t.Crosshair);
        GetElementById("toggle")!.Click += _ =>
        {
            Presses++;
            Open = !Open;
        };
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_gate is null)
            return;
        var target = _gateY + (Open ? OpenLift : 0f);
        var position = _gate.Position;
        var y = position.Y + Math.Clamp(target - position.Y, -gameTime.DeltaTime * 2.5f, gameTime.DeltaTime * 2.5f);
        if (y != position.Y)
            _gate.Position = new Vector3(position.X, y, position.Z);
    }

    protected override void OnExitTree()
    {
        _gate = null;
        _model = null;
        base.OnExitTree();
    }
}
