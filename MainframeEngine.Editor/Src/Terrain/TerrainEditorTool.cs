using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>The Terrain dock's mode tabs.</summary>
public enum TerrainDockMode : byte
{
    Sculpt,
    Paint,
    Water,

    /// <summary>Painted foliage density of one <see cref="FoliageType"/> (types 0–7).</summary>
    Foliage,
}

/// <summary>What Paint writes.</summary>
public enum TerrainPaintTarget : byte
{
    /// <summary>Faceted surface ids, or Realistic splat layers.</summary>
    Surface,

    /// <summary>One of the four user channels.</summary>
    Channel,
}

/// <summary>
/// The Terrain dock (G8a, docs/design/terrain.md#editing): while a <see cref="Terrain3D"/> is the single selection, the
/// right dock's Terrain tab creates its data and the view's left button sculpts (Raise, Lower, Smooth, Flatten, Set
/// height, Ramp), paints (Faceted surfaces or Realistic layers, user channels, rectangle fills) and lays water depth with
/// a brush ring draped on the ground. Shift inverts (Smooth while sculpting, the eraser while painting, dry while laying
/// water); Cmd/Ctrl+click picks what is under the cursor; <c>[</c> <c>]</c> resize, <c>-</c> <c>=</c> change strength
/// (also Alt+wheel and Shift+wheel); Esc steps out (ramp start, rectangle). Every stroke is one undo entry
/// (<see cref="TerrainEditAction"/>); the scene save writes the changed layer PNGs (<c>Terrain3D</c>'s save hook).
/// </summary>
[EditorViewportTool(typeof(Terrain3D), Title = "Terrain", Icon = "mountain")]
public sealed class TerrainEditorTool : IEditorViewportTool
{
    private const int RingPoints = 48;
    private const float RepeatSeconds = 0.1f;
    private const float RingLift = 0.05f;

    private static readonly Vector4 RingColor = new(0.35f, 0.65f, 1f, 1f);
    private static readonly Vector4 InvertColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 PickColor = new(0.4f, 0.95f, 0.45f, 1f);

    private readonly TerrainBrushOps _ops = new();
    private readonly TerrainStroke _stroke = new();
    private readonly List<Vector2> _stamps = [];
    private IViewportToolContext? _context;
    private Terrain3D? _terrain;
    private int _dockVersion;

    // Hover: where the pointer's ray meets the ground (world).
    private bool _hover;
    private Vector3 _hit;
    private EditorModifiers _modifiers;

    // The stroke in progress.
    private bool _editing;
    private float _flattenTarget;
    private float _still; // seconds since the last stamp
    private Vector2 _lastStamp;
    private TerrainSculptTool _strokeTool;

    // Two-click ramp and rectangle fills (terrain-local XZ, heights terrain-local).
    private Vector3? _rampStart;
    private Vector2? _rectStart;

    // The cursor label's last values (reformatted only when one changes).
    private (int Tool, int Size, int Strength, int Height, int Slope, bool Shift) _labelKey = (-1, 0, 0, 0, 0, false);

    // The create form.
    private TerrainProfile _createProfile = TerrainProfile.Faceted;
    private int _createSize = 128;
    private float _createSpacing = 2f;

    public TerrainBrush Brush { get; } = new();

    public TerrainDockMode Mode
    {
        get;
        set
        {
            if (field == value)
                return;
            // Strength is remembered per mode group: sculpting is gentle, painting full.
            if (field == TerrainDockMode.Sculpt)
                SculptStrength = Brush.Strength;
            else
                PaintStrength = Brush.Strength;
            field = value;
            Brush.Strength = value == TerrainDockMode.Sculpt ? SculptStrength : PaintStrength;
            _rampStart = null;
            _rectStart = null;
            Touch();
        }
    }

    public float SculptStrength { get; private set; } = 0.25f;
    public float PaintStrength { get; private set; } = 1f;

    public TerrainSculptTool SculptTool
    {
        get;
        set
        {
            field = value;
            _rampStart = null;
            Touch();
        }
    }

    /// <summary>Set height's target (terrain-local metres).</summary>
    public float TargetHeight { get; set; }

    public TerrainPaintTarget PaintTarget { get; set; }

    /// <summary>The surface id (Faceted) or layer (Realistic) Paint lays.</summary>
    public int Surface { get; set; } = 1;

    /// <summary>What Shift paints.</summary>
    public int EraserSurface { get; set; }

    public int Channel { get; set; }
    public TerrainChannelMode ChannelMode { get; set; }
    public byte ChannelValue { get; set; } = 255;

    /// <summary>Paint and Water fill a dragged rectangle (snapped to cells or vertices) instead of brushing.</summary>
    public bool Rectangle { get; set; }

    /// <summary>The foliage type (index in <see cref="TerrainData.FoliageTypes"/>, 0–7) the Foliage brush paints.</summary>
    public int FoliageType { get; set; }

    /// <summary>The density (0..1) the Foliage brush paints toward (Shift: 0).</summary>
    public float FoliageDensity { get; set; } = 1f;

    /// <summary>The water depth (metres above the bed) the Water brush lays.</summary>
    public float WaterLevel { get; set; } = 0.6f;

    /// <summary>The edited terrain.</summary>
    public Terrain3D? Terrain => _terrain;

    /// <summary>Whether the pointer is over the ground, and where (world).</summary>
    public bool Hovering => _hover;

    public Vector3 HoverPoint => _hit;

    /// <summary>Whether a ramp's first point is placed.</summary>
    public bool HasRampStart => _rampStart is not null;

