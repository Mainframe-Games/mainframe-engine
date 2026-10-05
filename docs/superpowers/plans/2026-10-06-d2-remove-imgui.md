# D2 — Remove ImGui Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove ImGui from the engine entirely. Its gizmos become a Vulkan screen-space overlay (`ScreenGizmos`) and its
F12 developer windows become an RmlUi `DevOverlay` that every game (GameHost included) gets.

**Architecture:** `ScreenGizmoBatch` (CPU tessellation, pixels, sRGB) is filled during the frame and drawn by
`ScreenGizmosRenderer : IOverlayRenderer` after the tonemap, between the canvas and the UI (explicit overlay order).
`DevOverlay` is an engine server (`IServer, IFrameServer`) that owns a top `UiLayer` with one document composed from
panels; built-in panels read renderer/shadow/GPU/audio/physics/network stats at 4 Hz through static bindings. Shadow-map
images reach RmlUi through a new depth-capable `engine://` texture source. Then ImGui is deleted.

**Tech Stack:** C# / .NET 10, Vulkan (Silk.NET), GLSL → SPIR-V (`glslc`), RmlUi, xUnit v3, render tests.

**Spec:** [`docs/design/future/remove-imgui.md`](../../design/future/remove-imgui.md)

**Plan series:** [D1 Demo](2026-10-06-d1-demo-project.md) → D2 (this) → [D3 Project icons](2026-10-06-d3-project-icons.md)
→ [D4 Demo download](2026-10-06-d4-demo-download.md). Starts on branch `demo-and-polish` after D1 (the Sandbox is gone;
its ImGui code is read from `origin/main` where noted).

## Global Constraints

- Commit locally; do not push. No new NuGet packages (this plan removes one: ImGui.NET).
- Per-frame engine code must not allocate (allocation gate) and must be validation-clean. Per-frame GPU resources are
  keyed by `IVulkanContext.FrameSlot` and sized `IVulkanContext.MaxFramesInFlight`; GPU objects are released through
  `IVulkanContext.Deletions`, never `vkDeviceWaitIdle`.
- Shaders live in `MainframeEngine/Content/Shaders/<Area>/<Name>.vk.{vert,frag}`; after any shader change run
  `just shaders` and commit the `.spv` files and `shaders.lock`; `just shaders-check` must pass.
- Colours authored by people are sRGB; overlay shaders linearise only when `IVulkanContext.OverlayEncodesSrgb`
  (specialization constant 0), exactly like the UI/ImGui overlay pipelines do.
- Rendering changes keep render tests green; intentional output changes: `UPDATE_GOLDENS=1`, inspect, commit both
  `moltenvk` and `lavapipe` goldens (`just render-tests-linux` for lavapipe).
- Gates: `just build`, Release build, `just test`, `just test-render`, `just format-check`, `just shaders-check`.
- Line numbers below are from `origin/main`/the port branch; anchor on the quoted code.

## Review Focus

- **Overlay visible during a window resize / swapchain rebuild:** `ScreenGizmosRenderer` must use the current
  `SwapchainExtent` each frame, and the dev overlay's shadow images must survive shadow maps being re-created. Pinned by
  Task 8's `DevOverlayShadowsPanelIsValidationClean` (it runs `--resize`).
- **Shadow maps released while shown** (unused for 120 frames → views become 0): the UI texture source must return
  "no view" and RmlUi must not sample a destroyed view. Pinned by Task 5's
  `DepthSourceWithoutAViewIsNotDrawn` unit test and Task 8.
- **Toggling F12 many times / overlay hidden:** hidden overlay does no per-frame work and showing it does not allocate.
  Pinned by Task 8's allocation gate (overlay visible) and Task 6's `HiddenOverlayDoesNotRefresh`.
- **Gizmo geometry behind the camera:** lines with an endpoint behind the near plane must be dropped, not drawn across
  the screen. Pinned by Task 4's `PointLightBehindTheCameraDrawsNothing`.
- **Games adding panels after start-up:** `AddPanel` after the overlay document loaded must still bind. Pinned by Task 6's
  `PanelAddedAfterLoadIsBound`.

---

### Task 1: Explicit overlay renderer order

**Files:**
- Create: `MainframeEngine/Src/Rendering/Vulkan/OverlayRendererList.cs`
- Modify: `MainframeEngine/Src/Rendering/Vulkan/IVulkanContext.cs` (`AddOverlayRenderer`, ~line 94),
  `VulkanRenderer.Presentation.cs` (`_overlayRenderers`, `AddOverlayRenderer`, `RemoveOverlayRenderer`, ~lines 384-395),
  `MainframeEngine/Src/Rendering/Canvas/VulkanCanvasRenderer.cs:117`, `MainframeEngine/Src/UI/Rendering/VulkanUiRenderer.cs:60`,
  `IOverlayRenderer.cs` (doc)
- Test: `Tests/MainframeEngine.Tests/Rendering/OverlayRendererListTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class OverlayOrder { public const int Canvas = 0; public const int Gizmos = 100; public const int Ui = 200; }
  internal sealed class OverlayRendererList  // stable: equal orders keep registration order
  {
      public void Add(IOverlayRenderer renderer, int order);   // no-op when already present
      public bool Remove(IOverlayRenderer renderer);
      public int Count { get; }
      public IOverlayRenderer this[int index] { get; }
  }
  // IVulkanContext: void AddOverlayRenderer(IOverlayRenderer renderer, int order = OverlayOrder.Ui);
  ```

- [ ] **Step 1: Failing tests**

```csharp
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

public sealed class OverlayRendererListTests
{
    private sealed class Fake(string name) : IOverlayRenderer
    {
        public void RecordOffscreen(CommandBuffer commandBuffer) { }
        public void RecordOverlay(CommandBuffer commandBuffer) { }
        public override string ToString() => name;
    }

    [Fact]
    public void RenderersAreOrderedByOrderThenRegistration()
    {
        var list = new OverlayRendererList();
        Fake ui = new("ui"), canvas = new("canvas"), gizmos = new("gizmos"), ui2 = new("ui2");
        list.Add(ui, OverlayOrder.Ui);
        list.Add(canvas, OverlayOrder.Canvas);
        list.Add(ui2, OverlayOrder.Ui);
        list.Add(gizmos, OverlayOrder.Gizmos);
        Assert.Equal(["canvas", "gizmos", "ui", "ui2"], Enumerable.Range(0, list.Count).Select(i => list[i].ToString()));
    }

    [Fact]
    public void AddingTwiceKeepsOneAndRemoveWorks()
    {
        var list = new OverlayRendererList();
        var a = new Fake("a");
        list.Add(a, 5);
        list.Add(a, 7);
        Assert.Equal(1, list.Count);
        Assert.True(list.Remove(a));
        Assert.Equal(0, list.Count);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter OverlayRendererListTests` — Expected: FAIL (type missing).
(The test project already sees engine internals if `InternalsVisibleTo("MainframeEngine.Tests")` is set in
`MainframeEngine.csproj`; it is — other tests use internal types. If not, add it.)

- [ ] **Step 2: Implement**

```csharp
namespace MainframeEngine;

/// <summary>Draw order of overlay renderers (after the tonemap): 2D canvas, then screen gizmos, then the UI.</summary>
public static class OverlayOrder
{
    public const int Canvas = 0;
    public const int Gizmos = 100;
    public const int Ui = 200;
}

/// <summary>Overlay renderers sorted by order; equal orders keep their registration order. Iterated without allocation.</summary>
internal sealed class OverlayRendererList
{
    private readonly List<(IOverlayRenderer Renderer, int Order)> _items = [];

    public int Count => _items.Count;

    public IOverlayRenderer this[int index] => _items[index].Renderer;

    public void Add(IOverlayRenderer renderer, int order)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (_items.Exists(i => ReferenceEquals(i.Renderer, renderer)))
            return;
        var at = _items.FindIndex(i => i.Order > order);
        _items.Insert(at < 0 ? _items.Count : at, (renderer, order));
    }

    public bool Remove(IOverlayRenderer renderer) =>
        _items.RemoveAll(i => ReferenceEquals(i.Renderer, renderer)) > 0;
}
```

In `VulkanRenderer.Presentation.cs` replace the `List<IOverlayRenderer> _overlayRenderers` with
`private readonly OverlayRendererList _overlayRenderers = new();`, make `AddOverlayRenderer(IOverlayRenderer renderer,
int order = OverlayOrder.Ui)` call `_overlayRenderers.Add(renderer, order)`, and change the two `foreach` loops in
`BeginOverlayPass` to `for (var i = 0; i < _overlayRenderers.Count; i++) _overlayRenderers[i].RecordOffscreen(cb);`
(and `RecordOverlay`). Update the `IVulkanContext` signature and doc. `VulkanCanvasRenderer` passes
`OverlayOrder.Canvas`; `VulkanUiRenderer` passes `OverlayOrder.Ui`.

- [ ] **Step 3: Tests + render tests, commit**

Run: `dotnet test Tests/MainframeEngine.Tests --filter OverlayRendererListTests && just test-render`
Expected: PASS; goldens unchanged (canvas already registered before UI).

```bash
git add MainframeEngine/Src/Rendering Tests/MainframeEngine.Tests/Rendering/OverlayRendererListTests.cs MainframeEngine/Src/UI/Rendering/VulkanUiRenderer.cs
git commit -m "Rendering: explicit overlay renderer order (canvas, gizmos, UI)"
```

---

### Task 2: `ScreenGizmoBatch` — CPU tessellation

