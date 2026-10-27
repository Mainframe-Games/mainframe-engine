using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;
using Key = Silk.NET.Input.Key;

namespace Neon;

/// <summary>
/// The computer room's rig: the six monitors' pictures and the keyboard. One 2D sub-viewport (the atlas: graphs, the
/// terminal, the traffic map, the hex dump, the status) feeds five monitors through their UV scale and offset; a 3D
/// sub-viewport with its own little world (a turning wireframe tower) feeds the sixth (ADR 0216). While the player sits
/// at the desk the typed characters go to the <see cref="Terminal"/> (Esc stands up; <c>interact</c> types an "e").
/// </summary>
public sealed class ComputerRig : Node3D
{
    // Which panel each monitor shows: the atlas cell (column, row), or -1 for the 3D view.
    private static readonly int[] MonitorCells = [0, 1, 2, 3, -1, 5];

    private SubViewport? _atlas;
    private SubViewport? _hologram;
    private Node3D? _tower;
    private TerminalPanel? _terminalPanel;

    public ComputerSeat? Seat { get; set; }

    /// <summary>A key was typed at the terminal (the audio clicks).</summary>
    [Signal] public event Action? KeyPressed;

    /// <summary>The keyboard on the desk (world space), for its clicks.</summary>
    public Vector3 KeyboardPosition => Seat is { } seat ? seat.GlobalPosition + new Vector3(-0.95f, 0.78f, 0f) : GlobalPosition;

    public Terminal? Terminal { get; private set; }

    public MapPanel? Map { get; private set; }

    /// <summary>The monitors' screen materials (the apartment's own copies), in model order.</summary>
    public List<StandardMaterial3D> Monitors { get; } = [];

    /// <summary>Wires the screens, the terminal and the seat.</summary>
    public void Setup(ITerminalHost host, Stereo? stereo)
    {
        Terminal = new Terminal(host);
        Terminal.Boot();
        Terminal.ExitRequested += () => Seat?.StandUp();

        _atlas = new SubViewport
        {
            Name = "Atlas",
            Width = Screens.CellWidth * 3,
            Height = Screens.CellHeight * 2,
            Disable3D = true,
            ClearColor = DrawingColor.FromArgb(255, 5, 8, 15),
        };
        AddChild(_atlas);
        Node2D Cell(Node2D panel, int column, int row)
        {
            panel.Position = new Vector2(column * Screens.CellWidth, row * Screens.CellHeight);
            _atlas!.AddChild(panel);
            return panel;
        }

        Cell(new GraphPanel { Name = "Graphs" }, 0, 0);
        _terminalPanel = (TerminalPanel)Cell(new TerminalPanel { Name = "Terminal", Terminal = Terminal }, 1, 0);
        Map = (MapPanel)Cell(new MapPanel { Name = "Map" }, 2, 0);
        Cell(new HexPanel { Name = "Hex" }, 0, 1);
        Cell(new StatusPanel { Name = "Status", Stereo = stereo }, 2, 1);

        _hologram = new SubViewport
        {
            Name = "Hologram",
            Width = Screens.CellWidth,
            Height = Screens.CellHeight,
            ClearColor = DrawingColor.FromArgb(255, 4, 6, 14),
        };
        _hologram.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0f, 1.6f, 4.2f), RotationDegrees = new Vector3(-14f, 0f, 0f), Fov = 45f, Current = true });
        _tower = BuildTower();
        _hologram.AddChild(_tower);
        AddChild(_hologram);

        var atlasTexture = _atlas.GetTexture();
        var hologramTexture = _hologram.GetTexture();
        for (var i = 0; i < Monitors.Count; i++)
        {
            var screen = Monitors[i];
            var cell = i < MonitorCells.Length ? MonitorCells[i] : 0;
            screen.AlbedoColor = DrawingColor.Black;
            screen.EmissionColor = DrawingColor.White;
            screen.EmissionEnergy = 2.2f;
            if (cell < 0)
            {
                screen.EmissionTexture = hologramTexture;
                continue;
            }

            screen.EmissionTexture = atlasTexture;
            screen.UvScale = new Vector2(1f / 3f, 1f / 2f);
            screen.UvOffset = new Vector2(cell % 3 / 3f, cell / 3 / 2f);
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_tower is not null)
            _tower.RotationDegrees += new Vector3(0f, gameTime.DeltaTime * 20f, 0f);
        if (_terminalPanel is not null)
            _terminalPanel.Active = Seat is { IsSeated: true };
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        if (Seat is not { IsSeated: true } || Terminal is not { } terminal || Tree?.EditMode == true)
            return;
        switch (inputEvent)
        {
            case InputEventText text:
                terminal.Type(text.Character);
                KeyPressed?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Key: Key.Backspace }:
                terminal.Backspace();
                KeyPressed?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Key: Key.Enter or Key.KeypadEnter }:
                terminal.Enter();
                KeyPressed?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
        }
    }

    /// <summary>A tower of glowing wireframe floors (thin emissive bars) turning on the spot.</summary>
    private static Node3D BuildTower()
    {
        var tower = new Node3D { Name = "Tower" };
        var cyan = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = DrawingColor.FromArgb(255, 60, 230, 255) };
        var pink = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = DrawingColor.FromArgb(255, 255, 70, 190) };
        var bar = new BoxMesh { Size = new Vector3(1f, 0.02f, 0.02f) };
        var post = new BoxMesh { Size = new Vector3(0.02f, 1f, 0.02f) };
        void Add(Mesh mesh, Material material, Vector3 position, Vector3 scale, float yaw = 0f) =>
            tower.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position, Scale = scale, RotationDegrees = new Vector3(0, yaw, 0) });

        for (var floor = 0; floor <= 12; floor++)
        {
            var y = floor * 0.22f;
            var half = floor > 8 ? 0.35f : 0.6f;
            var material = floor == 9 ? pink : cyan; // the apartment's floor
            Add(bar, material, new Vector3(0, y, half), new Vector3(half * 2, 1, 1));
            Add(bar, material, new Vector3(0, y, -half), new Vector3(half * 2, 1, 1));
            Add(bar, material, new Vector3(half, y, 0), new Vector3(half * 2, 1, 1), 90);
            Add(bar, material, new Vector3(-half, y, 0), new Vector3(half * 2, 1, 1), 90);
        }

        foreach (var (half, top) in new[] { (0.6f, 1.98f), (0.35f, 2.64f) })
            foreach (var (sx, sz) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                Add(post, cyan, new Vector3(sx * half, top / 2, sz * half), new Vector3(1, top, 1));
        Add(post, pink, new Vector3(0, 3.0f, 0), new Vector3(1, 0.7f, 1)); // the antenna
        return tower;
    }
}
