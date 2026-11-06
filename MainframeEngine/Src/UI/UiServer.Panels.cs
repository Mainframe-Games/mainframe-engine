using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>World-space panels (G11.5/G11.6, ADRs 0252/0253): their contexts, frame gating and input routing.</summary>
public sealed partial class UiServer
{
    /// <summary>The most panels rendered in one frame (nearest the camera first); the others keep their last image.</summary>
    public const int MaxPanelRendersPerFrame = 8;

    private readonly List<UiPanel3D> _panels = [];
    private UiPanel3D[] _candidates = new UiPanel3D[16];
    private float[] _candidateDistances = new float[16];
    private readonly UiPanel3D?[] _due = new UiPanel3D?[MaxPanelRendersPerFrame];
    private int _dueCount;

    // Pointer state of the mouse and crosshair: the panel under it, the buttons pressed on it (captured until released).
    private UiPanel3D? _hoverPanel;
    private int _panelMouseButtons;
    private Vector2 _mousePixel;
    private bool _mouseKnown;
    private bool _mouseOnLayers; // the cursor is over (or dragging in) a screen layer: Mouse-mode panels do not get it

    /// <summary>Every panel in the tree (render order is chosen per frame).</summary>
    public IReadOnlyList<UiPanel3D> Panels => _panels;

    /// <summary>Panels whose context was rendered last frame (≤ <see cref="MaxPanelRendersPerFrame"/>).</summary>
    public int PanelsRenderedLastFrame { get; private set; }

    /// <summary>The panel keys, text and gamepad navigation go to after the screen layers, or null.</summary>
    public UiPanel3D? FocusedPanel { get; private set; }

    /// <summary>The panel under the mouse or crosshair, or null.</summary>
    public UiPanel3D? HoveredPanel => _hoverPanel;

    internal RmlContext AddPanel(UiPanel3D panel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = new RmlContext($"panel{++_contextCounter}:{panel.Name}", panel.Resolution.X, panel.Resolution.Y, RenderInterface);
        context.SetDensityIndependentPixelRatio(panel.DpScale);
        _panels.Add(panel);
        Renderer?.AddPanelTarget(panel.Target);
        panel.ContentChanged = true;
        return context;
    }

    internal void RemovePanel(UiPanel3D panel)
    {
        _panels.Remove(panel);
        if (ReferenceEquals(_hoverPanel, panel))
        {
            _hoverPanel = null;
            _panelMouseButtons = 0;
        }

        if (ReferenceEquals(FocusedPanel, panel))
            FocusedPanel = null;
        for (var i = 0; i < _due.Length; i++)
            if (ReferenceEquals(_due[i], panel))
                _due[i] = null;
        Renderer?.RemovePanelTarget(panel.Target);
        panel.Context?.Dispose();
    }

    internal void SetPanelFocus(UiPanel3D? panel)
    {
        if (ReferenceEquals(FocusedPanel, panel))
            return;
        if (panel is { IsEditorPanel: true })
            return;
        var previous = FocusedPanel;
        FocusedPanel = panel;
        previous?.RaiseFocusChanged(false);
        panel?.RaiseFocusChanged(true);
    }

    // ── Frame ────────────────────────────────────────────────────────────────────────────────────────────────

    // Process, before the layers: the crosshair and mouse pointers follow the camera, then the due panels (nearest first,
    // at most MaxPanelRendersPerFrame) are laid out and updated.
    private void UpdatePanels()
    {
        _dueCount = 0;
        if (_panels.Count == 0)
            return;
        UpdatePanelPointer(sendMoves: true);

        var count = 0;
        for (var i = 0; i < _panels.Count; i++)
        {
            var panel = _panels[i];
            if (panel.Context is not { IsDisposed: false } || !panel.IsVisibleInTree())
                continue;
            if (!IsDue(panel, out var distance))
                continue;
            if (count == _candidates.Length)
            {
                Array.Resize(ref _candidates, count * 2);
                Array.Resize(ref _candidateDistances, count * 2);
            }

            _candidates[count] = panel;
            // Renders owed (a first render after loading, a skipped frame) go first, then the nearest.
            _candidateDistances[count] = panel.Target.NeedsRender || panel.ContentChanged ? distance - 1e9f : distance;
            count++;
        }

        if (count > MaxPanelRendersPerFrame)
            Array.Sort(_candidateDistances, _candidates, 0, count);
        _dueCount = Math.Min(count, MaxPanelRendersPerFrame);
        for (var i = 0; i < count; i++)
        {
            if (i < _dueCount)
                _due[i] = _candidates[i];
            _candidates[i] = null!;
        }

        for (var i = 0; i < _dueCount; i++)
        {
            var panel = _due[i]!;
            var context = panel.Context!;
            context.SetDimensions(panel.Resolution.X, panel.Resolution.Y);
            context.SetDensityIndependentPixelRatio(Math.Max(0.01f, panel.DpScale));
            var documents = panel.DocumentList;
            for (var d = 0; d < documents.Count; d++)
                documents[d].PrepareFrame();
            context.Update();
        }
    }