**Files:**
- Create: `MainframeEngine/Src/Rendering/Gizmos/ScreenGizmoBatch.cs`
- Test: `Tests/MainframeEngine.Tests/Rendering/ScreenGizmoBatchTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  [StructLayout(LayoutKind.Sequential)] public readonly struct ScreenGizmoVertex(Vector2 position, Vector4 color) { Position; Color; } // 24 bytes
  public sealed class ScreenGizmoBatch
  {
      public const float Feather = 1f;                          // anti-alias edge, pixels
      public int VertexCount { get; }
      internal ReadOnlySpan<ScreenGizmoVertex> Vertices { get; }
      public void Clear();
      public void Line(Vector2 a, Vector2 b, Vector4 color, float thickness = 1.5f);
      public void Polyline(ReadOnlySpan<Vector2> points, Vector4 color, float thickness = 1.5f, bool closed = false);
      public void Circle(Vector2 center, float radius, Vector4 color, float thickness = 1.5f, int segments = 32);
      public void FilledCircle(Vector2 center, float radius, Vector4 color, int segments = 24);
      public void FilledRect(Vector2 min, Vector2 max, Vector4 color);
      public void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color);
      public void Arrow(Vector2 from, Vector2 to, Vector4 color, float thickness = 2f, float head = 10f);
      public void Glyph(char c, Vector2 topLeft, float size, Vector4 color, float thickness = 1.5f); // 'X','Y','Z'; others ignored
  }
  ```
  Coordinates: framebuffer pixels, top-left origin, +Y down. Colours: sRGB, straight alpha. Triangle list.

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class ScreenGizmoBatchTests
{
    private static readonly Vector4 Red = new(1, 0, 0, 1);

    [Fact]
    public void FilledRectIsTwoTriangles()
    {
        var batch = new ScreenGizmoBatch();
        batch.FilledRect(new Vector2(10, 10), new Vector2(20, 30), Red);
        Assert.Equal(6, batch.VertexCount);
        Assert.All(batch.Vertices.ToArray(), v => Assert.Equal(Red, v.Color));
        Assert.Equal(new Vector2(10, 10), batch.Vertices.ToArray().Aggregate(new Vector2(float.MaxValue), (m, v) => Vector2.Min(m, v.Position)));
    }

    [Fact]
    public void ThickLineIsACoreQuadPlusTwoFeatherQuadsFadingToTransparent()
    {
        var batch = new ScreenGizmoBatch();
        batch.Line(new Vector2(0, 0), new Vector2(100, 0), Red, thickness: 4f);
        var vertices = batch.Vertices.ToArray();
        Assert.Equal(18, vertices.Length);
        var maxY = vertices.Max(v => MathF.Abs(v.Position.Y));
        Assert.Equal(2f + ScreenGizmoBatch.Feather, maxY, 3);                          // half thickness + feather
        Assert.All(vertices.Where(v => MathF.Abs(v.Position.Y) > 2.01f), v => Assert.Equal(0f, v.Color.W));
    }

    [Fact]
    public void ZeroLengthLineEmitsNothing()
    {
        var batch = new ScreenGizmoBatch();
        batch.Line(new Vector2(5, 5), new Vector2(5, 5), Red);
        Assert.Equal(0, batch.VertexCount);
    }

    [Fact]
    public void SteadyStateDoesNotAllocate()
    {
        var batch = new ScreenGizmoBatch();
        Fill(batch);
        batch.Clear();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Fill(batch);
            batch.Clear();
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());

        static void Fill(ScreenGizmoBatch b)
        {
            b.Circle(new Vector2(50), 20, Red);
            b.Arrow(Vector2.Zero, new Vector2(40, 0), Red);
            b.Glyph('X', new Vector2(5), 10, Red);
        }
    }

    [Fact]
    public void UnknownGlyphsAreIgnored()
    {
        var batch = new ScreenGizmoBatch();
        batch.Glyph('Q', Vector2.Zero, 10, Red);
        Assert.Equal(0, batch.VertexCount);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter ScreenGizmoBatchTests` — Expected: FAIL (type missing).

- [ ] **Step 2: Implement**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>A screen-gizmo vertex: framebuffer-pixel position (top-left origin) and straight-alpha sRGB colour.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ScreenGizmoVertex(Vector2 position, Vector4 color)
{
    public readonly Vector2 Position = position;
    public readonly Vector4 Color = color;
}

/// <summary>
/// Immediate-mode screen-space shapes (pixels, +Y down) tessellated into triangles on the CPU, drawn after the tonemap
/// by <see cref="ScreenGizmosRenderer"/>. Lines get a 1 px feathered edge for anti-aliasing. Grows on demand, then
/// never allocates.
/// </summary>
public sealed class ScreenGizmoBatch
{
    public const float Feather = 1f;

    private ScreenGizmoVertex[] _vertices = new ScreenGizmoVertex[4096];
    private int _count;

    public int VertexCount => _count;

    internal ReadOnlySpan<ScreenGizmoVertex> Vertices => _vertices.AsSpan(0, _count);

    public void Clear() => _count = 0;

    public void Line(Vector2 a, Vector2 b, Vector4 color, float thickness = 1.5f)
    {
        var delta = b - a;
        var length = delta.Length();
        if (length < 1e-4f)
            return;
        var normal = new Vector2(-delta.Y, delta.X) / length;
        var half = normal * (thickness * 0.5f);
        var outer = normal * (thickness * 0.5f + Feather);
        var clear = color with { W = 0f };
        Quad(a + half, b + half, b - half, a - half, color, color, color, color);
        Quad(a + outer, b + outer, b + half, a + half, clear, clear, color, color);
        Quad(a - half, b - half, b - outer, a - outer, color, color, clear, clear);
    }

    public void Polyline(ReadOnlySpan<Vector2> points, Vector4 color, float thickness = 1.5f, bool closed = false)
    {
        for (var i = 0; i + 1 < points.Length; i++)
            Line(points[i], points[i + 1], color, thickness);
        if (closed && points.Length > 2)
            Line(points[^1], points[0], color, thickness);
    }

    public void Circle(Vector2 center, float radius, Vector4 color, float thickness = 1.5f, int segments = 32)
    {
        var previous = center + new Vector2(radius, 0);
        for (var i = 1; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            Line(previous, next, color, thickness);
            previous = next;
        }
    }

    public void FilledCircle(Vector2 center, float radius, Vector4 color, int segments = 24)
    {
        var previous = center + new Vector2(radius, 0);
        for (var i = 1; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            Triangle(center, previous, next, color);
            previous = next;
        }
    }

    public void FilledRect(Vector2 min, Vector2 max, Vector4 color) =>
        Quad(min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y), color, color, color, color);

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color)
    {
        Ensure(3);
        _vertices[_count++] = new ScreenGizmoVertex(a, color);
        _vertices[_count++] = new ScreenGizmoVertex(b, color);
        _vertices[_count++] = new ScreenGizmoVertex(c, color);
    }

    public void Arrow(Vector2 from, Vector2 to, Vector4 color, float thickness = 2f, float head = 10f)
    {
        Line(from, to, color, thickness);
        var delta = to - from;
        var length = delta.Length();
        if (length < 1e-4f)
            return;
        var dir = delta / length;
        var perp = new Vector2(-dir.Y, dir.X);
        Line(to, to - dir * head + perp * (head * 0.5f), color, thickness);
        Line(to, to - dir * head - perp * (head * 0.5f), color, thickness);
    }

    /// <summary>Stroke glyphs for axis labels (no font): 'X', 'Y', 'Z' in a <paramref name="size"/>-pixel box.</summary>
    public void Glyph(char c, Vector2 topLeft, float size, Vector4 color, float thickness = 1.5f)
    {
        var tl = topLeft;
        var tr = topLeft + new Vector2(size * 0.7f, 0);
        var bl = topLeft + new Vector2(0, size);
        var br = topLeft + new Vector2(size * 0.7f, size);
        var mid = topLeft + new Vector2(size * 0.35f, size * 0.5f);
        switch (char.ToUpperInvariant(c))
        {
            case 'X':
                Line(tl, br, color, thickness);
                Line(tr, bl, color, thickness);
                break;
            case 'Y':
                Line(tl, mid, color, thickness);
                Line(tr, mid, color, thickness);
                Line(mid, topLeft + new Vector2(size * 0.35f, size), color, thickness);
                break;
            case 'Z':
                Line(tl, tr, color, thickness);
                Line(tr, bl, color, thickness);
                Line(bl, br, color, thickness);
                break;
        }
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 ca, Vector4 cb, Vector4 cc, Vector4 cd)
    {
        Ensure(6);
        _vertices[_count++] = new ScreenGizmoVertex(a, ca);
        _vertices[_count++] = new ScreenGizmoVertex(b, cb);
        _vertices[_count++] = new ScreenGizmoVertex(c, cc);
        _vertices[_count++] = new ScreenGizmoVertex(a, ca);
        _vertices[_count++] = new ScreenGizmoVertex(c, cc);
        _vertices[_count++] = new ScreenGizmoVertex(d, cd);
    }

    private void Ensure(int more)
    {
        if (_count + more > _vertices.Length)
            Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, _count + more));
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter ScreenGizmoBatchTests` — Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add MainframeEngine/Src/Rendering/Gizmos/ScreenGizmoBatch.cs Tests/MainframeEngine.Tests/Rendering/ScreenGizmoBatchTests.cs
git commit -m "Gizmos: ScreenGizmoBatch (screen-space, anti-aliased, allocation-free)"
```

---

### Task 3: `ScreenGizmosRenderer` and the colour-pipeline test

**Files:**
- Create: `MainframeEngine/Src/Rendering/Gizmos/ScreenGizmosRenderer.cs`
- Create: `MainframeEngine/Content/Shaders/Gizmos/ScreenGizmo.vk.vert`, `ScreenGizmo.vk.frag` (+ `.spv`, `shaders.lock`)
- Modify: `MainframeEngine/Src/Servers/RenderServer.cs` (property `ScreenGizmos`, renderer creation, dispose)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/ColorPipelineScene.cs`, `Tests/MainframeEngine.RenderTests/SceneTests.cs`
  (`HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath`)

**Interfaces:**
- Consumes: `ScreenGizmoBatch`, `OverlayOrder.Gizmos`.
- Produces: `public ScreenGizmoBatch RenderServer.ScreenGizmos { get; }` (filled any time during the frame; drawn and
  cleared in the overlay pass; cleared without drawing when the frame is skipped).

- [ ] **Step 1: Switch the colour-pipeline scene to ScreenGizmos (failing test first)**

`ColorPipelineScene`: delete the `OnImGui` override and `using ImGuiNET;`; draw the same rectangles in **pixels**
(`Host.Scale` × the old point coordinates) every update:

```csharp
protected override void UpdateScene(in GameTime gameTime)
{
    // (existing exposure switch logic stays)
    var gizmos = Servers.Render!.ScreenGizmos;
    var s = Host.Scale;
    gizmos.FilledRect(new Vector2(10, 10) * s, new Vector2(60, 60) * s, OverlayColor);
    gizmos.FilledRect(new Vector2(70, 10) * s, new Vector2(120, 60) * s, new Vector4(0, 0, 0, 1));
    gizmos.FilledRect(new Vector2(70, 10) * s, new Vector2(120, 60) * s, new Vector4(1, 1, 1, 0.5f));
}
```

In `SceneTests.HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath`, update the comments ("ImGui works in points" →
"the gizmo rects are drawn at host scale") — the asserted pixels and tolerances stay identical (35×scale, 95×scale).

Run: `dotnet build && dotnet test Tests/MainframeEngine.RenderTests --filter HdrTonemapSrgb`
Expected: FAIL to compile — `RenderServer.ScreenGizmos` missing.

- [ ] **Step 2: Shaders**

`MainframeEngine/Content/Shaders/Gizmos/ScreenGizmo.vk.vert`:

```glsl
#version 450
layout(location = 0) in vec2 inPosition; // framebuffer pixels, top-left origin
layout(location = 1) in vec4 inColor;    // sRGB, straight alpha
layout(push_constant) uniform Push { vec2 scale; vec2 translate; } push;
layout(location = 0) out vec4 fragColor;
void main()
{
    fragColor = inColor;
    gl_Position = vec4(inPosition * push.scale + push.translate, 0.0, 1.0);
}
```

`MainframeEngine/Content/Shaders/Gizmos/ScreenGizmo.vk.frag`:

```glsl
#version 450
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
layout(constant_id = 0) const bool kLinearizeColors = false; // swapchain encodes sRGB on store
layout(location = 0) in vec4 fragColor;
layout(location = 0) out vec4 outColor;
void main()
{
    vec4 color = fragColor;
    if (kLinearizeColors)
        color.rgb = srgbToLinear(color.rgb);
    outColor = color;
}
```

Run: `just shaders` — Expected: both compile and pass `spirv-val`; `shaders.lock` gains two rows.

- [ ] **Step 3: Renderer**

```csharp
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Draws <see cref="ScreenGizmoBatch"/> in the overlay pass (after the tonemap, below the UI).</summary>
internal sealed unsafe class ScreenGizmosRenderer : IOverlayRenderer, IDisposable
{
    private const string VertexShader = "Shaders/Gizmos/ScreenGizmo.vk.vert.spv";
    private const string FragmentShader = "Shaders/Gizmos/ScreenGizmo.vk.frag.spv";

    private readonly IVulkanContext _ctx;
    private readonly ScreenGizmoBatch _batch;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private bool _disposed;

    private struct Push
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    public ScreenGizmosRenderer(IVulkanContext ctx, ScreenGizmoBatch batch)
    {
        _ctx = ctx;
        _batch = batch;
        CreatePipeline();
        ctx.AddOverlayRenderer(this, OverlayOrder.Gizmos);
    }

    public void RecordOffscreen(CommandBuffer commandBuffer) { }

    public void RecordOverlay(CommandBuffer commandBuffer)
    {
        try
        {
            var vertices = _batch.Vertices;
            if (vertices.Length == 0)
                return;
            var slot = _ctx.FrameSlot;
            var size = (ulong)(vertices.Length * sizeof(ScreenGizmoVertex));
            var buffer = _buffers[slot];
            if (buffer is null || buffer.Size < size)
            {
                buffer?.Dispose();
                buffer = _buffers[slot] = GpuBuffer.Create(_ctx, Math.Max(size * 2, 64 * 1024), BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
            }

            buffer.Write(vertices);
            var vk = _ctx.Vk;
            var extent = _ctx.SwapchainExtent;
            PipelineBuilder.SetViewport(vk, commandBuffer, extent, flipY: false);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
            var handle = buffer.Handle;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &handle, &offset);
            var push = new Push
            {
                Scale = new Vector2(2f / extent.Width, 2f / extent.Height),
                Translate = new Vector2(-1f, -1f),
            };
            vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Push), &push);
            vk.CmdDraw(commandBuffer, (uint)vertices.Length, 1, 0, 0);
        }
        finally
        {
            _batch.Clear();
        }
    }

    private void CreatePipeline()
    {
        _layout = PipelineBuilder.CreateLayout(_ctx, [], (uint)sizeof(Push), ShaderStageFlags.VertexBit, "screen gizmos");
        ReadOnlySpan<VertexInputBindingDescription> bindings =
            [new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(ScreenGizmoVertex), InputRate = VertexInputRate.Vertex }];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 8 },
        ];
        var linearize = _ctx.OverlayEncodesSrgb ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &linearize };
        _pipeline = PipelineBuilder.Create(_ctx, new PipelineState { Blend = BlendMode.Alpha }, _layout, _ctx.OverlayRenderPass,
            VertexShader, FragmentShader, bindings, attributes, "screen gizmos", &specialization);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ctx.RemoveOverlayRenderer(this);
        foreach (var buffer in _buffers)
            buffer?.Dispose();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
    }
}
```

(The push-constant `Translate` of (−1, −1) with `flipY: false` maps pixel (0,0) to the top-left, as the UI overlay
does. If the rects come out vertically flipped in Step 5, negate `Scale.Y` and use `Translate.Y = 1`. Match
`GpuDeletion.Of` overloads and `PipelineBuilder.CreateLayout`'s parameters to the code in `PipelineBuilder.cs`.)

`RenderServer`:

```csharp
private ScreenGizmosRenderer? _screenGizmosRenderer;

/// <summary>
/// Screen-space gizmos (framebuffer pixels, sRGB), drawn after the tonemap between the 2D canvas and the UI, then
/// cleared. Fill it any time before the frame's overlay pass.
/// </summary>
public ScreenGizmoBatch ScreenGizmos { get; } = new();
```

In `PrepareFrame` (or the first method that runs every frame with a Vulkan context), create the renderer once:
`if (_screenGizmosRenderer is null && Vulkan is { } vk) _screenGizmosRenderer = new ScreenGizmosRenderer(vk, ScreenGizmos);`.
When a frame is skipped (no `FrameStarted`), call `ScreenGizmos.Clear()` so shapes do not pile up. Dispose the
renderer in `RenderServer.Dispose`.

- [ ] **Step 4: Run the test**

Run: `just shaders-check && dotnet test Tests/MainframeEngine.RenderTests --filter "HdrTonemapSrgb|ColorPipeline"`
Expected: PASS with the original pixel expectations (exact opaque colour ±1, 50% blend 127.5 ±2).

- [ ] **Step 5: All render tests, commit**

Run: `caffeinate -u -t 1200 & just test-render` — Expected: PASS (no golden changes: nothing else draws gizmos yet).

```bash
git add MainframeEngine/Content/Shaders/Gizmos MainframeEngine/Content/Shaders/shaders.lock MainframeEngine/Src Tests/MainframeEngine.RenderTests*
git commit -m "Gizmos: ScreenGizmosRenderer in the overlay pass; colour-pipeline test uses it"
```

---

### Task 4: Light and axis gizmos on ScreenGizmos

**Files:**
- Create: `MainframeEngine/Src/Rendering/Gizmos/GizmoProjection.cs`, `LightGizmos.cs`, `AxisGizmo.cs`
- Modify: `MainframeEngine/Src/Servers/RenderServer.cs` (`ShowLightGizmos`, `ShowAxisGizmo`, draw in `RenderMain`)
- Delete: `MainframeEngine/Src/Rendering/Gizmos/ImGuiCoordGizmo.cs`, `MainframeEngine/Src/Utils/ImGuiGizmos.cs`,
  `MainframeEngine/Src/Utils/ColorExtensions.cs`, `LightEnvironment.DrawLightGizmos` + helpers (`LightEnvironment.cs`
  ~lines 156-281, and `using ImGuiNET;`)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/ShowcaseScene.cs` (replace the gizmo calls in `OnImGui` with
  `Servers.Render!.ShowLightGizmos = true; Servers.Render.ShowAxisGizmo = true;` in `LoadScene`)
- Test: `Tests/MainframeEngine.Tests/Rendering/GizmoTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class GizmoProjection
  {
      public static bool TryProject(Vector3 world, in Matrix4x4 viewProjection, Vector2 viewport, out Vector2 pixel); // false when w <= 0
  }
  public static class LightGizmos { public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, LightEnvironment lights, float scale); }
  public static class AxisGizmo { public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, float scale); }
  // RenderServer: public bool ShowLightGizmos { get; set; }  public bool ShowAxisGizmo { get; set; }
  ```

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class GizmoTests
{
    private static PerspectiveCamera Camera() => new()
    {
        Position = new Vector3(0, 0, 10), Forward = -Vector3.UnitZ, Up = Vector3.UnitY, AspectRatio = 2f,
    };

    [Fact]
    public void ProjectionPutsTheTargetAtTheViewportCentre()
    {
        var camera = Camera();
        Assert.True(GizmoProjection.TryProject(Vector3.Zero, camera.ViewMatrix * camera.ProjectionMatrix, new Vector2(400, 200), out var pixel));
        Assert.True(Vector2.Distance(new Vector2(200, 100), pixel) < 0.01f, pixel.ToString());
    }

    [Fact]
    public void PointLightBehindTheCameraDrawsNothing()
    {
        var lights = new LightEnvironment();
        lights.AddLight(new PointLight { Position = new Vector3(0, 0, 20), Range = 1f });
        var batch = new ScreenGizmoBatch();
        LightGizmos.Draw(batch, Camera(), new Vector2(400, 200), lights, scale: 1f);
        Assert.Equal(0, batch.VertexCount);
    }

    [Fact]
    public void PointLightInFrontDrawsAnIconAndRangeCircles()
    {
        var lights = new LightEnvironment();
        lights.AddLight(new PointLight { Position = Vector3.Zero, Range = 2f });
        var batch = new ScreenGizmoBatch();
        LightGizmos.Draw(batch, Camera(), new Vector2(400, 200), lights, scale: 1f);
        Assert.True(batch.VertexCount > 100);
    }

    [Fact]
    public void AxisGizmoXPointsRightAndYPointsUpForAFrontCamera()
    {
        var batch = new ScreenGizmoBatch();
        AxisGizmo.Draw(batch, Camera(), new Vector2(400, 200), scale: 1f);
        var (x, y) = (AxisGizmo.LastTips[0], AxisGizmo.LastTips[1]);
        Assert.True(x.X > AxisGizmo.LastOrigin.X, "X right");
        Assert.True(y.Y < AxisGizmo.LastOrigin.Y, "Y up (screen -Y)");
    }
}
```

