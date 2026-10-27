# ADR 0216 — Sub-viewport textures on 3D materials

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G11 (part: G11.1's GPU textures)
- **Docs:** docs/design/future/world-space-ui.md#what-shipped-adr-0216, docs/design/materials-and-meshes.md#offscreen-views-subviewport,
  docs/design/canvas.md#2d-sub-viewports-adr-0116

## Context

The Neon example's apartment has PC monitors and a TV that show live scenes: a `SubViewport` on a screen mesh, as in
Godot (`material.albedo_texture = $SubViewport.get_texture()`). `Texture2D.FromViewport` served canvas items only. A 3D
material's `TextureGpu.Update` decoded CPU pixels, and a viewport texture has none: it threw "has no CPU pixels", the
slot logged "cannot be uploaded" and the screen drew untextured.

The two kinds of view finish at different points of the frame:

- A 3D view renders in `RenderServer.RenderOffscreen`, before the scene pass, into its LDR target (`SubViewportTargets.Ldr`).
- A 2D view (`Disable3D`) is drawn by the canvas renderer in `IOverlayRenderer.RecordOffscreen`, after the scene pass,
  into a target of its own (`VulkanCanvasRenderer.SubTarget`).

Both images hold sRGB-encoded values in `R8G8B8A8_UNORM` (the UI samples them as stored), while a colour slot of a
material expects to sample linear colour.

## Decision

- **`TextureGpu` gets a viewport path.** For `Texture.Viewport is { }` it uploads nothing: `Gpu` is a borrowed
  `GpuTexture` (`GpuTexture.Borrow`: the view's image, a view of it, a sampler; `Dispose` releases nothing) over the
  image the view shows now. Every material slot (standard albedo/emission/normal/ORM, particles, spatial `ShaderMaterial`
  samplers) goes through `TextureGpu`, so all of them work without changes. `Update` runs whenever the material is
  prepared: it compares the view's current `GpuImage` by reference and only on a change (first image, resize, a new
  target) bumps `Generation`, which makes materials write a fresh descriptor set (the old one is freed through
  `IVulkanContext.Deletions`, as for a reimported texture). Steady frames allocate nothing. The texture owns one sampler
  from its import settings (default linear, repeat, anisotropy 8).
- **Colour decoding by view format.** The LDR target and the canvas sub-viewport targets are created with
  `MUTABLE_FORMAT` (`RenderTargetAttachment.MutableFormat`), and colour slots sample them through an `R8G8B8A8_SRGB`
  view (`GpuImage.ViewAs`, created once per image and owned by it). Data slots and spatial samplers without
  `source_color` sample the stored values, as Godot does.
- **3D views: this frame's image.** `SubViewportTargets.Presented` is the LDR image after the view's last finished
  render (set at the end of `RenderSubViewport`, after the tonemap or post chain and any capture copy, so it is in
  `SHADER_READ_ONLY_OPTIMAL`). Views render before the scene pass, so the scene samples the frame's image. The LDR pass's
  `End` barrier already makes the writes visible to fragment sampling; the incoming subpass dependency orders the next
  frame's writes after this frame's reads. A view sampling itself (or a view that renders later) sees the previous frame.
  `Ensure` clears `Presented` on a resize: materials prepared before it keep the old image, which the deletion queue keeps
  alive past the frame; those prepared after it wait for the new one, so nothing samples an image still `UNDEFINED`.
- **2D views: the previous frame's image, one target.** The canvas draws after the scene pass, so the scene must sample
  the image drawn the frame before. Per-frame-slot targets or a copy would cost a second image (and a copy per frame);
  neither is needed. The single target is read before it is written in submission order: the scene pass samples it
  (frame N − 1's content), then the canvas pass of frame N overwrites it. The canvas render pass's incoming dependency
  (source stages include the fragment shader) orders that write after the reads, and its outgoing dependency plus the
  renderer's explicit barrier make frame N's write visible to frame N + 1's sampling, across submissions on the one
  queue. `VulkanCanvasRenderer.SampleSubViewport` returns the target (null before its first pass) and marks it used, so a
  view whose updates are disabled keeps its image while a material shows it.
- **Resizes** replace the image: the material's next `Update` sees another `GpuImage`, rewrites its set and drops the
  old one through the deletion queue. Nothing waits idle.
- Not `Texture2D.FromGpuSource`: G11.1's general external-texture API waits for `UiPanel3D`, whose images come from the
  UI renderer. CPU consumers (probe bakes, ORM packing, tree bakes) still get no pixels from a viewport texture and fall
  back as before.

## Consequences

- `SubViewport.GetTexture()` works on 3D materials and on spatial shader sampler uniforms. A 3D view shows the current
  frame; a 2D view lags one frame (invisible for screens). Before a view's first frame the slot is empty (the material's
  colour alone).
- 2D view targets hold premultiplied colour: a `TransparentBg` 2D view on a material shows darkened edges where it is
  partly transparent. Give screens an opaque clear colour (the default).
- Images used as viewport textures carry `MUTABLE_FORMAT`, which may disable framebuffer compression on some GPUs; it
  applies only to the views' LDR targets and 2D view targets.
- Sampling a viewport texture from a spatial shader's `vertex()` is not ordered: the barriers cover fragment sampling.
- Tests: `ViewportTextureTests` (no GPU: a viewport slot waits instead of failing an upload) and the `viewport-material`
  render test (a 2D pattern and a 3D camera view on albedo, emission and a spatial shader sampler, followed across a
  resize; golden, validation gate, 0 B per frame with a moving view).