    private bool IsDue(UiPanel3D panel, out float distance)
    {
        distance = 0f;
        var camera = panel.GetViewport()?.ActiveCamera3D;
        if (camera is not null)
            distance = Vector3.Distance(camera.GlobalPosition, panel.GlobalPosition);
        if (panel.Target.NeedsRender || panel.ContentChanged)
            return true;
        if (panel.IsEditorPanel)
            return false;
        if (ReferenceEquals(panel, _hoverPanel) || ReferenceEquals(panel, FocusedPanel) || panel.HasPointer)
            return true;
        switch (panel.UpdateMode)
        {
            case SubViewportUpdateMode.Once:
                return true;
            case SubViewportUpdateMode.Always:
                if (camera is null)
                    return true;
                if (panel.MaxUpdateDistance > 0f && distance > panel.MaxUpdateDistance)
                    return false;
                return camera.GetFrustum().Intersects(panel.WorldBounds());
            default:
                return false;
        }
    }

    // After BeginFrame: the due panels' segments, then the screen layers (the last segment).
    private void RenderPanels(VulkanUiRenderer? vulkan)
    {
        PanelsRenderedLastFrame = _dueCount;
        for (var i = 0; i < _dueCount; i++)
        {
            var panel = _due[i]!;
            _due[i] = null;
            if (panel.Context is not { IsDisposed: false } context)
                continue;
            if (vulkan is not null)
            {
                vulkan.BeginTarget(panel.Target);
            }
            else
            {
                panel.Target.Renders++;
                panel.Target.NeedsRender = false;
            }

            context.Render();
            panel.ContentChanged = false;
            if (panel.UpdateMode == SubViewportUpdateMode.Once && !panel.IsEditorPanel)
                panel.UpdateMode = SubViewportUpdateMode.Disabled;
        }

        _dueCount = 0;
        vulkan?.BeginScreen();
    }

    // A frame that records nothing (minimised): the due panels render on a later frame.
    private void SkipPanelRenders()
    {
        for (var i = 0; i < _dueCount; i++)
        {
            if (_due[i] is { } panel)
                panel.Target.NeedsRender = true;
            _due[i] = null;
        }

        _dueCount = 0;
    }

    // ── Pointer ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the panel under the mouse cursor (<see cref="PanelInputMode.Mouse"/> panels, not while the mouse is raw)
    /// or the screen centre (<see cref="PanelInputMode.Crosshair"/> panels): the nearest hit within its
    /// <see cref="UiPanel3D.MaxInteractDistance"/> that a physics ray reaches first (<see cref="UiPanel3D.RequireLineOfSight"/>).
    /// A panel holding a pressed button keeps the pointer, which follows its plane past the edge. Returns whether the
    /// hovered panel's context reported the pointer over an element.
    /// </summary>
    private bool UpdatePanelPointer(bool sendMoves)
    {
        if (_panels.Count == 0)
            return false;
        if (_panelMouseButtons != 0 && _hoverPanel is { } captured)
            return MovePanelPointer(captured, sendMoves, bounded: false);

        // Only asked when a Mouse panel could use the cursor (Silk's mouse list is not free to read every frame).
        var raw = _mouseKnown && !_mouseOnLayers && HasMousePanel() && MouseIsRaw();
        UiPanel3D? best = null;
        var bestDistance = float.MaxValue;
        var bestPixel = Vector2.Zero;
        for (var i = 0; i < _panels.Count; i++)
        {
            var panel = _panels[i];
            if (panel.IsEditorPanel || panel.Context is null || !panel.IsVisibleInTree())
                continue;
            if (!TryPointerRay(panel, raw, out var origin, out var direction))
                continue;
            if (!panel.TryRayToPixel(origin, direction, out var pixel, out var distance) || distance > panel.MaxInteractDistance ||
                distance >= bestDistance)
                continue;
            if (panel.RequireLineOfSight && IsBlocked(panel, origin, direction, distance))
                continue;
            (best, bestDistance, bestPixel) = (panel, distance, pixel);
        }

        if (!ReferenceEquals(best, _hoverPanel))
        {
            if (_hoverPanel is { } previous)
                LeavePanel(previous);
            _hoverPanel = best;
            if (best is not null)
                EnterPanel(best);
        }

        if (best is null)
            return false;
        best.PointerPosition = bestPixel;
        return sendMoves && best.Context!.ProcessMouseMove((int)bestPixel.X, (int)bestPixel.Y, _modifiers);
    }