(Use the real `PerspectiveCamera` and `PointLight` type/member names from `MainframeEngine/Src/Rendering/Camera` and
`MainframeEngine/Src/Lighting`; `AxisGizmo.LastTips`/`LastOrigin` are `internal static` diagnostics for this test.)

Run: `dotnet test Tests/MainframeEngine.Tests --filter GizmoTests` — Expected: FAIL (types missing).

- [ ] **Step 2: Implement projection and the light gizmos (ported from `LightEnvironment.DrawLightGizmos`)**

```csharp
using System.Numerics;

namespace MainframeEngine;

public static class GizmoProjection
{
    /// <summary>World → framebuffer pixel (top-left origin). False when the point is at or behind the camera plane.</summary>
    public static bool TryProject(Vector3 world, in Matrix4x4 viewProjection, Vector2 viewport, out Vector2 pixel)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W <= 1e-5f)
        {
            pixel = default;
            return false;
        }

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        pixel = new Vector2((ndc.X * 0.5f + 0.5f) * viewport.X, (1f - (ndc.Y * 0.5f + 0.5f)) * viewport.Y);
        return true;
    }
}
```

```csharp
using System.Numerics;

namespace MainframeEngine;

/// <summary>Debug icons for lights (Debug builds; the dev overlay toggles them): dots + range circles, spot cones, sun arrows.</summary>
public static class LightGizmos
{
    private static readonly Vector4 White = new(1, 1, 1, 1);

    public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, LightEnvironment lights, float scale)
    {
        var viewProjection = camera.ViewMatrix * camera.ProjectionMatrix;
        foreach (var point in lights.PointLights)
        {
            var color = new Vector4(point.Color, 1f);
            if (!GizmoProjection.TryProject(point.Position, viewProjection, viewport, out var at))
                continue;
            Icon(batch, at, color, scale);
            var faded = color with { W = 0.5f };
            Circle3D(batch, point.Position, Vector3.UnitX, Vector3.UnitY, point.Range, viewProjection, viewport, faded, scale);
            Circle3D(batch, point.Position, Vector3.UnitX, Vector3.UnitZ, point.Range, viewProjection, viewport, faded, scale);
            Circle3D(batch, point.Position, Vector3.UnitY, Vector3.UnitZ, point.Range, viewProjection, viewport, faded, scale);
        }

        foreach (var spot in lights.SpotLights)
        {
            var color = new Vector4(spot.Color, 1f);
            if (!GizmoProjection.TryProject(spot.Position, viewProjection, viewport, out var apex))
                continue;
            Icon(batch, apex, color, scale);
            var perp1 = Perpendicular(spot.Direction);
            var perp2 = Vector3.Normalize(Vector3.Cross(spot.Direction, perp1));
            var baseCentre = spot.Position + spot.Direction * spot.Range;
            var outer = spot.Range * MathF.Tan(spot.OuterConeAngle * MathF.PI / 180f);
            var inner = spot.Range * MathF.Tan(spot.InnerConeAngle * MathF.PI / 180f);
            Circle3D(batch, baseCentre, perp1, perp2, outer, viewProjection, viewport, color, scale);
            Circle3D(batch, baseCentre, perp1, perp2, inner, viewProjection, viewport, color with { W = 0.5f }, scale);
            for (var i = 0; i < 4; i++)
            {
                var angle = i * MathF.PI / 2f;
                var rim = baseCentre + (perp1 * MathF.Cos(angle) + perp2 * MathF.Sin(angle)) * outer;
                if (GizmoProjection.TryProject(rim, viewProjection, viewport, out var rimPixel))
                    batch.Line(apex, rimPixel, color, 1.2f * scale);
            }
        }

        foreach (var sun in lights.DirectionalLights)
        {
            var color = new Vector4(sun.Color, 1f);
            if (!GizmoProjection.TryProject(sun.Position, viewProjection, viewport, out var origin))
                continue;
            // Direction as seen on screen: project a point one unit along the light direction.
            var dir2d = GizmoProjection.TryProject(sun.Position + sun.Direction, viewProjection, viewport, out var ahead) && Vector2.DistanceSquared(ahead, origin) > 1e-6f
                ? Vector2.Normalize(ahead - origin)
                : new Vector2(0, 1);
            batch.Arrow(origin, origin + dir2d * 40f * scale, color, 2f * scale, 10f * scale);
            Sun(batch, origin, color, scale);
        }
    }

    private static void Icon(ScreenGizmoBatch batch, Vector2 at, Vector4 color, float scale)
    {
        batch.FilledCircle(at, 8f * scale, color);
        batch.Circle(at, 10f * scale, White, 1.5f * scale);
    }

    private static void Sun(ScreenGizmoBatch batch, Vector2 center, Vector4 color, float scale)
    {
        batch.FilledCircle(center, 6f * scale, color, 16);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            batch.Line(center + dir * 8f * scale, center + dir * 12f * scale, color, 1.5f * scale);
        }
    }

    private static void Circle3D(ScreenGizmoBatch batch, Vector3 center, Vector3 right, Vector3 up, float radius,
        in Matrix4x4 viewProjection, Vector2 viewport, Vector4 color, float scale, int segments = 32)
    {
        var hasPrevious = false;
        var previous = Vector2.Zero;
        for (var i = 0; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var world = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * radius;
            if (!GizmoProjection.TryProject(world, viewProjection, viewport, out var pixel))
            {
                hasPrevious = false; // break the polyline where a point is behind the camera
                continue;
            }

            if (hasPrevious)
                batch.Line(previous, pixel, color, 1.2f * scale);
            previous = pixel;
            hasPrevious = true;
        }
    }

    private static Vector3 Perpendicular(Vector3 v)
    {
        var other = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, other));
    }
}
```