    /// <summary>The dock's own state, and whether the terrain has data (an undone Create shows the form again).</summary>
    public int DockVersion => HashCode.Combine(_dockVersion, _terrain?.Data);

    private void Touch() => _dockVersion++;

    public bool Handles(Node node) => node is Terrain3D;

    public void Activate(IViewportToolContext context, Node node)
    {
        _context = context;
        _terrain = (Terrain3D)node;
        _hover = false;
        _rampStart = null;
        _rectStart = null;
        _labelKey = (-1, 0, 0, 0, 0, false);
        if (_terrain.Data is { } data && Surface >= SurfaceCount(data))
            Surface = 0;
        Touch();
    }

    public void Deactivate()
    {
        EndStroke();
        _context?.SetCursorLabel(null);
        _context = null;
        _terrain = null;
        _hover = false;
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    public ViewportToolResult OnInput(in ViewportToolInput input)
    {
        if (_terrain is not { } terrain || _context is null)
            return ViewportToolResult.Pass;
        _modifiers = input.Modifiers;
        switch (input.Kind)
        {
            case ViewportToolInputKind.Motion:
                UpdateHover(terrain, input);
                if (_editing)
                    StrokeTo(terrain, Local(terrain, _hit));

                UpdateLabel(terrain);
                return input.Captured || _hover ? ViewportToolResult.Consumed : ViewportToolResult.Pass;
            case ViewportToolInputKind.Press when input.Button == MouseButton.Left:
                UpdateHover(terrain, input);
                if (terrain.Data is null || !_hover)
                    return ViewportToolResult.Pass;
                if (input.Command)
                {
                    Pick(terrain);
                    return ViewportToolResult.Consumed;
                }

                Press(terrain);
                UpdateLabel(terrain);
                return ViewportToolResult.Consumed;
            case ViewportToolInputKind.Release when input.Button == MouseButton.Left:
                UpdateHover(terrain, input);
                if (_rectStart is { } start && Mode != TerrainDockMode.Sculpt)
                {
                    FillRectangle(terrain, start, Local(terrain, _hit));
                    _rectStart = null;
                }

                EndStroke();
                UpdateLabel(terrain);
                return ViewportToolResult.Consumed;
            case ViewportToolInputKind.Cancel:
                _rectStart = null;
                EndStroke(); // keep what was applied: one undo entry
                return ViewportToolResult.Consumed;
            case ViewportToolInputKind.Wheel:
                if (input.Alt)
                    Brush.Size *= MathF.Pow(1.15f, input.WheelDelta);
                else
                    Brush.Strength += 0.05f * MathF.Sign(input.WheelDelta);
                Touch();
                UpdateLabel(terrain);
                return ViewportToolResult.Consumed;
            case ViewportToolInputKind.KeyDown:
                return Key(terrain, input.Key);
            default:
                return ViewportToolResult.Pass;
        }
    }

    private ViewportToolResult Key(Terrain3D terrain, Key key)
    {
        switch (key)
        {
            case Silk.NET.Input.Key.LeftBracket:
                Brush.Size /= 1.25f;
                break;
            case Silk.NET.Input.Key.RightBracket:
                Brush.Size *= 1.25f;
                break;
            case Silk.NET.Input.Key.Minus:
                Brush.Strength -= 0.05f;
                break;
            case Silk.NET.Input.Key.Equal:
                Brush.Strength += 0.05f;
                break;
            case Silk.NET.Input.Key.Escape when _rampStart is not null || _rectStart is not null:
                _rampStart = null;
                _rectStart = null;
                break;
            default:
                return ViewportToolResult.Pass;
        }

        Touch();
        UpdateLabel(terrain);
        return ViewportToolResult.Consumed;
    }

    private void UpdateHover(Terrain3D terrain, in ViewportToolInput input)
    {
        _hover = false;
        if (terrain.Data is null || !input.InView && !input.Captured)
            return;
        if (terrain.Raycast(input.RayOrigin, input.RayDirection, 100_000f, out var hit))
        {
            _hover = true;
            _hit = hit.Position;
        }
    }

    private static Vector2 Local(Terrain3D terrain, Vector3 world)
    {
        var o = terrain.GlobalPosition;
        return new Vector2(world.X - o.X, world.Z - o.Z);
    }

    private bool Inverted => (_modifiers & EditorModifiers.Shift) != 0;

    private void Press(Terrain3D terrain)
    {
        var data = terrain.Data!;
        var local = Local(terrain, _hit);
        if (Mode == TerrainDockMode.Sculpt && SculptTool == TerrainSculptTool.Ramp)
        {
            var height = _hit.Y - terrain.GlobalPosition.Y;
            if (_rampStart is not { } start)
            {
                _rampStart = new Vector3(local.X, height, local.Y);
                return;
            }

            Record(terrain, TerrainLayers.Height, "Terrain Ramp",
                () => _ops.Ramp(data, Brush, new Vector2(start.X, start.Z), start.Y, local, height));
            _rampStart = null;
            return;
        }

        if (Rectangle && Mode != TerrainDockMode.Sculpt)
        {
            _rectStart = local;
            return;
        }

        _strokeTool = Inverted && SculptTool is TerrainSculptTool.Raise or TerrainSculptTool.Lower or TerrainSculptTool.Flatten or TerrainSculptTool.SetHeight
            ? TerrainSculptTool.Smooth
            : SculptTool;
        terrain.Edit.Begin(LayersOf(Mode));
        _editing = true;
        _still = 0f;
        _flattenTarget = Mode == TerrainDockMode.Sculpt && _strokeTool == TerrainSculptTool.Flatten ? _ops.WeightedMeanHeight(data, Brush, local) : 0f;
        Stamp(terrain, _stroke.Begin(local));
    }

    private void StrokeTo(Terrain3D terrain, Vector2 local)
    {
        _stamps.Clear();
        _stroke.MoveTo(local, Brush.StampDistance, _stamps);
        foreach (var stamp in _stamps)
            Stamp(terrain, stamp);
    }

    // One stamp of the current mode at terrain-local point p.
    private void Stamp(Terrain3D terrain, Vector2 p)
    {
        var data = terrain.Data!;
        _lastStamp = p;
        _still = 0f; // the time since the last stamp
        switch (Mode)
        {
            case TerrainDockMode.Sculpt:
                _ops.Sculpt(data, Brush, _strokeTool, p, _strokeTool == TerrainSculptTool.Flatten ? _flattenTarget : TargetHeight);
                break;
            case TerrainDockMode.Paint when PaintTarget == TerrainPaintTarget.Channel:
                _ops.PaintChannel(terrain, data, Brush, p, Channel, ChannelMode, Inverted ? (byte)0 : ChannelValue);
                break;
            case TerrainDockMode.Paint when data.Profile == TerrainProfile.Faceted:
                _ops.PaintSurface(terrain, data, Brush, p, Inverted ? EraserSurface : Surface);
                break;
            case TerrainDockMode.Paint:
                _ops.PaintLayer(terrain, data, Brush, p, Inverted ? EraserSurface : Surface);
                break;
            case TerrainDockMode.Water:
                _ops.PaintWater(data, Brush, p, Inverted ? 0f : WaterLevel);
                break;
            case TerrainDockMode.Foliage:
                _ops.PaintFoliage(terrain, data, Brush, p, FoliageType, Inverted ? 0f : FoliageDensity);
                break;
        }
    }

    public void Process(float deltaSeconds)
    {
        // Height tools keep working while the button is held: a stamp under the pointer whenever 0.1 s passed without one
        // (holding still raises a hill; a slow drag is not weaker than a fast one). Paint converges in one pass.
        if (!_editing || _terrain is not { } terrain || Mode != TerrainDockMode.Sculpt)
            return;
        _still += deltaSeconds;
        if (_still >= RepeatSeconds)
            Stamp(terrain, _hover ? Local(terrain, _hit) : _lastStamp); // resets the timer
    }

    private void EndStroke()
    {
        _stroke.End();
        if (!_editing)
            return;
        _editing = false;
        if (_terrain is not { } terrain || !terrain.Edit.IsActive)
            return;
        Commit(terrain, terrain.Edit.End(), StrokeName());
    }

    private string StrokeName() => Mode switch
    {
        TerrainDockMode.Sculpt => "Terrain " + SculptName(_strokeTool),
        TerrainDockMode.Water => "Terrain Water",
        TerrainDockMode.Foliage => "Terrain Foliage",
        _ => PaintTarget == TerrainPaintTarget.Channel ? "Terrain Paint Channel" : "Terrain Paint",
    };

    private void FillRectangle(Terrain3D terrain, Vector2 a, Vector2 b)
    {
        var data = terrain.Data!;
        if (Mode == TerrainDockMode.Foliage)
        {
            var area = TerrainBrushOps.CellRectBetween(data, a, b);
            Record(terrain, TerrainLayers.Foliage, "Terrain Foliage Fill",
                () => _ops.PaintFoliage(terrain, data, Brush, default, FoliageType, Inverted ? 0f : FoliageDensity, area));
            return;
        }

        if (Mode == TerrainDockMode.Water)
        {
            var vertices = TerrainBrushOps.VertexRectBetween(data, a, b);
            Record(terrain, TerrainLayers.Water, "Terrain Water Fill",
                () => _ops.PaintWater(data, Brush, default, Inverted ? 0f : WaterLevel, vertices));
            return;
        }

        var cells = TerrainBrushOps.CellRectBetween(data, a, b);
        if (PaintTarget == TerrainPaintTarget.Channel)
            Record(terrain, TerrainLayers.User, "Terrain Fill Channel",
                () => _ops.PaintChannel(terrain, data, Brush, default, Channel, ChannelMode, Inverted ? (byte)0 : ChannelValue, cells));
        else if (data.Profile == TerrainProfile.Faceted)
            Record(terrain, TerrainLayers.Surface, "Terrain Fill",
                () => _ops.PaintSurface(terrain, data, Brush, default, Inverted ? EraserSurface : Surface, cells));
        else
            Record(terrain, TerrainLayers.Surface, "Terrain Fill",
                () => _ops.PaintLayer(terrain, data, Brush, default, Inverted ? EraserSurface : Surface, cells));
    }

    // A one-shot edit (ramp, fill) as one history entry.
    private void Record(Terrain3D terrain, TerrainLayers layers, string name, Action apply)
    {
        terrain.Edit.Begin(layers);
        try
        {
            apply();
        }
        finally
        {
            Commit(terrain, terrain.Edit.End(), name);
        }
    }

    private void Commit(Terrain3D terrain, TerrainEditRecord record, string name)
    {
        if (record.Tiles.Count == 0 || _context is null)
            return;
        _context.History.Commit(new TerrainEditAction(terrain, record, name), alreadyApplied: true);
    }

    private TerrainLayers LayersOf(TerrainDockMode mode) => mode switch
    {
        TerrainDockMode.Sculpt => TerrainLayers.Height,
        TerrainDockMode.Water => TerrainLayers.Water,
        TerrainDockMode.Foliage => TerrainLayers.Foliage,
        _ => PaintTarget == TerrainPaintTarget.Channel ? TerrainLayers.User : TerrainLayers.Surface,
    };

    // Cmd/Ctrl+click: the value under the cursor becomes the tool's.
    private void Pick(Terrain3D terrain)
    {
        var data = terrain.Data!;
        switch (Mode)
        {
            case TerrainDockMode.Sculpt:
                TargetHeight = MathF.Round((_hit.Y - terrain.GlobalPosition.Y) * 100f) / 100f;
                if (SculptTool != TerrainSculptTool.SetHeight)
                    SculptTool = TerrainSculptTool.SetHeight;
                break;
            case TerrainDockMode.Paint when PaintTarget == TerrainPaintTarget.Channel:
                ChannelValue = terrain.UserChannelAt(Channel, _hit.X, _hit.Z);
                break;
            case TerrainDockMode.Paint:
                Surface = Math.Min(terrain.SurfaceAt(_hit.X, _hit.Z, ignoreWater: true), SurfaceCount(data) - 1);
                break;
            case TerrainDockMode.Water:
                WaterLevel = MathF.Round(terrain.WaterDepthAt(_hit.X, _hit.Z) * 100f) / 100f;
                break;
            case TerrainDockMode.Foliage:
                var local = Local(terrain, _hit);
                FoliageDensity = MathF.Round(data.GetFoliageDensity(FoliageType, (int)(local.X / data.CellSize), (int)(local.Y / data.CellSize)) * 100f) / 100f;
                break;
        }

        Touch();
    }

    // ── Overlay and label ────────────────────────────────────────────────────────────────────────────────────────

    public void DrawOverlay(DebugLines overlay)
    {
        if (_terrain is not { Data: not null } terrain)
            return;
        var color = (_modifiers & EditorModifiers.Command) != 0 ? PickColor : Inverted ? InvertColor : RingColor;
        if (_rampStart is { } start)
        {
            var o = terrain.GlobalPosition;
            var a = o + start;
            overlay.AddLine(a, _hover ? _hit : a + Vector3.UnitY, color);
            DrawRing(overlay, terrain, new Vector2(start.X, start.Z), Brush.Radius, color);
        }

        if (_rectStart is { } corner && _hover)
        {
            DrawRect(overlay, terrain, corner, Local(terrain, _hit), color);
            return;
        }

        if (!_hover)
            return;
        var center = Local(terrain, _hit);
        DrawRing(overlay, terrain, center, Brush.Radius, color);
        if (Brush.Falloff is > 0f and < 1f)
            DrawRing(overlay, terrain, center, Brush.Radius * (1f - Brush.Falloff), color with { W = 0.45f });
    }

    private void DrawRing(DebugLines overlay, Terrain3D terrain, Vector2 center, float radius, Vector4 color)
    {
        var previous = Vector3.Zero;
        for (var i = 0; i <= RingPoints; i++)
        {
            Vector2 p;
            if (Brush.Shape == TerrainBrushShape.Circle)
            {
                var angle = i * (MathF.Tau / RingPoints);
                p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }
            else
                p = center + SquarePoint(i % RingPoints, radius);

            var point = Drape(terrain, p);
            if (i > 0)
                overlay.AddLine(previous, point, color);
            previous = point;
        }
    }

    // Points around a square's perimeter, RingPoints in all, starting at its +X edge's middle.
    private static Vector2 SquarePoint(int i, float r)
    {
        const int perSide = RingPoints / 4;
        var side = i / perSide;
        var t = (i % perSide) / (float)perSide * 2f - 1f;
        return side switch
        {
            0 => new Vector2(r, t * r),
            1 => new Vector2(-t * r, r),
            2 => new Vector2(-r, -t * r),
            _ => new Vector2(t * r, -r),
        };
    }

    private static void DrawRect(DebugLines overlay, Terrain3D terrain, Vector2 a, Vector2 b, Vector4 color)
    {
        var c0 = Vector2.Min(a, b);
        var c1 = Vector2.Max(a, b);
        Span<Vector2> corners = [c0, new Vector2(c1.X, c0.Y), c1, new Vector2(c0.X, c1.Y)];
        for (var side = 0; side < 4; side++)
        {
            var from = corners[side];
            var to = corners[(side + 1) % 4];
            var previous = Drape(terrain, from);
            for (var k = 1; k <= 12; k++)
            {
                var point = Drape(terrain, Vector2.Lerp(from, to, k / 12f));
                overlay.AddLine(previous, point, color);
                previous = point;
            }
        }
    }

    private static Vector3 Drape(Terrain3D terrain, Vector2 local)
    {
        var o = terrain.GlobalPosition;
        var size = terrain.Data!.Grid.Size;
        var x = Math.Clamp(local.X, 0f, size);
        var z = Math.Clamp(local.Y, 0f, size);
        return new Vector3(o.X + x, terrain.HeightAt(o.X + x, o.Z + z) + RingLift, o.Z + z);
    }

    // "Raise · 16 m · 25 % · h 3.4 m · 12°", reformatted only when a shown value changes.
    private void UpdateLabel(Terrain3D terrain)
    {
        if (_context is null)
            return;
        if (!_hover || terrain.Data is null)
        {
            if (_labelKey.Tool != -1)
                _context.SetCursorLabel(null);
            _labelKey = (-1, 0, 0, 0, 0, false);
            return;
        }

        var height = _hit.Y - terrain.GlobalPosition.Y;
        var n = terrain.NormalAt(_hit.X, _hit.Z);
        var slope = MathF.Acos(Math.Clamp(n.Y, -1f, 1f)) * (180f / MathF.PI);
        var tool = Mode == TerrainDockMode.Sculpt ? (int)SculptTool : 10 + (int)Mode;
        var key = (tool, (int)MathF.Round(Brush.Size * 10f), (int)MathF.Round(Brush.Strength * 100f), (int)MathF.Round(height * 10f),
            (int)MathF.Round(slope), Inverted);
        if (key == _labelKey)
            return;
        _labelKey = key;
        var c = CultureInfo.InvariantCulture;
        var name = Mode switch
        {
            TerrainDockMode.Sculpt => Inverted && SculptTool != TerrainSculptTool.Ramp ? "Smooth" : SculptName(SculptTool),
            TerrainDockMode.Water => Inverted ? "Dry" : "Water",
            TerrainDockMode.Foliage => Inverted ? "Clear foliage" : "Foliage",
            _ => Inverted ? "Erase" : "Paint",
        };
        var text = string.Create(c, $"{name} · {Brush.Size:0.#} m · {Brush.Strength * 100f:0} % · h {height:0.0} m · {slope:0}°");
        if (_rampStart is not null)
            text += " · click the ramp's end";
        _context.SetCursorLabel(text);
    }

    private static string SculptName(TerrainSculptTool tool) => tool switch
    {
        TerrainSculptTool.Raise => "Raise",
        TerrainSculptTool.Lower => "Lower",
        TerrainSculptTool.Smooth => "Smooth",
        TerrainSculptTool.Flatten => "Flatten",
        TerrainSculptTool.SetHeight => "Set height",
        _ => "Ramp",
    };

    // ── Dock ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int SurfaceCount(TerrainData data) => data.Profile == TerrainProfile.Faceted ? TerrainMaterial3D.MaxSurfaces : TerrainData.MaxLayers;

    public string BuildDockRml()
    {
        var rml = new StringBuilder(4096);
        if (_terrain is not { } terrain)
            return "";
        if (terrain.Data is not { } data)
        {
            AppendCreate(rml);
            return rml.ToString();
        }

        rml.Append("<div class=\"td-modes\">");
        ModeButton(rml, TerrainDockMode.Sculpt, "mountain", "Sculpt — raise, lower, smooth, flatten, set height and ramps");
        ModeButton(rml, TerrainDockMode.Paint, "brush", data.Profile == TerrainProfile.Faceted
            ? "Paint — surfaces (palette ids) and user channels"
            : "Paint — splat layers and user channels");
        ModeButton(rml, TerrainDockMode.Water, "droplet", "Water — lay water depth over the bed (ponds and lakes)");
        ModeButton(rml, TerrainDockMode.Foliage, "plant-2", "Foliage — paint where each foliage type grows (grass, ferns, flowers)");
        rml.Append("<span class=\"td-info\">").Append(data.Profile == TerrainProfile.Faceted ? "Faceted" : "Realistic").Append(" · ")
            .Append(data.SizeMeters).Append(" m · ").Append(Num(data.VertexSpacing)).Append(" m</span></div>");

        switch (Mode)
        {
            case TerrainDockMode.Sculpt:
                AppendSculpt(rml);
                break;
            case TerrainDockMode.Paint:
                AppendPaint(rml, terrain, data);
                break;
            case TerrainDockMode.Water:
                rml.Append("<div class=\"td-head\">Water</div>");
                Field(rml, "Level", "level", Num(WaterLevel), "Depth above the bed the brush lays, metres (0 to " + Num(data.MaxWaterDepth) +
                    "; Shift dries, Cmd/Ctrl+click picks)");
                RectangleToggle(rml);
                break;
            case TerrainDockMode.Foliage:
                AppendFoliage(rml, data);
                break;
        }

        AppendBrush(rml);
        rml.Append("<div class=\"td-hint\">Left drag paints · Shift inverts · Cmd/Ctrl+click picks · [ ] size · - = strength · Esc steps out</div>");
        return rml.ToString();
    }

    private void AppendCreate(StringBuilder rml)
    {
        rml.Append("<div class=\"td-head\">Create terrain data</div>")
            .Append("<div class=\"td-row\"><span class=\"td-label\">Profile</span><select class=\"td-select\" data-tool-field=\"create-profile\" ")
            .Append("data-tooltip=\"Faceted: flat palette colours on 2 m facets (low-poly). Realistic: smooth splat-blended layers with chunk LOD\">")
            .Append("<option value=\"Faceted\"").Append(_createProfile == TerrainProfile.Faceted ? " selected" : "").Append(">Faceted</option>")
            .Append("<option value=\"Realistic\"").Append(_createProfile == TerrainProfile.Realistic ? " selected" : "").Append(">Realistic</option>")
            .Append("</select></div>");
        Field(rml, "Size", "create-size", _createSize.ToString(CultureInfo.InvariantCulture), "Side length in metres (16 to 2048)");
        Field(rml, "Spacing", "create-spacing", Num(_createSpacing), "Metres between height vertices (0.25 to 4)");
        rml.Append("<div class=\"td-row\"><button class=\"tool-button td-create\" data-tool-action=\"create\" ")
            .Append("data-tooltip=\"Create terrain data — a new heightmap and layers for this terrain, saved beside the scene\">")
            .Append("<span class=\"icon icon-plus icon-sm\"></span><span class=\"label\">Create</span></button></div>");
    }

    private void ModeButton(StringBuilder rml, TerrainDockMode mode, string icon, string tooltip) =>
        IconButton(rml, "mode", mode.ToString(), icon, tooltip, Mode == mode);

    private void AppendSculpt(StringBuilder rml)
    {
        rml.Append("<div class=\"td-tools\">");
        SculptButton(rml, TerrainSculptTool.Raise, "arrow-up", "Raise — lift the ground (Shift: smooth)");
        SculptButton(rml, TerrainSculptTool.Lower, "arrow-down", "Lower — dig the ground (Shift: smooth)");
        SculptButton(rml, TerrainSculptTool.Smooth, "wave-sine", "Smooth — even out bumps");
        SculptButton(rml, TerrainSculptTool.Flatten, "minus", "Flatten — level to the height under the first stamp (Shift: smooth)");
        SculptButton(rml, TerrainSculptTool.SetHeight, "ruler-2", "Set height — move toward the target height (Cmd/Ctrl+click picks it)");
        SculptButton(rml, TerrainSculptTool.Ramp, "trending-up", "Ramp — click the start, then the end: a slope one brush wide");
        rml.Append("</div>");
        if (SculptTool == TerrainSculptTool.SetHeight)
            Field(rml, "Height", "target", Num(TargetHeight), "Target height in metres (terrain-local; Cmd/Ctrl+click picks)");
    }

    private void SculptButton(StringBuilder rml, TerrainSculptTool tool, string icon, string tooltip) =>
        IconButton(rml, "sculpt", tool.ToString(), icon, tooltip, SculptTool == tool);

    private void AppendPaint(StringBuilder rml, Terrain3D terrain, TerrainData data)
    {
        rml.Append("<div class=\"td-tools\">");
        IconButton(rml, "target", "Surface", "palette", data.Profile == TerrainProfile.Faceted ? "Surfaces — palette ids" : "Layers — splat layers",
            PaintTarget == TerrainPaintTarget.Surface);
        IconButton(rml, "target", "Channel", "hash", "User channels — four game-defined values per cell (masks, ids)",
            PaintTarget == TerrainPaintTarget.Channel);
        rml.Append("</div>");
        if (PaintTarget == TerrainPaintTarget.Channel)
        {
            rml.Append("<div class=\"td-tools\">");
            for (var c = 0; c < 4; c++)
                TextButton(rml, "channel", c, "RGBA"[c].ToString(), "User channel " + c + " (" + "RGBA"[c] + " of user.png)", Channel == c);
            rml.Append("<span class=\"td-sep\"></span>");
            IconButton(rml, "channel-mode", "Value", "wave-square", "Value — blend toward the value by the brush weight", ChannelMode == TerrainChannelMode.Value);
            IconButton(rml, "channel-mode", "Id", "tag", "Id — write the value exactly under the brush (Shift writes 0)", ChannelMode == TerrainChannelMode.Id);
            rml.Append("</div>");
            Field(rml, "Value", "channel-value", ChannelValue.ToString(CultureInfo.InvariantCulture), "0 to 255 (Cmd/Ctrl+click picks)");
        }
        else if (data.Profile == TerrainProfile.Faceted)
        {
            rml.Append("<div class=\"td-head\">Surfaces</div><div class=\"td-layers\">");
            var palette = terrain.RenderMaterial as TerrainMaterial3D;
            var count = palette is null ? 8 : Math.Max(1, palette.Surfaces.Length);
            for (var i = 0; i < count; i++)
            {
                var name = palette?.NameOf(i) ?? "Surface " + i;
                Swatch(rml, i, name, palette?.ColorOf(i) ?? System.Drawing.Color.Gray, name + " (id " + i + ")" + (i == EraserSurface ? " — the eraser (Shift)" : ""));
            }

            rml.Append("</div>");
            if (palette is null)
                rml.Append("<div class=\"td-hint\">The terrain's material is not a TerrainMaterial3D: surfaces show as ids.</div>");
            Field(rml, "Eraser", "eraser", EraserSurface.ToString(CultureInfo.InvariantCulture), "The surface Shift paints");
        }
        else
        {
            rml.Append("<div class=\"td-head\">Layers</div><div class=\"td-layers\">");
            var splat = terrain.Material as TerrainSplatMaterial3D;
            var count = splat?.LayerCount ?? 0;
            for (var i = 0; i < Math.Max(count, 1); i++)
            {
                var layer = i < count ? splat!.Layers[i] : null;
                var name = layer is null ? "Layer " + i : !string.IsNullOrEmpty(layer.Name) ? layer.Name : layer.Albedo?.ResourcePath is { } path ? Path.GetFileNameWithoutExtension(path) : "Layer " + i;
                Swatch(rml, i, name, layer?.Tint ?? System.Drawing.Color.Gray, name + (i == EraserSurface ? " (eraser: Shift)" : ""));
            }

            rml.Append("</div>");
            if (count == 0)
                rml.Append("<div class=\"td-hint\">Give the terrain a TerrainSplatMaterial3D to name its layers.</div>");
            Field(rml, "Eraser", "eraser", EraserSurface.ToString(CultureInfo.InvariantCulture), "The layer Shift paints");
        }

        RectangleToggle(rml);
    }

    private void AppendFoliage(StringBuilder rml, TerrainData data)
    {
        rml.Append("<div class=\"td-head\">Foliage types</div><div class=\"td-layers\">");
        var types = data.FoliageTypes;
        for (var i = 0; i < Math.Min(types.Length, TerrainData.PaintedFoliageTypes); i++)
        {
            var type = types[i];
            var name = type is null ? "Type " + i : !string.IsNullOrEmpty(type.ResourceName) ? type.ResourceName
                : type.Mesh is { ResourceName.Length: > 0 } mesh ? mesh.ResourceName : "Type " + i;
            var tooltip = name + (type is { AutoGrowth: false } ? " — grows only where painted" : " — grows by its own rule too (Auto Growth)");
            rml.Append("<div class=\"td-swatch").Append(i == FoliageType ? " active" : "").Append("\" data-tool-action=\"foliage\" data-tool-value=\"")
                .Append(i).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-plant-2 icon-sm\"></span>")
                .Append("<span class=\"td-name\">").Append(RmlText.Escape(name)).Append("</span></div>");
        }

        rml.Append("</div>");
        if (types.Length == 0)
            rml.Append("<div class=\"td-hint\">Add Foliage Types to the terrain data in the Inspector, then paint where they grow.</div>");
        else if (types.Length > TerrainData.PaintedFoliageTypes)
            rml.Append("<div class=\"td-hint\">Only the first eight types can be painted.</div>");
        Field(rml, "Density", "foliage-density", Num(FoliageDensity), "The density the brush paints toward, 0 to 1 (Shift clears, Cmd/Ctrl+click picks)");
        RectangleToggle(rml);
    }