    private bool HasMousePanel()
    {
        for (var i = 0; i < _panels.Count; i++)
            if (_panels[i].InputMode == PanelInputMode.Mouse)
                return true;
        return false;
    }

    private bool MovePanelPointer(UiPanel3D panel, bool sendMoves, bool bounded)
    {
        if (!TryPointerRay(panel, panel.InputMode == PanelInputMode.Mouse && MouseIsRaw(), out var origin, out var direction) ||
            !panel.TryRayToPlane(origin, direction, bounded, out var pixel, out _))
            return true;
        panel.PointerPosition = pixel;
        if (sendMoves)
            panel.Context?.ProcessMouseMove((int)pixel.X, (int)pixel.Y, _modifiers);
        return true;
    }

    // The ray a panel's pointer follows this frame: the cursor's or the screen centre's, from its viewport's camera.
    private bool TryPointerRay(UiPanel3D panel, bool mouseRaw, out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        if (panel.GetViewport() is not { } viewport || viewport.ActiveCamera3D is not { } camera)
            return false;
        Vector2 pixel;
        switch (panel.InputMode)
        {
            case PanelInputMode.Mouse when !mouseRaw && _mouseKnown && !_mouseOnLayers:
                pixel = _mousePixel;
                break;
            case PanelInputMode.Crosshair:
                pixel = viewport.Size * 0.5f;
                break;
            default:
                return false;
        }

        origin = camera.ProjectRayOrigin(pixel);
        direction = camera.ProjectRayNormal(pixel);
        return true;
    }

    private static bool IsBlocked(UiPanel3D panel, Vector3 origin, Vector3 direction, float distance)
    {
        if (panel.GetWorld3D()?.DirectSpaceState is not { } space)
            return false;
        var length = direction.Length();
        if (!(length > 0f))
            return false;
        var end = origin + direction / length * MathF.Max(0f, distance - 0.01f);
        return space.RayCast(origin, end, out _, CollisionLayers.All, NearestBody(panel), hitFromInside: false);
    }