(The old code drew the directional arrow from the raw world direction `(X, -Y)`; projecting a point along the
direction is the correct screen direction. Use the `LightEnvironment` collections and light member names the old
`DrawLightGizmos` used — copy them from `git show origin/main:MainframeEngine/Src/Lighting/LightEnvironment.cs`.)

- [ ] **Step 3: Axis gizmo (fixes the old Y-sign quirk)**

```csharp
using System.Numerics;

namespace MainframeEngine;

/// <summary>The corner XYZ axes (top-right), drawn back to front with stroke labels.</summary>
public static class AxisGizmo
{
    private static readonly (Vector3 Axis, Vector4 Color, char Label)[] Axes =
    [
        (Vector3.UnitX, new Vector4(0.86f, 0.24f, 0.24f, 1), 'X'),
        (Vector3.UnitY, new Vector4(0.24f, 0.78f, 0.24f, 1), 'Y'),
        (Vector3.UnitZ, new Vector4(0.24f, 0.47f, 0.86f, 1), 'Z'),
    ];

    private static readonly int[] Order = new int[3];
    internal static readonly Vector2[] LastTips = new Vector2[3];
    internal static Vector2 LastOrigin;

    public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, float scale)
    {
        var view = camera.ViewMatrix;
        var origin = new Vector2(viewport.X - 80f * scale, 80f * scale);
        LastOrigin = origin;
        // Back to front: smallest view-space z first (System.Numerics view: camera looks down -Z).
        for (var i = 0; i < 3; i++)
            Order[i] = i;
        for (var i = 1; i < 3; i++)
            for (var j = i; j > 0 && ViewZ(view, Order[j]) < ViewZ(view, Order[j - 1]); j--)
                (Order[j], Order[j - 1]) = (Order[j - 1], Order[j]);

        foreach (var index in Order)
        {
            var (axis, color, label) = Axes[index];
            var vx = axis.X * view.M11 + axis.Y * view.M21 + axis.Z * view.M31;
            var vy = axis.X * view.M12 + axis.Y * view.M22 + axis.Z * view.M32;
            var screen = new Vector2(vx, -vy); // view +Y is screen up
            var tip = screen.LengthSquared() > 1e-8f ? origin + Vector2.Normalize(screen) * 55f * scale : origin;
            LastTips[index] = tip;
            batch.Arrow(origin, tip, color, 2f * scale, 10f * scale);
            batch.Glyph(label, tip + new Vector2(4f, -6f) * scale, 10f * scale, color, 1.5f * scale);
        }
    }

    private static float ViewZ(in Matrix4x4 view, int index)
    {
        var axis = Axes[index].Axis;
        return axis.X * view.M13 + axis.Y * view.M23 + axis.Z * view.M33;
    }
}
```

- [ ] **Step 4: RenderServer flags**

```csharp
/// <summary>Draws light icons/ranges for the root viewport's lights (dev overlay toggle).</summary>
public bool ShowLightGizmos { get; set; }

/// <summary>Draws the corner XYZ axes for the root viewport's camera (dev overlay toggle).</summary>
public bool ShowAxisGizmo { get; set; }
```

At the end of `RenderMain(SceneViewport viewport)`, inside the `try` after `DrawLines`, and only for the root viewport
(`viewport.Parent is null` or however `RenderServer` tells the root apart — `RenderMain(Root)` is the root call):

```csharp
if (ShowLightGizmos || ShowAxisGizmo)
{
    var size = new Vector2(vk.SwapchainExtent.Width, vk.SwapchainExtent.Height);
    var scale = vk.ContentScale; // pixels per point; use the engine's content/pixel scale accessor
    if (ShowLightGizmos)
        LightGizmos.Draw(ScreenGizmos, camera, size, world.Lights, scale);
    if (ShowAxisGizmo)
        AxisGizmo.Draw(ScreenGizmos, camera, size, scale);
}
```

(If `IVulkanContext` has no content-scale accessor, add a `public float GizmoScale { get; set; } = 1f;` to
`RenderServer` and have `Engine` set it from `UiServer.PixelScale` each frame.)

- [ ] **Step 5: Delete the ImGui gizmos, update ShowcaseScene, run tests**