    private void RectangleToggle(StringBuilder rml)
    {
        rml.Append("<div class=\"td-tools\">");
        IconButton(rml, "shape", "Circle", "circle", "Circle brush", !Rectangle && Brush.Shape == TerrainBrushShape.Circle);
        IconButton(rml, "shape", "Square", "square", "Square brush", !Rectangle && Brush.Shape == TerrainBrushShape.Square);
        IconButton(rml, "shape", "Rectangle", "square-dashed", "Rectangle — drag a rectangle to fill (snapped to cells)", Rectangle);
        rml.Append("</div>");
    }

    private void AppendBrush(StringBuilder rml)
    {
        rml.Append("<div class=\"td-head\">Brush</div>");
        if (Mode == TerrainDockMode.Sculpt)
        {
            rml.Append("<div class=\"td-tools\">");
            IconButton(rml, "shape", "Circle", "circle", "Circle brush", Brush.Shape == TerrainBrushShape.Circle);
            IconButton(rml, "shape", "Square", "square", "Square brush", Brush.Shape == TerrainBrushShape.Square);
            rml.Append("</div>");
        }

        Slider(rml, "Size", "size", Brush.Size, TerrainBrush.MinSize, TerrainBrush.MaxSize, 1f, "Diameter in metres ([ and ], Alt+wheel)");
        Slider(rml, "Strength", "strength", Brush.Strength * 100f, 1f, 100f, 1f, "Percent per stamp (- and =, Shift+wheel)");
        Slider(rml, "Falloff", "falloff", Brush.Falloff * 100f, 0f, 100f, 1f, "Percent of the radius that fades to the edge");
        Slider(rml, "Spacing", "spacing", Brush.Spacing * 100f, 5f, 100f, 1f, "Stamp distance in percent of the size");
        rml.Append("<div class=\"td-row\"><span class=\"td-label\">Slope</span><input type=\"checkbox\" data-tool-field=\"slope\"")
            .Append(Brush.SlopeLimit ? " checked" : "")
            .Append(" data-tooltip=\"Limit to slopes between the two angles (degrees)\"/>")
            .Append("<input type=\"text\" class=\"td-num\" data-tool-field=\"slope-min\" value=\"").Append(Num(Brush.SlopeMin)).Append("\"/>")
            .Append("<input type=\"text\" class=\"td-num\" data-tool-field=\"slope-max\" value=\"").Append(Num(Brush.SlopeMax)).Append("\"/></div>");
    }

