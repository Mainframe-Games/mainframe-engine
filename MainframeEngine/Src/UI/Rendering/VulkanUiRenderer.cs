using System.Numerics;
using System.Runtime.CompilerServices;
using MainframeEngine.UI.Rml;
using Silk.NET.Vulkan;
using StbImageSharp;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>How an engine texture (<c>engine://name</c>) is converted for the UI, which works in premultiplied sRGB.</summary>
[Flags]
public enum UiTextureConversion
{
    /// <summary>Sampled as stored: premultiplied, sRGB-encoded 8-bit values.</summary>
    None = 0,

    /// <summary>The sampler returns linear colour (an <c>_SRGB</c> or float format): encode to sRGB in the shader.</summary>
    EncodeSrgb = 1,

    /// <summary>The texture has straight alpha: premultiply in the shader.</summary>
    Premultiply = 2,

    /// <summary>Sample <c>.r</c> as grey with alpha 1 (depth maps); the view is sampled in the layout its source reports.</summary>
    DepthToGray = 4,

    /// <summary>Choose from the format: <see cref="EncodeSrgb"/> for sRGB and float formats, plus <see cref="Premultiply"/>.</summary>
    Auto = 1 << 8,
}

/// <summary>Per-frame counters of the UI renderer (no allocation to read).</summary>
public readonly record struct UiRenderStats(
    int Commands, int DrawCalls, int RenderPasses, int Geometry, int Textures, int GeometryChunks, int Layers);

/// <summary>
/// RmlUi's render interface on the engine's Vulkan device (docs/design/game-ui.md). RmlUi's callbacks, issued by
/// <see cref="RmlContext.Render"/> in the UI server's frame step, are recorded into a CPU command list; the GPU work is
/// recorded later, inside <see cref="IVulkanContext.BeginOverlayPass"/>: the UI renders into an offscreen premultiplied
/// sRGB layer (with a stencil buffer for clip masks and further layers for filters), which is composited onto the
/// swapchain after tonemapping and below the dev overlay — so it looks exactly as authored, unaffected by exposure or ACES.
/// </summary>
/// <remarks>
/// <para>Supports RmlUi 6.3's full interface: compiled geometry (a retained, sub-allocated arena), textures (images via
/// the UI file interface, premultiplied on load; generated font atlases; <c>engine://</c> textures and render
/// targets), scissor, transforms, clip masks (stencil), layers with filters (opacity, blur, drop-shadow, colour
/// matrices, mask-image) and gradient shaders. The effects port RmlUi's GL3 backend.</para>
/// <para>Allocation-free per steady-state frame: commands are structs in grow-only arrays, callbacks dispatch through
/// function pointers. Releases are deferred until the frames that may use them have completed.</para>
/// </remarks>
public sealed unsafe partial class VulkanUiRenderer : RmlRenderInterface, IOverlayRenderer
{
    private readonly IVulkanContext _ctx;
    private readonly Func<string, Stream?> _openFile;
    private readonly UiGeometryArena _arena;

    public VulkanUiRenderer(IVulkanContext ctx, Func<string, Stream?> openFile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(openFile);
        _ctx = ctx;
        _openFile = openFile;
        _arena = new UiGeometryArena(ctx);
        CreateResources();
        ctx.AddOverlayRenderer(this, OverlayOrder.Ui);
    }

    /// <summary>When false nothing is composited (the command list is still consumed).</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Counters of the last replayed frame.</summary>
    public UiRenderStats Stats { get; private set; }

    // ── Recording state ──────────────────────────────────────────────────────────────────────────────────────

    private enum UiOp : byte
    {
        Geometry,
        Shader,
        ClipMask,
        PushLayer,
        PopLayer,
        Composite,
        SaveTexture,
        SaveMask,
    }

    private struct UiCommand
    {
        public UiOp Op;
        public bool Stencil;          // clip mask test active
        public bool ScissorEnabled;
        public byte StencilRef;
        public RmlClipMaskOperation ClipOp;
        public RmlBlendMode Blend;
        public int ScissorX, ScissorY, ScissorW, ScissorH;
        public int Transform;         // index into _transforms, -1 = identity
        public Vector2 Translation;
        public VkBuffer Buffer;
        public uint FirstIndex;
        public int VertexOffset;
        public uint IndexCount;
        public DescriptorSet Set;
        public uint TextureFlags;
        public int A, B, C;           // op-specific: layers, filter range, shader / texture slot
    }

    private UiCommand[] _commands = new UiCommand[256];
    private int _commandCount;
    private Matrix4x4[] _transforms = new Matrix4x4[16];
    private int _transformCount;
    private int _currentTransform = -1;
    private bool _scissorEnabled;
    private int _scissorX, _scissorY, _scissorW, _scissorH;
    private bool _clipMaskEnabled;
    private byte _stencilRef = 1;
    private int _layerTop;              // handle of the top layer (0 = base)
    private int _maxLayerDepth;         // layers needed by the recorded list
    private UiFilter[] _filterData = new UiFilter[16];      // filters of recorded composites, copied at record time
    private int _filterDataCount;
    private GradientUbo[] _gradientData = new GradientUbo[16]; // gradients of recorded shader draws
    private int _shaderDraws;
    private bool _listConsumed = true;
    private bool _listHasSavedTargets;

    /// <summary>
    /// Starts a new frame's command list; called by the UI server before rendering its contexts. If the previous list
    /// was never replayed (a skipped frame) and it produced saved layer textures, those textures have no content, so
    /// RmlUi is asked to regenerate its textures.
    /// </summary>
    public void BeginFrame()
    {
        if (!_listConsumed && _listHasSavedTargets && RmlCore.IsInitialised)
        {
            Log.Debug("[UI] A frame with saved UI layers was skipped; regenerating UI textures.");
            RmlCore.ReleaseTextures(this);
        }

        _commandCount = 0;
        _transformCount = 0;
        _filterDataCount = 0;
        _shaderDraws = 0;
        _maxLayerDepth = 0;
        _listConsumed = false;
        _listHasSavedTargets = false;
        ResetState();
    }

    private void ResetState()
    {
        _currentTransform = -1;
        _scissorEnabled = false;
        _clipMaskEnabled = false;
        _stencilRef = 1;
        _layerTop = 0;
    }

    /// <summary>Frame number a release must wait for (the frame being recorded, or the next one between frames).</summary>
    private ulong ReleaseFrame => _ctx.Deletions.IsRecording ? _ctx.Deletions.CurrentFrame : _ctx.Deletions.CurrentFrame + 1;

    private ref UiCommand Add(UiOp op)
    {
        if (_commandCount == _commands.Length)
            Array.Resize(ref _commands, _commands.Length * 2);
        ref var c = ref _commands[_commandCount++];
        c = default;
        c.Op = op;
        c.Stencil = _clipMaskEnabled;
        c.StencilRef = _stencilRef;
        c.ScissorEnabled = _scissorEnabled;
        c.ScissorX = _scissorX;
        c.ScissorY = _scissorY;
        c.ScissorW = _scissorW;
        c.ScissorH = _scissorH;
        c.Transform = _currentTransform;
        return ref c;
    }

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────────────

    private struct GeometrySlot
    {
        public uint Generation;
        public bool Live;
        public UiGeometryRange Range;
    }

    private GeometrySlot[] _geometry = new GeometrySlot[256];
    private int _geometryCount;      // slots ever used
    private readonly Stack<int> _freeGeometry = new(64);
    private int _liveGeometry;

    private static ulong MakeHandle(int index, uint generation) => ((ulong)generation << 32) | (uint)(index + 1);

    private static int HandleIndex(ulong handle) => (int)(uint)handle - 1;

    private static uint HandleGeneration(ulong handle) => (uint)(handle >> 32);

    protected override ulong CompileGeometry(ReadOnlySpan<RmlVertex> vertices, ReadOnlySpan<int> indices)
    {
        var range = _arena.Allocate(vertices, indices);
        int index;
        if (_freeGeometry.Count > 0)
        {
            index = _freeGeometry.Pop();
        }
        else
        {
            if (_geometryCount == _geometry.Length)
                Array.Resize(ref _geometry, _geometry.Length * 2);
            index = _geometryCount++;
        }

        ref var slot = ref _geometry[index];
        slot.Generation++;
        slot.Live = true;
        slot.Range = range;
        _liveGeometry++;
        return MakeHandle(index, slot.Generation);
    }

    private ref GeometrySlot ResolveGeometry(ulong handle, out bool ok)
    {
        var index = HandleIndex(handle);
        if ((uint)index < (uint)_geometryCount)
        {
            ref var slot = ref _geometry[index];
            if (slot.Live && slot.Generation == HandleGeneration(handle))
            {
                ok = true;
                return ref slot;
            }
        }

        ok = false;
        return ref Unsafe.NullRef<GeometrySlot>();
    }

    protected override void ReleaseGeometry(ulong geometry)
    {
        ref var slot = ref ResolveGeometry(geometry, out var ok);
        if (!ok)
            return;
        _arena.Release(slot.Range, ReleaseFrame);
        slot.Live = false;
        slot.Generation++;
        _liveGeometry--;
        _freeGeometry.Push(HandleIndex(geometry));
    }

    protected override void RenderGeometry(ulong geometry, Vector2 translation, ulong texture)
    {
        ref var slot = ref ResolveGeometry(geometry, out var ok);
        if (!ok)
            return;
        if (!ResolveTexture(texture, out var set, out var flags))
            return;

        ref var c = ref Add(UiOp.Geometry);
        SetGeometry(ref c, slot.Range, translation);
        c.Set = set;
        c.TextureFlags = flags;
    }

    private static void SetGeometry(ref UiCommand c, in UiGeometryRange range, Vector2 translation)
    {
        c.Buffer = range.Buffer;
        c.FirstIndex = range.FirstIndex;
        c.VertexOffset = range.VertexOffset;
        c.IndexCount = range.IndexCount;
        c.Translation = translation;
    }

    // ── Scissor, transform, clip mask ────────────────────────────────────────────────────────────────────────

    protected override void EnableScissorRegion(bool enable) => _scissorEnabled = enable;

    protected override void SetScissorRegion(int x, int y, int width, int height)
    {
        _scissorEnabled = true;
        _scissorX = x;
        _scissorY = y;
        _scissorW = Math.Max(0, width);
        _scissorH = Math.Max(0, height);
    }

    protected override void SetTransform(in Matrix4x4? transform)
    {
        if (transform is not { } m)
        {
            _currentTransform = -1;
            return;
        }

        if (_transformCount == _transforms.Length)
            Array.Resize(ref _transforms, _transforms.Length * 2);
        _transforms[_transformCount] = m;
        _currentTransform = _transformCount++;
    }

    protected override void EnableClipMask(bool enable) => _clipMaskEnabled = enable;

    protected override void RenderToClipMask(RmlClipMaskOperation operation, ulong geometry, Vector2 translation)
    {
        ref var slot = ref ResolveGeometry(geometry, out var ok);
        if (!ok)
            return;

        // Same stencil scheme as RmlUi's GL3 backend: Set clears to 0 and writes 1, SetInverse clears to 1 and writes 0,
        // Intersect increments; geometry is then drawn where stencil == the test value.
        var testValue = operation == RmlClipMaskOperation.Intersect ? (byte)Math.Min(255, _stencilRef + 1) : (byte)1;
        ref var c = ref Add(UiOp.ClipMask);
        SetGeometry(ref c, slot.Range, translation);
        c.ClipOp = operation;
        c.StencilRef = operation == RmlClipMaskOperation.SetInverse ? (byte)0 : (byte)1; // write value (REPLACE ops)
        _stencilRef = testValue;
    }

    // ── Layers and filters ───────────────────────────────────────────────────────────────────────────────────

    protected override ulong PushLayer()
    {
        ref var c = ref Add(UiOp.PushLayer);
        _layerTop++;
        c.A = _layerTop;
        _maxLayerDepth = Math.Max(_maxLayerDepth, _layerTop);
        return (ulong)_layerTop;
    }

    protected override void PopLayer()
    {
        if (_layerTop == 0)
            return;
        ref var c = ref Add(UiOp.PopLayer);
        _layerTop--;
        c.A = _layerTop;
    }

    protected override void CompositeLayers(ulong source, ulong destination, RmlBlendMode blendMode, ReadOnlySpan<ulong> filters)
    {
        ref var c = ref Add(UiOp.Composite);
        c.A = (int)source;
        c.B = (int)destination;
        c.Blend = blendMode;
        c.C = _filterDataCount;
        foreach (var handle in filters)
        {
            if (!TryGetFilter(handle, out var filter))
                continue;
            if (_filterDataCount == _filterData.Length)
                Array.Resize(ref _filterData, _filterData.Length * 2);
            _filterData[_filterDataCount++] = filter;
        }

        c.IndexCount = (uint)(_filterDataCount - c.C);
    }

    protected override ulong SaveLayerAsTexture()
    {
        if (!_scissorEnabled || _scissorW <= 0 || _scissorH <= 0)
            return 0;
        var handle = CreateSavedTexture(_scissorW, _scissorH);
        if (handle == 0)
            return 0;
        ref var c = ref Add(UiOp.SaveTexture);
        c.A = HandleIndex(handle);
        c.B = _layerTop;
        c.IndexCount = HandleGeneration(handle);
        _listHasSavedTargets = true;
        return handle;
    }

    protected override ulong SaveLayerAsMaskImage()
    {
        ref var c = ref Add(UiOp.SaveMask);
        c.B = _layerTop;
        _listHasSavedTargets = true;
        return CreateFilter(new UiFilter { Kind = UiFilterKind.MaskImage });
    }

    // ── Shaders ──────────────────────────────────────────────────────────────────────────────────────────────

    protected override void RenderShader(ulong shader, ulong geometry, Vector2 translation, ulong texture)
    {
        ref var slot = ref ResolveGeometry(geometry, out var ok);
        if (!ok)
            return;
        var shaderIndex = HandleIndex(shader);
        if ((uint)shaderIndex >= (uint)_shaderCount || !_shaders[shaderIndex].Live || _shaders[shaderIndex].Generation != HandleGeneration(shader))
            return;

        ref var c = ref Add(UiOp.Shader);
        SetGeometry(ref c, slot.Range, translation);
        if (_shaderDraws == _gradientData.Length)
            Array.Resize(ref _gradientData, _gradientData.Length * 2);
        _gradientData[_shaderDraws] = _shaders[shaderIndex].Gradient;
        c.C = _shaderDraws++;
    }

    // ── Images ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The URL scheme for engine textures and render targets (<see cref="RegisterTexture(string, GpuTexture, UiTextureConversion)"/>).</summary>
    public const string EngineScheme = "engine://";

    protected override ulong LoadTexture(string source, out int width, out int height)
    {
        width = height = 0;
        if (source.StartsWith(EngineScheme, StringComparison.Ordinal))
            return LoadEngineTexture(source[EngineScheme.Length..], out width, out height);

        using var stream = _openFile(source);
        if (stream is null)
        {
            Log.Warning($"[UI] Image '{source}' not found.");
            return 0;
        }

        ImageResult image;
        try
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            Log.Warning($"[UI] Image '{source}' could not be decoded: {e.Message}");
            return 0;
        }

        Premultiply(image.Data);
        width = image.Width;
        height = image.Height;
        var texture = GpuTexture.Create2D(_ctx, (uint)image.Width, (uint)image.Height, image.Data, TextureColorSpace.Linear,
            TextureSampling.LinearClamp, generateMips: true);
        return AddTexture(texture, width, height, 0);
    }

    /// <summary>Premultiplies straight-alpha RGBA8 in place (in sRGB space, as browsers and RmlUi's backends do).</summary>
    internal static void Premultiply(Span<byte> rgba)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            var a = rgba[i + 3];
            if (a == 255)
                continue;
            rgba[i] = (byte)((rgba[i] * a + 127) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * a + 127) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * a + 127) / 255);
        }
    }

    protected override ulong GenerateTexture(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var texture = GpuTexture.Create2D(_ctx, (uint)width, (uint)height, rgba, TextureColorSpace.Linear, TextureSampling.LinearClamp);
        return AddTexture(texture, width, height, 0);
    }

    protected override void ReleaseTexture(ulong texture) => FreeTexture(texture);
}