Delete `ImGuiCoordGizmo.cs`, `ImGuiGizmos.cs`, `ColorExtensions.cs`, and the `DrawLightGizmos`/`GetPerp`/
`DrawCircle3d`/`TryProjectToScreen` members plus `using ImGuiNET;` from `LightEnvironment.cs`. In `ShowcaseScene`,
remove `Lights.DrawLightGizmos(...)` and `ImGuiCoordGizmo.DrawCoordinateGizmo(...)` from `OnImGui` and set the two
`RenderServer` flags in `LoadScene`.

Run: `dotnet test Tests/MainframeEngine.Tests --filter GizmoTests && caffeinate -u -t 1200 & just test-render`
Expected: unit PASS; render PASS including `ShowcaseSteadyStateAllocatesNothing` (gizmos allocate nothing).

- [ ] **Step 6: Commit**

```bash
git add -A MainframeEngine/Src Tests
git commit -m "Gizmos: light and axis gizmos on ScreenGizmos; ImGui gizmo code removed"
```

---

### Task 5: Depth images in RmlUi (`engine://` texture sources)

**Files:**
- Modify: `MainframeEngine/Src/UI/Rendering/VulkanUiRenderer.cs` (`UiTextureConversion` enum, ~lines 11-25),
  `VulkanUiRenderer.Tables.cs` (`RegisterTexture` overloads ~150-217, `UiEngineTexture` ~555-597, `ResolveTexture` ~98-109),
  `VulkanUiRenderer.Resources.cs` (`AllocateTextureSet` ~415-449)
- Modify: `MainframeEngine/Content/Shaders/UI/Ui.vk.frag` (+ `.spv`, lock)
- Modify: `MainframeEngine/Src/UI/UiServer.cs` (internal forwarding overload)
- Test: `Tests/MainframeEngine.Tests/UI/UiEngineTextureTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  [Flags] public enum UiTextureConversion { None = 0, EncodeSrgb = 1, Premultiply = 2, DepthToGray = 4, Auto = 1 << 8 }
  internal readonly record struct UiTextureView(ImageView View, Sampler Sampler, ImageLayout Layout, uint Width, uint Height);
  internal void VulkanUiRenderer.RegisterTexture(string name, Func<UiTextureView> source, UiTextureConversion flags);
  internal void UiServer.RegisterTexture(string name, Func<UiTextureView> source, UiTextureConversion flags = UiTextureConversion.DepthToGray);
  ```
  A source returning `View.Handle == 0` means "nothing to show" (RmlUi draws nothing for that image this frame).

- [ ] **Step 1: Failing unit test (no GPU: `UiEngineTexture` view resolution only)**

```csharp
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.UI;

public sealed class UiEngineTextureTests
{
    [Fact]
    public void DepthSourceWithoutAViewIsNotDrawn()
    {
        var texture = new UiEngineTexture(static () => default, (uint)UiTextureConversion.DepthToGray);
        Assert.False(texture.TryGetView(out _, out _, out _));
    }

    [Fact]
    public void DepthSourceReportsItsLayoutAndSize()
    {
        var view = new UiTextureView(new ImageView(42), new Sampler(7), ImageLayout.DepthStencilReadOnlyOptimal, 512, 256);
        var texture = new UiEngineTexture(() => view, (uint)UiTextureConversion.DepthToGray);
        Assert.True(texture.TryGetView(out var image, out var sampler, out var layout));
        Assert.Equal((42ul, 7ul, ImageLayout.DepthStencilReadOnlyOptimal), (image.Handle, sampler.Handle, layout));
        Assert.Equal((512u, 256u), ((uint)texture.Size.X, (uint)texture.Size.Y));
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter UiEngineTextureTests` — Expected: FAIL to compile.

- [ ] **Step 2: Implement**

- `UiTextureConversion`: add `DepthToGray = 4` with doc "sample .r as grey, alpha 1 (depth maps)".
- `UiEngineTexture`: add a third source kind and widen `TryGetView` to return the layout:

```csharp
private readonly Func<UiTextureView>? _source;

public UiEngineTexture(Func<UiTextureView> source, uint flags)
{
    _source = source;
    Flags = flags;
}

public bool TryGetView(out ImageView view, out Sampler sampler, out ImageLayout layout)
{
    if (_source is not null)
    {
        var current = _source();
        (view, sampler, layout) = (current.View, current.Sampler, current.Layout);
        return view.Handle != 0;
    }

    layout = ImageLayout.ShaderReadOnlyOptimal;
    // (existing texture / render-target branches, unchanged, assign view + sampler)
}
```

`Size` returns `new Vector2(current.Width, current.Height)` for a source. Thread `layout` through
`AllocateTextureSet(ImageView view, Sampler sampler, ImageLayout layout)` (it currently hard-codes
`ImageLayout.ShaderReadOnlyOptimal`) and through its callers (`AddTexture` passes `ShaderReadOnlyOptimal`;
`LoadEngineTexture` and the re-bind in `ResolveTexture` pass the layout from `TryGetView`). In `ResolveTexture`, a
`false` from `TryGetView` skips the draw for that geometry instead of binding a stale set.
- Add the registration overload next to the existing two:

```csharp
/// <summary>Publishes an image whose view may change or disappear (shadow maps) as <c>engine://name</c>.</summary>
internal void RegisterTexture(string name, Func<UiTextureView> source, UiTextureConversion flags) =>
    Register(name, new UiEngineTexture(source, (uint)(flags & ~UiTextureConversion.Auto)));
```

and in `UiServer`:

```csharp
internal void RegisterTexture(string name, Func<UiTextureView> source, UiTextureConversion flags = UiTextureConversion.DepthToGray) =>
    Renderer?.RegisterTexture(name, source, flags);
```

- `Ui.vk.frag`: next to the existing `kEncodeSrgb`/`kPremultiply` handling add, before them:

```glsl
const uint kDepthToGray = 4u;
// ...
if ((push.textureFlags & kDepthToGray) != 0u)
    texel = vec4(texel.rrr, 1.0);
```

Run: `just shaders && dotnet test Tests/MainframeEngine.Tests --filter "UiEngineTextureTests|UiServerTests"`
Expected: PASS.

- [ ] **Step 3: Render tests (no visual change), commit**

Run: `just shaders-check && caffeinate -u -t 1200 & just test-render` — Expected: PASS.

```bash
git add MainframeEngine/Src/UI MainframeEngine/Content/Shaders Tests/MainframeEngine.Tests/UI/UiEngineTextureTests.cs
git commit -m "UI: engine:// texture sources with their own layout (depth maps shown grey)"
```

---

### Task 6: `DevOverlay` server, document and panel API

**Files:**
- Create: `MainframeEngine/Src/Debugging/DevOverlay/DevOverlay.cs`, `DevOverlayPanel.cs`, `DevOverlayDocument.cs`
- Create: `MainframeEngine/Content/UI/dev/overlay.rcss`
- Modify: `MainframeEngine/Src/Core/Engine.cs` (register after the `UiServer`; F12; `DevOverlayVisible` forwards;
  `EngineOptions.DevOverlayVisible` default **false**)
- Test: `Tests/MainframeEngine.Tests/Debugging/DevOverlayTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class DevOverlay : IServer, IFrameServer
  {
      public const int LayerOrder = int.MaxValue;
      public DevOverlay(SceneTree tree, float refreshInterval = 0.25f);
      public bool Visible { get; set; }
      public IReadOnlyList<DevOverlayPanel> Panels { get; }
      public DevOverlayPanel AddPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind = null);
      public bool RemovePanel(string id);
      public event Action? Refreshed;           // raised at refreshInterval while visible (built-in panels hook it)
      public void Process(in GameTime gameTime);
      public void Dispose();
  }
  public sealed class DevOverlayPanel
  {
      public string Id { get; } public string Title { get; } public string Rml { get; }
      public bool Expanded { get; set; }        // bound as "open" in the panel's model
      public RmlDataModel? Model { get; }       // non-null once bound (inside/after the bind callback)
      public void Dirty(string name);           // no-op until bound
  }
  ```
  Each panel's model is named `dev_{id}`; its body is wrapped as
  `<div class="dev-section" data-model="dev_{id}"><div class="dev-title" data-event-click="open = !open">{title}</div><div class="dev-body" data-if="open">{rml}</div></div>`.
  (This refines the spec's `AddPanel(id, title, rml)`: the model is created when the overlay document is ready, so
  binding happens in the callback. Update the spec's Extensibility paragraph in this task's commit.)

- [ ] **Step 1: Failing tests (headless UI)**

```csharp
using MainframeEngine.Tests.UI;

namespace MainframeEngine.Tests.Debugging;

[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayTests
{
    private static (UiTestTree Ui, DevOverlay Overlay) Create()
    {
        var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        return (ui, overlay);
    }

    [Fact]
    public void PanelsBindAndShowWhenVisible()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        var value = 7;
        overlay.AddPanel("test", "Test", "<p id='v'>{{v}}</p>", p => p.Model!.Bind("v", () => value));
        overlay.Visible = true;
        ui.Tick(3);
        var document = ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild<UiDocument>(0);
        Assert.Equal("7", document.GetElementById("v")!.InnerRml);
    }

    [Fact]
    public void PanelAddedAfterLoadIsBound()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.Visible = true;
        ui.Tick(3);
        overlay.AddPanel("late", "Late", "<p id='late'>{{x}}</p>", p => p.Model!.Bind("x", () => "ok"));
        ui.Tick(3);
        var document = ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild<UiDocument>(0);
        Assert.Equal("ok", document.GetElementById("late")!.InnerRml);
    }

    [Fact]
    public void HiddenOverlayDoesNotRefresh()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        var refreshes = 0;
        overlay.Refreshed += () => refreshes++;
        for (var i = 0; i < 60; i++)
            overlay.Process(new GameTime(1f / 60f));
        Assert.Equal(0, refreshes);
        overlay.Visible = true;
        for (var i = 0; i < 60; i++)
            overlay.Process(new GameTime(1f / 60f));
        Assert.InRange(refreshes, 3, 5); // ~4 Hz over one second
    }

    [Fact]
    public void DuplicatePanelIdsAreRejected()
    {
        var (ui, overlay) = Create();
        using var _ = ui;
        overlay.AddPanel("a", "A", "<p/>");
        Assert.Throws<ArgumentException>(() => overlay.AddPanel("a", "A", "<p/>"));
    }
}
```