    private static void IconButton(StringBuilder rml, string action, string value, string icon, string tooltip, bool active) =>
        rml.Append("<button class=\"tool-button small").Append(active ? " active" : "").Append("\" data-tool-action=\"").Append(action)
            .Append("\" data-tool-value=\"").Append(value).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-")
            .Append(icon).Append(" icon-sm\"></span></button>");

    private static void TextButton(StringBuilder rml, string action, int value, string text, string tooltip, bool active) =>
        rml.Append("<button class=\"tool-button small td-letter").Append(active ? " active" : "").Append("\" data-tool-action=\"").Append(action)
            .Append("\" data-tool-value=\"").Append(value).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\">")
            .Append(text).Append("</button>");

    private void Swatch(StringBuilder rml, int index, string? name, System.Drawing.Color color, string tooltip)
    {
        rml.Append("<div class=\"td-swatch").Append(index == Surface ? " active" : "").Append("\" data-tool-action=\"surface\" data-tool-value=\"")
            .Append(index).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"td-chip\" style=\"background-color: #")
            .Append(color.R.ToString("x2", CultureInfo.InvariantCulture)).Append(color.G.ToString("x2", CultureInfo.InvariantCulture))
            .Append(color.B.ToString("x2", CultureInfo.InvariantCulture)).Append(";\"></span>");
        rml.Append("<span class=\"td-name\">").Append(name is null ? index.ToString(CultureInfo.InvariantCulture) : RmlText.Escape(name))
            .Append("</span></div>");
    }

    private static void Field(StringBuilder rml, string label, string field, string value, string tooltip) =>
        rml.Append("<div class=\"td-row\"><span class=\"td-label\">").Append(label).Append("</span><input type=\"text\" class=\"td-text\" data-tool-field=\"")
            .Append(field).Append("\" value=\"").Append(RmlText.Escape(value)).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"/></div>");

    private static void Slider(StringBuilder rml, string label, string field, float value, float min, float max, float step, string tooltip) =>
        rml.Append("<div class=\"td-row\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"td-label\">").Append(label)
            .Append("</span><input type=\"range\" class=\"range td-range\" data-tool-field=\"").Append(field).Append("\" min=\"").Append(Num(min))
            .Append("\" max=\"").Append(Num(max)).Append("\" step=\"").Append(Num(step)).Append("\" value=\"").Append(Num(value)).Append("\"/>")
            .Append("<span class=\"td-value\">").Append(Num(value)).Append("</span></div>");

    private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public void OnDockAction(string action, string? value)
    {
        switch (action)
        {
            case "create":
                CreateData();
                return;
            case "mode" when Enum.TryParse<TerrainDockMode>(value, out var mode):
                Mode = mode;
                return;
            case "sculpt" when Enum.TryParse<TerrainSculptTool>(value, out var tool):
                SculptTool = tool;
                return;
            case "target" when Enum.TryParse<TerrainPaintTarget>(value, out var target):
                PaintTarget = target;
                break;
            case "channel" when int.TryParse(value, CultureInfo.InvariantCulture, out var channel):
                Channel = channel & 3;
                break;
            case "channel-mode" when Enum.TryParse<TerrainChannelMode>(value, out var channelMode):
                ChannelMode = channelMode;
                break;
            case "surface" when int.TryParse(value, CultureInfo.InvariantCulture, out var surface):
                Surface = surface;
                break;
            case "foliage" when int.TryParse(value, CultureInfo.InvariantCulture, out var foliage):
                FoliageType = Math.Clamp(foliage, 0, TerrainData.PaintedFoliageTypes - 1);
                break;
            case "shape" when value == "Rectangle":
                Rectangle = true;
                break;
            case "shape" when Enum.TryParse<TerrainBrushShape>(value, out var shape):
                Brush.Shape = shape;
                Rectangle = false;
                break;
            default:
                return;
        }

        Touch();
    }

    public void OnDockField(string field, string value)
    {
        var number = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && float.IsFinite(f);
        switch (field)
        {
            case "size" when number:
                Brush.Size = f;
                break;
            case "strength" when number:
                Brush.Strength = f / 100f;
                break;
            case "falloff" when number:
                Brush.Falloff = f / 100f;
                break;
            case "spacing" when number:
                Brush.Spacing = f / 100f;
                break;
            case "slope":
                Brush.SlopeLimit = value == "true";
                break;
            case "slope-min" when number:
                Brush.SlopeMin = Math.Clamp(f, 0f, 90f);
                break;
            case "slope-max" when number:
                Brush.SlopeMax = Math.Clamp(f, 0f, 90f);
                break;
            case "target" when number:
                TargetHeight = f;
                break;
            case "level" when number:
                WaterLevel = Math.Max(0f, f);
                break;
            case "eraser" when number:
                EraserSurface = Math.Clamp((int)f, 0, TerrainData.MaxLayers - 1);
                break;
            case "foliage-density" when number:
                FoliageDensity = Math.Clamp(f, 0f, 1f);
                break;
            case "channel-value" when number:
                ChannelValue = (byte)Math.Clamp((int)MathF.Round(f), 0, 255);
                break;
            case "create-profile" when Enum.TryParse<TerrainProfile>(value, out var profile):
                _createProfile = profile;
                if (profile == TerrainProfile.Realistic && _createSpacing > 1f)
                    _createSpacing = 1f;
                break;
            case "create-size" when number:
                _createSize = Math.Clamp((int)MathF.Round(f), 16, 2048);
                break;
            case "create-spacing" when number:
                _createSpacing = Math.Clamp(MathF.Round(f * 4f) / 4f, 0.25f, 4f);
                break;
            default:
                return;
        }

        Touch();
    }

    /// <summary>
    /// A chunk size for a new terrain: the profile's default (16 quads Faceted, 64 Realistic) or the largest even quad
    /// count below it that divides the map and is a whole number of metres; 0 (the default, which validation then
    /// reports) when none does.
    /// </summary>
    public static int ChunkMetersFor(TerrainProfile profile, int sizeMeters, float spacing)
    {
        var quads = (int)MathF.Round(sizeMeters / spacing);
        for (var chunk = profile == TerrainProfile.Realistic ? 64 : 16; chunk >= 2; chunk -= 2)
        {
            var meters = chunk * spacing;
            if (quads % chunk == 0 && MathF.Abs(meters - MathF.Round(meters)) < 1e-4f)
                return (int)MathF.Round(meters);
        }

        return 0;
    }

    /// <summary>The create form's values, for tests and QA.</summary>
    public void SetCreateOptions(TerrainProfile profile, int sizeMeters, float vertexSpacing)
    {
        _createProfile = profile;
        _createSize = sizeMeters;
        _createSpacing = vertexSpacing;
        Touch();
    }

    /// <summary>Gives the terrain new data from the create form (one undo entry).</summary>
    public void CreateData()
    {
        if (_terrain is not { } terrain || _context is null || terrain.Data is not null)
            return;
        TerrainData data;
        try
        {
            data = TerrainData.Create(_createProfile, _createSize, _createSpacing, ChunkMetersFor(_createProfile, _createSize, _createSpacing));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            Log.Warning($"[Editor] Terrain data not created: {e.Message}");
            return;
        }

        var property = TypeRegistry.GetNearest(typeof(Terrain3D))!.FindProperty(nameof(Terrain3D.Data))!;
        _context.History.Commit(SetPropertyAction.Create(terrain, property, data));
        Touch();
    }
}