    private static CollisionObject3D? NearestBody(Node node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n is CollisionObject3D body)
                return body;
        return null;
    }

    private static void EnterPanel(UiPanel3D panel)
    {
        panel.HasPointer = true;
        panel.RaisePointerEntered();
    }

    private static void LeavePanel(UiPanel3D panel)
    {
        if (!panel.HasPointer)
            return;
        panel.HasPointer = false;
        if (panel.Context is { IsDisposed: false } context)
            context.ProcessMouseLeave();
        panel.RaisePointerExited();
    }

    // Mouse motion the screen layers did not take: the cursor's panel (Mouse mode).
    private bool HandlePanelMouseMove(int x, int y, bool layersConsumed)
    {
        _mousePixel = new Vector2(x, y);
        _mouseKnown = true;
        if (layersConsumed && _panelMouseButtons == 0)
        {
            if (_hoverPanel is { InputMode: PanelInputMode.Mouse } hovered)
            {
                LeavePanel(hovered);
                _hoverPanel = null;
            }

            return false;
        }

        return UpdatePanelPointer(sendMoves: true) || _panelMouseButtons != 0 || _hoverPanel is not null;
    }

    // A press or release the screen layers did not take: the hovered panel (a press focuses it); a press elsewhere
    // releases the focused panel.
    private bool HandlePanelButton(int button, bool pressed)
    {
        var bit = 1 << button;
        if (!pressed)
        {
            if ((_panelMouseButtons & bit) == 0)
                return false;
            _panelMouseButtons &= ~bit;
            _hoverPanel?.Context?.ProcessMouseButtonUp(button, _modifiers);
            if (_panelMouseButtons == 0)
                UpdatePanelPointer(sendMoves: true); // the capture ended: the pointer may be off the panel now
            return true;
        }

        if (_hoverPanel is not { Context: { IsDisposed: false } context } panel)
        {
            if (FocusedPanel is { InputMode: not PanelInputMode.Manual })
                SetPanelFocus(null);
            return false;
        }

        _panelMouseButtons |= bit;
        SetPanelFocus(panel);
        context.ProcessMouseButtonDown(button, _modifiers);
        return true;
    }

    private bool HandlePanelWheel(float x, float y) =>
        _hoverPanel?.Context is { IsDisposed: false } context && (context.ProcessMouseWheel(x, y, _modifiers) || true);

    // The crosshair's ClickAction (a key or gamepad button the layers did not take) clicks the hovered crosshair panel.
    private bool HandlePanelClickAction(InputEvent e)
    {
        if (_hoverPanel is not { InputMode: PanelInputMode.Crosshair, ClickAction: { Length: > 0 } action } panel)
            return false;
        var map = panel.Tree?.Input.Map; // the tree's map (the running game's, or a test's)
        if (!e.IsAction(action, map))
            return false;
        var left = UiInputMap.ToRmlButton(MouseButton.Left);
        return e.IsActionPressed(action, map) ? HandlePanelButton(left, pressed: true) : HandlePanelButton(left, pressed: false) || panel.HasPointer;
    }

    // Keys the layers did not take: Escape releases the focused panel, the rest go to its context.
    private bool HandlePanelKey(RmlKey key, bool pressed, Key engineKey)
    {
        if (FocusedPanel is not { Context: { IsDisposed: false } context })
            return false;
        if (pressed && engineKey == Key.Escape && !_system.TextInputActive)
        {
            SetPanelFocus(null);
            return true;
        }

        var consumed = pressed ? context.ProcessKeyDown(key, _modifiers) : context.ProcessKeyUp(key, _modifiers);
        return consumed || _system.TextInputActive;
    }

    private bool HandlePanelText(ReadOnlySpan<char> text) =>
        FocusedPanel?.Context is { IsDisposed: false } context && context.ProcessTextInput(text);

    // Gamepad navigation the layers did not take: within the focused panel.
    private bool NavigatePanel(RmlKey key)
    {
        if (FocusedPanel?.Context is not { IsDisposed: false } context)
            return false;
        if (IsDirection(key) && !HasElementFocus(context.FocusElement))
        {
            context.ProcessKeyDown(RmlKey.Tab, RmlKeyModifiers.None);
            return HasElementFocus(context.FocusElement);
        }

        return context.ProcessKeyDown(key, _modifiers);
    }

    // ── Manual pointer ───────────────────────────────────────────────────────────────────────────────────────

    internal void PushPanelPointer(UiPanel3D panel, Vector3 origin, Vector3 direction)
    {
        if (panel.IsEditorPanel || panel.Context is not { IsDisposed: false } context)
            return;
        var held = ReferenceEquals(_manualCapture, panel);
        if (panel.TryRayToPlane(origin, direction, bounded: !held, out var pixel, out _))
        {
            if (!panel.HasPointer)
                EnterPanel(panel);
            panel.PointerPosition = pixel;
            context.ProcessMouseMove((int)pixel.X, (int)pixel.Y, RmlKeyModifiers.None);
        }
        else
        {
            LeavePanel(panel);
        }
    }

    private UiPanel3D? _manualCapture;
    private int _manualButtons;

    internal void PushPanelButton(UiPanel3D panel, MouseButton button, bool pressed)
    {
        if (panel.IsEditorPanel || panel.Context is not { IsDisposed: false } context)
            return;
        var rml = UiInputMap.ToRmlButton(button);
        if (rml < 0 || rml > 30)
            return;
        if (pressed)
        {
            if (!panel.HasPointer)
                return;
            SetPanelFocus(panel);
            _manualCapture = panel;
            _manualButtons |= 1 << rml;
            context.ProcessMouseButtonDown(rml, RmlKeyModifiers.None);
        }
        else
        {
            context.ProcessMouseButtonUp(rml, RmlKeyModifiers.None);
            _manualButtons &= ~(1 << rml);
            if (_manualButtons == 0)
                _manualCapture = null;
        }
    }

    internal void PushPanelWheel(UiPanel3D panel, Vector2 delta)
    {
        if (!panel.IsEditorPanel && panel.HasPointer && panel.Context is { IsDisposed: false } context)
            context.ProcessMouseWheel(delta.X, delta.Y, _modifiers);
    }
}