(Match `UiTestTree`'s API, `UiElement.InnerRml` and `GameTime`'s constructor to the existing code; `UiTestTree.Tick`
must also process registered `IFrameServer`s — if it only ticks the tree, call `overlay.Process` in the test too.)

Run: `dotnet test Tests/MainframeEngine.Tests --filter DevOverlayTests` — Expected: FAIL (types missing).

- [ ] **Step 2: Implement**

`DevOverlayPanel.cs`:

```csharp
namespace MainframeEngine;

/// <summary>One collapsible section of the <see cref="DevOverlay"/>: RML body bound to its own data model.</summary>
public sealed class DevOverlayPanel
{
    internal DevOverlayPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind)
    {
        Id = id;
        Title = title;
        Rml = rml;
        Bind = bind;
    }

    public string Id { get; }
    public string Title { get; }
    public string Rml { get; }
    public bool Expanded { get; set; } = true;
    public RmlDataModel? Model { get; internal set; }
    internal Action<DevOverlayPanel>? Bind { get; }
    internal string ModelName => "dev_" + Id;

    public void Dirty(string name) => Model?.Dirty(name);
}
```

`DevOverlayDocument.cs`:

```csharp
using System.Text;

namespace MainframeEngine;

/// <summary>The overlay's single document: its RML is composed from the panels; models are created before it loads.</summary>
internal sealed class DevOverlayDocument : UiDocument
{
    private readonly DevOverlay _overlay;

    public DevOverlayDocument(DevOverlay overlay)
    {
        _overlay = overlay;
        Name = "Panels";
        AutoFocus = false;
        Rml = Compose(overlay.Panels);
    }

    internal bool IsReady { get; private set; }

    protected override void OnReady()
    {
        foreach (var panel in _overlay.Panels)
            BindPanel(panel);
        IsReady = true;
    }

    internal void BindPanel(DevOverlayPanel panel)
    {
        panel.Model = CreateDataModel(panel.ModelName)
            .Bind("open", panel, static p => p.Expanded, static (p, v) => p.Expanded = v);
        panel.Bind?.Invoke(panel);
    }

    internal void Recompose()
    {
        Rml = Compose(_overlay.Panels);
        Reload();
    }

    internal static string Compose(IReadOnlyList<DevOverlayPanel> panels)
    {
        var rml = new StringBuilder("""
            <rml><head><title>Developer overlay</title>
            <link type="text/rcss" href="/Content/UI/widgets/widgets.rcss"/>
            <link type="text/rcss" href="/Content/UI/dev/overlay.rcss"/></head>
            <body class="dev-overlay"><div id="dev-panel">
            """);
        foreach (var panel in panels)
            rml.Append($"""<div class="dev-section" data-model="{panel.ModelName}"><div class="dev-title" data-event-click="open = !open">{System.Security.SecurityElement.Escape(panel.Title)}</div><div class="dev-body" data-if="open">{panel.Rml}</div></div>""");
        rml.Append("</div></body></rml>");
        return rml.ToString();
    }
}
```

`DevOverlay.cs`:

```csharp
namespace MainframeEngine;

/// <summary>
/// The F12 developer overlay: an RmlUi layer on top of everything with collapsible panels (renderer, shadows, GPU memory,
/// audio, physics, network, plus game panels from <see cref="AddPanel"/>). Values refresh at <c>refreshInterval</c>
/// while visible; hidden, it does no work. Reach it with <c>Tree.Servers.Get&lt;DevOverlay&gt;()</c>.
/// </summary>
public sealed class DevOverlay : IServer, IFrameServer
{
    public const int LayerOrder = int.MaxValue;

    private readonly SceneTree _tree;
    private readonly float _refreshInterval;
    private readonly List<DevOverlayPanel> _panels = [];
    private UiLayer? _layer;
    private DevOverlayDocument? _document;
    private float _sinceRefresh;
    private bool _visible;

    public DevOverlay(SceneTree tree, float refreshInterval = 0.25f)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _refreshInterval = refreshInterval;
    }

    public IReadOnlyList<DevOverlayPanel> Panels => _panels;

    public event Action? Refreshed;

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (value)
                EnsureLayer();
            if (_layer is not null)
                _layer.Visible = value;
            _sinceRefresh = _refreshInterval; // refresh on the next frame after showing
        }
    }

    public DevOverlayPanel AddPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_panels.Exists(p => p.Id == id))
            throw new ArgumentException($"A dev overlay panel '{id}' already exists.", nameof(id));
        var panel = new DevOverlayPanel(id, title, rml, bind);
        _panels.Add(panel);
        if (_document is { IsReady: true } document)
        {
            document.BindPanel(panel);
            document.Recompose();
        }

        return panel;
    }

    public bool RemovePanel(string id)
    {
        var removed = _panels.RemoveAll(p => p.Id == id) > 0;
        if (removed && _document is { IsReady: true } document)
            document.Recompose();
        return removed;
    }

    public void Process(in GameTime gameTime)
    {
        if (!_visible)
            return;
        _sinceRefresh += gameTime.DeltaTime;
        if (_sinceRefresh < _refreshInterval)
            return;
        _sinceRefresh = 0f;
        Refreshed?.Invoke();
    }

    private void EnsureLayer()
    {
        if (_layer is not null)
            return;
        _layer = new UiLayer { Name = "DevOverlay", Layer = LayerOrder, Visible = _visible };
        _document = new DevOverlayDocument(this);
        _layer.AddChild(_document);
        _tree.Root.AddChild(_layer);
    }

    public void Dispose()
    {
        if (_layer is { IsInsideTree: true })
            _layer.Free();
        _layer = null;
        _document = null;
    }
}
```

(A model removed by `RemovePanel` stays in RmlUi until the document reloads, which `Recompose` does; if RmlUi rejects a
second `CreateDataModel` with the same name after reload, keep a set of created names and reuse the model.)

`MainframeEngine/Content/UI/dev/overlay.rcss`:

```css
body.dev-overlay { pointer-events: none; font-size: 12dp; color: #e5e7eb; }
#dev-panel {
    pointer-events: auto; position: absolute; top: 8dp; right: 8dp; bottom: 8dp; width: 360dp;
    overflow-y: auto; padding: 6dp; border-radius: 8dp; background-color: #0b1020e0; border: 1dp #ffffff22;
}
.dev-title { padding: 4dp 6dp; font-weight: bold; background-color: #ffffff12; border-radius: 4dp; margin-top: 6dp; }
.dev-title:hover { background-color: #ffffff22; }
.dev-body { padding: 4dp 6dp; }
.dev-row { display: flex; justify-content: space-between; }
.dev-row span.v { color: #93c5fd; font-family: "Roboto Mono"; }
.dev-meter { height: 6dp; background-color: #22c55e; }
```

- [ ] **Step 3: Wire into `Engine`**

- `EngineOptions.DevOverlayVisible` default `false`; doc: "Shows the developer overlay (RmlUi, F12) at start-up."
- In `Engine.OnLoad`, right after `Servers.Register(ui);`:

```csharp
DevOverlay = new DevOverlay(Tree) { Visible = DevOverlayVisible };
Servers.Register(DevOverlay);
```

- New property `public DevOverlay? DevOverlay { get; private set; }`; `DevOverlayVisible`'s setter also sets
  `DevOverlay.Visible` (keep the backing field for before `OnLoad`).
- (ImGui still renders on F12 until Task 9; that double display is temporary.)

- [ ] **Step 4: Run tests, commit**

Run: `dotnet test Tests/MainframeEngine.Tests --filter DevOverlayTests && just test`
Expected: PASS.

```bash
git add MainframeEngine/Src/Debugging/DevOverlay MainframeEngine/Content/UI/dev MainframeEngine/Src/Core/Engine.cs \
  Tests/MainframeEngine.Tests/Debugging docs/design/future/remove-imgui.md
git commit -m "DevOverlay: RmlUi developer overlay server with a panel API"
```

---

### Task 7: Built-in DevOverlay panels

**Files:**
- Create: `MainframeEngine/Src/Debugging/DevOverlay/DevOverlayPanels.cs` (one static method per panel) and
  `MainframeEngine/Src/Debugging/DevOverlay/DevOverlayStats.cs` (snapshot structs + cached strings)
- Modify: `Engine.cs` (`DevOverlayPanels.AddBuiltIns(DevOverlay, this)` after registering the overlay)
- Test: `Tests/MainframeEngine.Tests/Debugging/DevOverlayPanelsTests.cs`

**Interfaces:**
- Produces: `internal static class DevOverlayPanels { public static void AddBuiltIns(DevOverlay overlay, Engine engine); }`
  with panel ids `frame`, `renderer`, `shadows`, `gpu`, `audio`, `physics`, `network`; RmlUi texture names
  `engine://dev-shadow-cascade-{0..MaxCascades-1}` and `engine://dev-shadow-atlas`.

The values each panel shows are exactly what the ImGui windows showed (read the old code with
`git show origin/main:MainframeEngine/Src/Rendering/Vulkan/RendererDebugWindow.cs`,
`git show origin/main:MainframeEngine/Src/Audio/AudioImGui.cs`, and for the network counters
`git show origin/main:MainframeEngine.Sandbox/Src/NetworkDemo.cs`). Rules for every panel:
- Bind numbers as `int`/`float` and format in RML (`{{ms | format(2)}}`); never build strings per refresh except when
  the underlying value changed (cache the last value + string).
- Bindings use `Bind(name, owner, static getter, static setter)`; snapshot structs are filled on `Refreshed`, then
  `panel.Dirty(...)` for each changed name.

- [ ] **Step 1: Failing tests**

```csharp
using MainframeEngine.Tests.UI;

namespace MainframeEngine.Tests.Debugging;

[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayPanelsTests
{
    [Fact]
    public void BuiltInsAreTheSevenPanelsInOrder()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        Assert.Equal(["frame", "renderer", "shadows", "gpu", "audio", "physics", "network"], overlay.Panels.Select(p => p.Id));
    }

    [Fact]
    public void PanelsWithoutTheirServerShowPlaceholdersAndDoNotThrow()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        overlay.Visible = true;
        for (var i = 0; i < 30; i++)
        {
            overlay.Process(new GameTime(1f / 60f));
            ui.Tick();
        }
        // No renderer/audio/physics/network servers registered: panels render "—" and no RmlUi errors are logged.
        Assert.Empty(ui.Errors);
    }
}
```

(`AddBuiltIns` takes `Engine?` so tests can pass null; every panel reads its server through `tree.Servers.Get<T>()`
and tolerates null. Use the `UiTestTree` error list name from `RmlTestHost.cs`.)

Run: `dotnet test Tests/MainframeEngine.Tests --filter DevOverlayPanelsTests` — Expected: FAIL.

- [ ] **Step 2: Implement the panels**

Frame panel (full code; the others follow the same pattern with the fields listed below):

```csharp
namespace MainframeEngine;

internal static class DevOverlayPanels
{
    public static void AddBuiltIns(DevOverlay overlay, Engine? engine)
    {
        AddFrame(overlay, engine);
        AddRenderer(overlay, engine);
        AddShadows(overlay, engine);
        AddGpu(overlay, engine);
        AddAudio(overlay, engine);
        AddPhysics(overlay, engine);
        AddNetwork(overlay, engine);
    }

    private sealed class FrameStats
    {
        public float Fps, Ms, MinMs, MaxMs;
        public long Frame;
        public int UiDrawCalls;
        public bool VSync;
        internal float WindowMin = float.MaxValue, WindowMax, WindowSum;
        internal int WindowCount;
    }

    private static void AddFrame(DevOverlay overlay, Engine? engine)
    {
        var stats = new FrameStats();
        var panel = overlay.AddPanel("frame", "Frame", """
            <div class="dev-row"><span>FPS</span><span class="v">{{fps | format(1)}}</span></div>
            <div class="dev-row"><span>Frame ms (avg/min/max)</span><span class="v">{{ms | format(2)}} / {{min | format(2)}} / {{max | format(2)}}</span></div>
            <div class="dev-row"><span>Frame</span><span class="v">{{frame}}</span></div>
            <div class="dev-row"><span>VSync</span><input type="checkbox" data-checked="vsync"/></div>
            <div class="dev-row"><span>UI draw calls</span><span class="v">{{ui}}</span></div>
            """, p => p.Model!
            .Bind("fps", stats, static s => s.Fps)
            .Bind("ms", stats, static s => s.Ms)
            .Bind("min", stats, static s => s.MinMs)
            .Bind("max", stats, static s => s.MaxMs)
            .Bind("frame", stats, static s => (int)s.Frame)
            .Bind("ui", stats, static s => s.UiDrawCalls)
            .Bind("vsync", engine, static e => e?.Renderer.VSync ?? false, static (e, v) => { if (e is not null) e.Renderer.VSync = v; }));

        // Accumulate every frame (cheap, no allocation) through the engine's frame-time accessor; publish at refresh.
        overlay.Frame += dt =>
        {
            var ms = dt * 1000f;
            stats.WindowMin = MathF.Min(stats.WindowMin, ms);
            stats.WindowMax = MathF.Max(stats.WindowMax, ms);
            stats.WindowSum += ms;
            stats.WindowCount++;
        };
        overlay.Refreshed += () =>
        {
            if (stats.WindowCount == 0) return;
            stats.Ms = stats.WindowSum / stats.WindowCount;
            stats.Fps = 1000f / MathF.Max(stats.Ms, 1e-3f);
            (stats.MinMs, stats.MaxMs) = (stats.WindowMin, stats.WindowMax);
            (stats.WindowMin, stats.WindowMax, stats.WindowSum, stats.WindowCount) = (float.MaxValue, 0f, 0f, 0);
            stats.Frame = engine?.Renderer is IVulkanContext vk ? (long)vk.FrameNumber : stats.Frame + 1;
            stats.UiDrawCalls = engine?.Ui?.Renderer?.Stats.DrawCalls ?? 0;
            panel.Model?.DirtyAll();
        };
    }
}
```

This needs one more `DevOverlay` member: `public event Action<float>? Frame;` raised from `Process` **every** visible
frame with `gameTime.DeltaTime` (add it in this task; extend `HiddenOverlayDoesNotRefresh` to also assert `Frame` is
not raised while hidden). Use the engine's real accessors for the UI server (`Engine.Ui` or
`Tree.Servers.Get<UiServer>()`) and VSync.

The remaining panels, each `AddX(DevOverlay overlay, Engine? engine)`, with model fields (bind exactly these names)
and sources:

| Panel (id, title) | Fields → source |
|---|---|
| `renderer`, "Renderer" | `exposure` (rw float, `IVulkanContext.Exposure`; slider 0.1–8 + `reset()` event → `IVulkanContext.DefaultExposure`); `pipeline` (string, cached: `$"{SceneColorFormat} → {SwapchainFormat} ({Encoding})"` from `VulkanRenderer`); `draws`, `surfaces`, `instances`, `culled`, `pipelineBinds`, `materialBinds`, `shadowDraws`, `idDraws` (ints, `RenderServer.MeshStats`); `meshes`, `materials`, `textures` (`RenderServer.ResidentResources`); `cacheHits` (`RenderServer.PipelineStates?.Hits`) |
| `shadows`, "Shadows" | `passes`, `planned` (ints), `cascadeRes`, `atlasSize`, `atlasPacked` (ints), `mapMiB` (float), `cpuMs`, `gpuMs` (floats) from `RenderServer.ExistingShadows`; rw `debugCascades`, `stable` (bools), `filter` (int, `ShadowFilter`), `radius` (float 0.5–8); images: `<img src="engine://dev-shadow-cascade-{i}" class="dev-map"/>` for `i < ShadowSystem.MaxCascades` (96 dp) and `engine://dev-shadow-atlas` (256 dp), registered once in the bind callback via `ui.RegisterTexture(name, () => View(shadows, i))` where `View` returns `new UiTextureView(shadows.CascadeLayerView(i), shadows.DebugSampler, ImageLayout.DepthStencilReadOnlyOptimal, res, res)` or `default` when there is no shadow system |
| `gpu`, "GPU memory" | `allocations`, `deviceMemories`, `blocks`, `dedicated` (ints), `usedMiB`, `reservedMiB` (floats) from `vk.Allocator.Totals`; `types` (list via `BindList` of a small struct: index, heap, flags string cached, allocations, usedMiB, reservedMiB from `vk.Allocator.GetMemoryTypeStats(i)`); `ringUsedKiB`, `ringKiB`, `uploadsPending`, `deletionsPending`, `pipelineCacheKiB`, `shaderModules` |
| `audio`, "Audio" | `device` (string, cached), `voices`, `totalVoices`, `steals`, `underruns` (ints from `AudioServer.Stats`); `buses` (`BindList(audio.Buses, BusType)` with members `name`, `volume` (rw: `≤ -60 → AudioMath.SilenceDb`), `mute` (rw), `solo` (rw), `meter` (float 0..1: `Clamp((AudioMath.LinearToDb(bus.Peak) + 60) / 60, 0, 1)`)); RML uses `data-for="b : buses"` with a range input `min=-60 max=6`, two checkboxes and `<div class="dev-meter" data-style-width="(b.meter * 100) + '%'"/>` |
| `physics`, "Physics" | `shapes` (rw bool: both `PhysicsServer3D/2D.DebugDrawEnabled`); `bodies3d`, `awake3d` (ints: `PhysicsServer3D.FindSpace(root world).ObjectCount/ActiveBodyCount`); `bodies2d`, `awake2d` likewise for 2D; `lightGizmos`, `axisGizmo` (rw bools → `RenderServer.ShowLightGizmos/ShowAxisGizmo`) |
| `network`, "Network" | section body wrapped in `<div data-if="active">`; `active` (bool: a `MultiplayerApi` peer is active); `tick`, `nodes`, `clients` (ints), `outBps`, `inBps` (floats) — the members `NetworkDemo.DrawImGui` read |

Run: `dotnet test Tests/MainframeEngine.Tests --filter "DevOverlayPanelsTests|DevOverlayTests"` — Expected: PASS.

- [ ] **Step 3: Look at it**

```bash
dotnet run --project Examples/Demo/Demo.Launcher
```

Press F12 in each Demo tab. Every panel populates; sliders/checkboxes change the game (exposure, VSync, bus volumes,
collision shapes, gizmos); the Shadows panel shows the cascades and atlas in grey; the Network panel is hidden.

- [ ] **Step 4: Commit**

```bash
git add MainframeEngine/Src/Debugging/DevOverlay MainframeEngine/Src/Core/Engine.cs Tests/MainframeEngine.Tests/Debugging
git commit -m "DevOverlay: frame, renderer, shadows, GPU memory, audio, physics and network panels"
```

---

### Task 8: Render tests on the DevOverlay

**Files:**
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/ShowcaseScene.cs` (drop `OnImGui`; show the overlay)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/ShadowScenes.cs:178-187` (drop `OnImGui`; `--count 2` shows the
  overlay with only the Shadows panel expanded)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/MeshScenes.cs` (`PickingScene`: show the sub-viewport through a UI
  `<img src="engine://picking-preview">` at (8,8) 96×96; the check at ~383 uses `ColorTarget` registration instead of
  `ImGuiTextureId`)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/RenderTestGame.cs` (`CreateOptions`: `DevOverlayVisible = false` explicitly)
- Modify: `Tests/MainframeEngine.RenderTests/SceneTests.cs`, `ShadowTests.cs`; goldens `picking_frame0020.png` (both drivers)

- [ ] **Step 1: Showcase with the overlay visible (allocation gate)**

In `ShowcaseScene.LoadScene`: `DevOverlayVisible = true;` and expand every panel. Delete its `OnImGui` override and the
`ImGuiNET`/`MainframeEngine.Gizmos` usings. The Spanish-catalog lines it drew through ImGui (`Tr._("Language")`,
`Tr.P("overlay section","Camera")`, `Tr._("Render test")`) move into a test-only dev panel added in `LoadScene`:

```csharp
DevOverlay!.AddPanel("render-test", Tr._("Render test"), """
    <div class="dev-row"><span>{{language}}</span><span class="v">{{locale}}</span></div>
    <div class="dev-row"><span>{{camera}}</span><span class="v">{{x | format(2)}}, {{y | format(2)}}, {{z | format(2)}}</span></div>
    """, p => p.Model!
    .Bind("language", this, static s => Tr._("Language"))
    .Bind("camera", this, static s => Tr.P("overlay section", "Camera"))
    .Bind("locale", this, static s => Tr.CurrentLocale)
    .Bind("x", this, static s => s.Camera.GlobalPosition.X)
    .Bind("y", this, static s => s.Camera.GlobalPosition.Y)
    .Bind("z", this, static s => s.Camera.GlobalPosition.Z));
```

(`Tr._` returns a cached catalog string — no allocation per call; verify by the gate.) In `SceneTests`, rename the gate
comment's "ImGui text formatting" to "dev overlay formatting".

Run: `dotnet test Tests/MainframeEngine.RenderTests --filter ShowcaseSteadyStateAllocatesNothing`
Expected: PASS with `AllocatedBytes == 0`. If it fails, find the allocation with
`DOTNET_gcServer=0 dotnet-trace` or by bisecting panels (disable panels until it passes) and fix it at the source.

- [ ] **Step 2: Shadows panel validation (replaces `ShadowMapViewerIsValidationClean`)**

`ShadowLightsScene`: when `Host.Count == 2`, in `LoadScene`: `DevOverlayVisible = true;` and set `Expanded = false` on
every panel except `shadows`. `ShadowTests`:

```csharp
[Fact]
public void DevOverlayShadowsPanelIsValidationClean()
{
    // The dev overlay's Shadows panel samples the cascade layers and the atlas in their read-only depth layout, also
    // across a swapchain rebuild (--resize).
    var result = HostRunner.Run("shadow-lights", Output("shadow-lights-overlay"), "--count", "2", "--resize", "400x300@8", "--capture", "12", "--hidden");
    Assert.Empty(result.SceneCheckFailures);
    Gates.AssertValidationClean(result);
}
```

Delete `ShadowMapViewerIsValidationClean`. Run it — Expected: PASS.

- [ ] **Step 3: Picking preview through the UI; re-record the golden**

In `PickingScene.LoadScene`, after creating `_view`:

```csharp
var layer = new UiLayer { Name = "Preview", Layer = 0 };
Tree.Root.AddChild(layer);
layer.AddChild(new UiDocument
{
    Name = "PreviewDoc",
    AutoFocus = false,
    Rml = """<rml><head><style>body{pointer-events:none;} img{position:absolute;left:8px;top:8px;width:96px;height:96px;}</style></head><body><img src="engine://picking-preview"/></body></rml>""",
});
```

and once `_view.ColorTarget` exists (first `UpdateScene` where it is non-null) call
`Servers.Get<UiServer>()!.RegisterTexture("picking-preview", _view.ColorTarget)`. Replace the check
`_view.ImGuiTextureId == 0` with a `_registered` flag. Delete the `OnImGui` override and `using ImGuiNET;`.
(`px` in RCSS are layout pixels; with the host scale the image covers the same framebuffer area ImGui's point
coordinates did. If the golden differs only in that inset, that is expected.)

```bash
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter ObjectIdPickingAndSubViewportsWork
just render-tests-linux   # records/checks the lavapipe golden; inspect it
```

Inspect both `picking_frame0020.png`: the 96×96 preview sits in the top-left as before.

- [ ] **Step 4: Explicit overlay default in the host; full render suite; commit**

`RenderTestGame.CreateOptions`: `DevOverlayVisible = false,` (scenes opt in). Remove the empty `OnImGui` in
`SkyGridScene.cs:57-59`.

Run: `caffeinate -u -t 1200 & just test-render` — Expected: PASS.

```bash
git add Tests/MainframeEngine.RenderTests Tests/MainframeEngine.RenderTests.Host
git commit -m "Render tests: dev overlay replaces ImGui (allocation gate, shadow maps, picking preview)"
```

---

### Task 9: Delete ImGui

**Files:**
- Delete: `MainframeEngine/Src/Rendering/Vulkan/VulkanImGuiController.cs`, `RendererDebugWindow.cs`,
  `MainframeEngine/Src/Audio/AudioImGui.cs`, `MainframeEngine/Content/Shaders/ImGui/` (sources + `.spv`)
- Modify: `MainframeEngine/Src/Core/Engine.cs` (all ImGui lines: field `_vkImGuiController`, creation in `OnLoad`,
  `Update`/`OnImGui` call in the update loop, `DiscardFrame` when minimised, `Render`/`DiscardFrame` before `EndFrame`,
  the `OnImGui` virtual, dispose in `OnClose`, doc comments at the "ImGui" mentions)
- Modify: `IVulkanContext.cs` (remove `ImGuiTextures` and `IImGuiTextureRegistry`, ~135-162), `VulkanRenderer.cs:146`
- Modify: `MainframeEngine/Src/Scene/SubViewport.cs` (remove `ImGuiTextureId` and its register/update/unregister)
- Modify: `MainframeEngine/MainframeEngine.csproj:18`, `Directory.Packages.props:26`
- Modify: comment-only mentions: `WindowPixels.cs`, `UiSystemInterface.cs`, `VulkanUiRenderer.cs`, `UploadQueue.cs`,
  `IOverlayRenderer.cs`, `VulkanRenderer.Presentation.cs`, `SubViewportCompositor.cs`, `Tools/MainframeEngine.L10n/PseudoLocalizer.cs`
- Modify: `.gitignore` and `Templates/MainframeEngine.Templates/content/mfgame/.gitignore` (`imgui.ini` lines)
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs:662` (F12 shortcut text: "Developer overlay"),
  `MainframeEngine.Editor/Src/EditorApp.cs:87` (keep `DevOverlayVisible = false`)
- Rename: `Tests/MainframeEngine.Tests/Rendering/ColorPipelineTests.cs` `UnormIsPreferredSoImGuiStaysExact` →
  `UnormIsPreferredSoOverlaysStayExact`

- [ ] **Step 1: Delete and fix compile errors**

```bash
git rm MainframeEngine/Src/Rendering/Vulkan/VulkanImGuiController.cs MainframeEngine/Src/Rendering/Vulkan/RendererDebugWindow.cs \
  MainframeEngine/Src/Audio/AudioImGui.cs
git rm -r MainframeEngine/Content/Shaders/ImGui
```

Remove the package reference and version. In `Engine.cs` the render tail becomes just `Renderer.EndFrame();` (it runs
the overlay pass when no one else did); the minimised branch drops `DiscardFrame`; the update loop drops the ImGui
`Update` and the `OnImGui` call. Keep `DevOverlayVisible`/`DevOverlayKey` (they drive `DevOverlay`). Build until clean:
`just build`.

- [ ] **Step 2: Lock and grep**

```bash
just shaders            # rewrites shaders.lock without the ImGui rows
just shaders-check
git grep -n -i imgui -- ':!memory' ':!docs' ':!*.lock'
```

Expected: the grep prints nothing (docs are Task 10).

- [ ] **Step 3: All gates; publish check**

```bash
just build && dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test && caffeinate -u -t 1200 & just test-render
just format-check && just shaders-check
just publish-local && ls artifacts/publish/*/ | grep -i -E "imgui|cimgui" || echo "no imgui"
```

Expected: all green; prints `no imgui`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Remove ImGui (ImGui.NET, controller, shaders, texture registry, OnImGui)"
```

---

### Task 10: Docs and ADR

**Files:**
- Delete: `docs/design/imgui-and-debug-tools.md`; Create: `docs/design/dev-overlay.md`
- Create: `memory/decisions/0115-remove-imgui.md`
- Modify: `CLAUDE.md`, `README.md`, `THIRD_PARTY_NOTICES.md` (ImGui.NET entry), `MainframeEngine/Content/UI/credits.rml:23`,
  `docs/README.md`, `docs/milestones.md` (D2 ✅), design docs mentioning ImGui, SVGs
  `docs/images/{architecture-layers,main-render-pass,viewport-and-depth,rmlui-integration}.svg`

- [ ] **Step 1: `docs/design/dev-overlay.md`**

Sections: what it is (F12, `EngineOptions.DevOverlayVisible`, `Engine.DevOverlay`, `Tree.Servers.Get<DevOverlay>()`);
built-in panels table (id → contents); `AddPanel(id, title, rml, bind)` with a short example; refresh model (4 Hz,
`Refreshed`, `Frame`), the no-allocation rules; shadow-map images (`engine://dev-shadow-*`, depth sources,
`UiTextureConversion.DepthToGray`); `ScreenGizmos` (`RenderServer.ScreenGizmos`, API, coordinates, overlay order,
`ShowLightGizmos`, `ShowAxisGizmo`).

- [ ] **Step 2: ADR 0115**

`memory/decisions/0115-remove-imgui.md`: Context (two UI stacks; overlay unreachable from GameHost; cimgui native),
Decision (RmlUi is the only UI stack; DevOverlay + ScreenGizmos), Consequences (no ImGui.NET; debug UI uses RML;
goldens re-recorded; `OnImGui` removed — games use `AddPanel`), Status Accepted, date 2026-10-06. Follow the format of
`memory/decisions/0114-godot-runtime-rules.md`.

- [ ] **Step 3: Sweep**

`git grep -n -i imgui -- docs README.md CLAUDE.md THIRD_PARTY_NOTICES.md MainframeEngine/Content` and fix every hit:
`CLAUDE.md` frame order step 1 (delete the `OnImGui` step, renumber), step 7 ("then ImGui — the developer overlay" →
"then the UI layers, the dev overlay on top"), the legacy-hooks sentence (drop `OnImGui`), Dependencies (drop ImGui.NET),
the RmlUi-debugger line stays. Edit the SVG text labels in place. Mark the spec
`docs/design/future/remove-imgui.md` status ✅ and D2 ✅ in `docs/milestones.md`.

- [ ] **Step 4: Commit**

```bash
git add -A docs memory CLAUDE.md README.md THIRD_PARTY_NOTICES.md MainframeEngine/Content/UI/credits.rml
git commit -m "Docs: dev overlay and screen gizmos replace ImGui (ADR 0115)"
```
