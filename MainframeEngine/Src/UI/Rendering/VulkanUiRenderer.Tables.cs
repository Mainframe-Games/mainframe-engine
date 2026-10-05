using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MainframeEngine.UI.Rml;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public sealed unsafe partial class VulkanUiRenderer
{
    // ── Textures ─────────────────────────────────────────────────────────────────────────────────────────────

    private struct TextureSlot
    {
        public uint Generation;
        public bool Live;
        public GpuTexture? Texture;      // loaded image or generated (font atlas)
        public GpuImage? SavedImage;     // SaveLayerAsTexture target
        public Framebuffer SavedFramebuffer;
        public UiEngineTexture? Engine;  // engine://
        public ImageView BoundView;      // the view Set points at (engine textures follow resizes)
        public ImageLayout BoundLayout;  // the layout the view is sampled in (depth maps: DepthStencilReadOnlyOptimal)
        public ulong BoundGeneration;    // UiTextureView.Generation Set was written for
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public uint Flags;
        public int Width, Height;
    }

    private TextureSlot[] _textures = new TextureSlot[64];
    private int _textureCount;
    private readonly Stack<int> _freeTextures = new(16);
    private int _liveTextures;

    private ulong AddTexture(GpuTexture texture, int width, int height, uint flags)
    {
        var index = NewTextureSlot();
        ref var slot = ref _textures[index];
        slot.Texture = texture;
        slot.Width = width;
        slot.Height = height;
        slot.Flags = flags;
        (slot.Set, slot.Pool) = AllocateTextureSet(texture.View, texture.Sampler, ImageLayout.ShaderReadOnlyOptimal);
        slot.BoundView = texture.View;
        slot.BoundLayout = ImageLayout.ShaderReadOnlyOptimal;
        return MakeHandle(index, slot.Generation);
    }

    private int NewTextureSlot()
    {
        int index;
        if (_freeTextures.Count > 0)
        {
            index = _freeTextures.Pop();
        }
        else
        {
            if (_textureCount == _textures.Length)
                Array.Resize(ref _textures, _textures.Length * 2);
            index = _textureCount++;
        }

        ref var slot = ref _textures[index];
        var generation = slot.Generation + 1;
        slot = default;
        slot.Generation = generation;
        slot.Live = true;
        _liveTextures++;
        return index;
    }

    private ref TextureSlot ResolveTextureSlot(ulong handle, out bool ok)
    {
        var index = HandleIndex(handle);
        if ((uint)index < (uint)_textureCount)
        {
            ref var slot = ref _textures[index];
            if (slot.Live && slot.Generation == HandleGeneration(handle))
            {
                ok = true;
                return ref slot;
            }
        }

        ok = false;
        return ref Unsafe.NullRef<TextureSlot>();
    }

    /// <summary>The descriptor set and shader flags for a texture handle (0 = the white texture). False: skip the draw.</summary>
    private bool ResolveTexture(ulong handle, out DescriptorSet set, out uint flags)
    {
        set = _whiteSet;
        flags = 0;
        if (handle == 0)
            return true;

        ref var slot = ref ResolveTextureSlot(handle, out var ok);
        if (!ok)
            return false;

        if (slot.Engine is { } engine)
        {
            // Engine textures can be replaced (render target resize) or unregistered while RmlUi holds the handle.
            if (!engine.IsRegistered)
                return false;
            if (!engine.TryGetImage(out var image))
            {
                // Nothing to show: drop the set so it never outlives the view it points at (the image may be destroyed
                // and a new one created with the same handle value); the next valid view allocates a fresh set.
                FreeTextureSet(slot.Set, slot.Pool);
                slot.Set = default;
                slot.Pool = default;
                slot.BoundView = default;
                slot.BoundLayout = default;
                slot.BoundGeneration = 0;
                return false;
            }

            if (image.NeedsRebind(slot.BoundView, slot.BoundLayout, slot.BoundGeneration))
            {
                FreeTextureSet(slot.Set, slot.Pool);
                (slot.Set, slot.Pool) = AllocateTextureSet(image.View, image.Sampler, image.Layout);
                slot.BoundView = image.View;
                slot.BoundLayout = image.Layout;
                slot.BoundGeneration = image.Generation;
            }
        }

        set = slot.Set;
        flags = slot.Flags;
        return true;
    }

    private void FreeTexture(ulong handle)
    {
        ref var slot = ref ResolveTextureSlot(handle, out var ok);
        if (!ok)
            return;

        FreeTextureSet(slot.Set, slot.Pool);
        slot.Texture?.Dispose(); // deletion queue: after the frames that may sample it
        slot.SavedImage?.Dispose();
        if (slot.SavedFramebuffer.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(slot.SavedFramebuffer));
        var generation = slot.Generation + 1;
        slot = default;
        slot.Generation = generation;
        _liveTextures--;
        _freeTextures.Push(HandleIndex(handle));
    }

    /// <summary>A texture to render a saved layer region into (content written when the command list is replayed).</summary>
    private ulong CreateSavedTexture(int width, int height)
    {
        var image = GpuImage.Create(_ctx, new GpuImageDesc((uint)width, (uint)height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit));
        var index = NewTextureSlot();
        ref var slot = ref _textures[index];
        slot.SavedImage = image;
        slot.SavedFramebuffer = CreateFramebuffer(_postPassDiscard, image.View, default, (uint)width, (uint)height, "UI saved layer");
        slot.Width = width;
        slot.Height = height;
        (slot.Set, slot.Pool) = AllocateTextureSet(image.View, _linearSampler, ImageLayout.ShaderReadOnlyOptimal);
        slot.BoundView = image.View;
        slot.BoundLayout = ImageLayout.ShaderReadOnlyOptimal;
        return MakeHandle(index, slot.Generation);
    }

    // ── Engine textures (engine://name) ──────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, UiEngineTexture> _engineTextures = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes <paramref name="texture"/> available to documents as <c>engine://<paramref name="name"/></c>
    /// (<c>&lt;img src="engine://minimap"/&gt;</c>, <c>decorator: image(engine://avatar)</c>). Replacing a name updates
    /// documents that already use it.
    /// </summary>
    public void RegisterTexture(string name, GpuTexture texture, UiTextureConversion flags = UiTextureConversion.Auto)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Register(name, new UiEngineTexture(texture, Resolve(flags, texture.Image.Format)));
    }

    /// <summary>
    /// Makes colour attachment <paramref name="colorAttachment"/> of a render target (rendered earlier in the frame,
    /// final layout <c>SHADER_READ_ONLY_OPTIMAL</c>) available as <c>engine://<paramref name="name"/></c>; follows
    /// <see cref="RenderTarget.Resize"/>. HDR targets are encoded to sRGB but not tonemapped.
    /// </summary>
    public void RegisterTexture(string name, RenderTarget target, int colorAttachment = 0, UiTextureConversion flags = UiTextureConversion.Auto)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(colorAttachment);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(colorAttachment, target.ColorCount);
        Register(name, new UiEngineTexture(target, colorAttachment, _linearSampler,
            Resolve(flags, target.Description.ColorAttachments[colorAttachment].Format)));
    }

    /// <summary>
    /// Publishes an image whose view may change or disappear (shadow maps) as <c>engine://name</c>. The source is asked
    /// every frame the image is drawn; a view with a zero handle means "nothing to show" and nothing is drawn. The
    /// image must be in the layout the source reports when the UI renders.
    /// </summary>
    internal void RegisterTexture(string name, Func<UiTextureView> source, UiTextureConversion flags) =>
        Register(name, new UiEngineTexture(source, (uint)(flags & ~UiTextureConversion.Auto)));

    /// <summary>
    /// Removes an engine texture; documents using it stop drawing it. RmlUi caches textures by source, so its textures
    /// are released too: a texture registered later under the same name is loaded again instead of the cached one.
    /// </summary>
    public bool UnregisterTexture(string name)
    {
        if (!_engineTextures.Remove(name, out var existing))
            return false;
        existing.IsRegistered = false;
        if (RmlCore.IsInitialised)
            RmlCore.ReleaseTextures(this);
        return true;
    }

    private void Register(string name, UiEngineTexture texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_engineTextures.Remove(name, out var previous))
            previous.IsRegistered = false;
        _engineTextures.Add(name, texture);
        if (RmlCore.IsInitialised && previous is not null)
            RmlCore.ReleaseTextures(this); // documents re-load the name and pick up the new texture
    }

    private static uint Resolve(UiTextureConversion flags, Format format)
    {
        if ((flags & UiTextureConversion.Auto) == 0)
            return (uint)flags;
        var encode = FormatInfo.IsSrgb(format) || format is Format.R16G16B16A16Sfloat or Format.R32G32B32A32Sfloat
            or Format.B10G11R11UfloatPack32 or Format.R16G16Sfloat;
        return (uint)((encode ? UiTextureConversion.EncodeSrgb : 0) | UiTextureConversion.Premultiply);
    }

    private ulong LoadEngineTexture(string name, out int width, out int height)
    {
        width = height = 0;
        if (!_engineTextures.TryGetValue(name, out var engine))
        {
            Log.Warning($"[UI] Engine texture '{EngineScheme}{name}' is not registered.");
            return 0;
        }

        var hasView = engine.TryGetImage(out var image);
        if (!hasView && !engine.IsSource)
        {
            Log.Warning($"[UI] Engine texture '{EngineScheme}{name}' is not registered.");
            return 0;
        }

        (width, height) = ((int)image.Width, (int)image.Height);
        var index = NewTextureSlot();
        ref var slot = ref _textures[index];
        slot.Engine = engine;
        slot.Width = width;
        slot.Height = height;
        slot.Flags = engine.Flags;
        if (hasView)
        {
            (slot.Set, slot.Pool) = AllocateTextureSet(image.View, image.Sampler, image.Layout);
            slot.BoundView = image.View;
            slot.BoundLayout = image.Layout;
            slot.BoundGeneration = image.Generation;
        }

        // else: a source without an image yet (a normal state, e.g. shadow maps before the first shadow pass): the
        // slot has no set and ResolveTexture binds one when the view appears.
        return MakeHandle(index, slot.Generation);
    }

    // ── Filters ──────────────────────────────────────────────────────────────────────────────────────────────

    private enum UiFilterKind : byte
    {
        Invalid,
        Opacity,
        Blur,
        DropShadow,
        ColorMatrix,
        MaskImage,
    }

    private struct UiFilter
    {
        public uint Generation;
        public bool Live;
        public UiFilterKind Kind;
        public float Value;         // opacity
        public float Sigma;         // blur, drop-shadow
        public Vector2 Offset;      // drop-shadow
        public Vector4 Color;       // drop-shadow, premultiplied 0..1
        public Matrix4x4 Matrix;    // colour matrix (math layout: rows are output channels)
    }

    private UiFilter[] _filters = new UiFilter[16];
    private int _filterCount;
    private readonly Stack<int> _freeFilters = new(8);

    private ulong CreateFilter(in UiFilter filter)
    {
        int index;
        if (_freeFilters.Count > 0)
        {
            index = _freeFilters.Pop();
        }
        else
        {
            if (_filterCount == _filters.Length)
                Array.Resize(ref _filters, _filters.Length * 2);
            index = _filterCount++;
        }

        var generation = _filters[index].Generation + 1;
        _filters[index] = filter;
        _filters[index].Generation = generation;
        _filters[index].Live = true;
        return MakeHandle(index, generation);
    }

    private bool TryGetFilter(ulong handle, out UiFilter filter)
    {
        var index = HandleIndex(handle);
        if ((uint)index < (uint)_filterCount && _filters[index].Live && _filters[index].Generation == HandleGeneration(handle))
        {
            filter = _filters[index];
            return true;
        }

        filter = default;
        return false;
    }

    protected override ulong CompileFilter(RmlFilterKind kind, ReadOnlySpan<byte> name, RmlDictionary parameters)
    {
        var f = new UiFilter();
        var value = Number(parameters, "value"u8, 1f);
        switch (kind)
        {
            case RmlFilterKind.Opacity:
                f.Kind = UiFilterKind.Opacity;
                f.Value = value;
                break;
            case RmlFilterKind.Blur:
                f.Kind = UiFilterKind.Blur;
                f.Sigma = Number(parameters, "sigma"u8, 1f);
                break;
            case RmlFilterKind.DropShadow:
                f.Kind = UiFilterKind.DropShadow;
                f.Sigma = Number(parameters, "sigma"u8, 0f);
                var color = parameters.FindUtf8("color"u8);
                var rgba = color.IsNull ? 0u : color.GetColourb();
                f.Color = PremultipliedColor(rgba);
                var offset = parameters.FindUtf8("offset"u8);
                var o = offset.IsNull ? Vector4.Zero : offset.GetVector4();
                f.Offset = new Vector2(o.X, o.Y);
                break;
            case RmlFilterKind.Brightness:
                f.Kind = UiFilterKind.ColorMatrix;
                f.Matrix = Matrix4x4.CreateScale(value, value, value);
                break;
            case RmlFilterKind.Contrast:
                {
                    f.Kind = UiFilterKind.ColorMatrix;
                    var grayness = 0.5f - 0.5f * value;
                    f.Matrix = Matrix4x4.CreateScale(value, value, value);
                    f.Matrix.M14 = grayness; // column 3 = constant term (scaled by alpha in the shader)
                    f.Matrix.M24 = grayness;
                    f.Matrix.M34 = grayness;
                    break;
                }
            case RmlFilterKind.Invert:
                {
                    f.Kind = UiFilterKind.ColorMatrix;
                    var v = Math.Clamp(value, 0f, 1f);
                    var inverted = 1f - 2f * v;
                    f.Matrix = Matrix4x4.CreateScale(inverted, inverted, inverted);
                    f.Matrix.M14 = v;
                    f.Matrix.M24 = v;
                    f.Matrix.M34 = v;
                    break;
                }
            case RmlFilterKind.Grayscale:
                {
                    f.Kind = UiFilterKind.ColorMatrix;
                    var rev = 1f - value;
                    var g = value * new Vector3(0.2126f, 0.7152f, 0.0722f);
                    f.Matrix = Rows(new Vector4(g.X + rev, g.Y, g.Z, 0), new Vector4(g.X, g.Y + rev, g.Z, 0),
                        new Vector4(g.X, g.Y, g.Z + rev, 0));
                    break;
                }
            case RmlFilterKind.Sepia:
                {
                    f.Kind = UiFilterKind.ColorMatrix;
                    var rev = 1f - value;
                    var r = value * new Vector3(0.393f, 0.769f, 0.189f);
                    var g = value * new Vector3(0.349f, 0.686f, 0.168f);
                    var b = value * new Vector3(0.272f, 0.534f, 0.131f);
                    f.Matrix = Rows(new Vector4(r.X + rev, r.Y, r.Z, 0), new Vector4(g.X, g.Y + rev, g.Z, 0),
                        new Vector4(b.X, b.Y, b.Z + rev, 0));
                    break;
                }
            case RmlFilterKind.HueRotate:
                {
                    // https://www.w3.org/TR/filter-effects-1/#attr-valuedef-type-huerotate (value in radians)
                    f.Kind = UiFilterKind.ColorMatrix;
                    var s = MathF.Sin(value);
                    var c = MathF.Cos(value);
                    f.Matrix = Rows(
                        new Vector4(0.213f + 0.787f * c - 0.213f * s, 0.715f - 0.715f * c - 0.715f * s, 0.072f - 0.072f * c + 0.928f * s, 0),
                        new Vector4(0.213f - 0.213f * c + 0.143f * s, 0.715f + 0.285f * c + 0.140f * s, 0.072f - 0.072f * c - 0.283f * s, 0),
                        new Vector4(0.213f - 0.213f * c - 0.787f * s, 0.715f - 0.715f * c + 0.715f * s, 0.072f + 0.928f * c + 0.072f * s, 0));
                    break;
                }
            case RmlFilterKind.Saturate:
                f.Kind = UiFilterKind.ColorMatrix;
                f.Matrix = Rows(
                    new Vector4(0.213f + 0.787f * value, 0.715f - 0.715f * value, 0.072f - 0.072f * value, 0),
                    new Vector4(0.213f - 0.213f * value, 0.715f + 0.285f * value, 0.072f - 0.072f * value, 0),
                    new Vector4(0.213f - 0.213f * value, 0.715f - 0.715f * value, 0.072f + 0.928f * value, 0));
                break;
            default:
                Log.Warning($"[UI] Unsupported filter '{RmlCore.Decode(name)}'.");
                return 0;
        }

        return CreateFilter(f);
    }

    /// <summary>A colour matrix from three RGB rows (the alpha row is identity).</summary>
    private static Matrix4x4 Rows(Vector4 r, Vector4 g, Vector4 b) =>
        new(r.X, r.Y, r.Z, r.W, g.X, g.Y, g.Z, g.W, b.X, b.Y, b.Z, b.W, 0, 0, 0, 1);

    private static float Number(RmlDictionary parameters, ReadOnlySpan<byte> key, float fallback)
    {
        var v = parameters.FindUtf8(key);
        return v.IsNull ? fallback : v.GetSingle(fallback);
    }

    /// <summary>Straight RGBA8 (packed R | G&lt;&lt;8 | B&lt;&lt;16 | A&lt;&lt;24) to premultiplied floats.</summary>
    internal static Vector4 PremultipliedColor(uint rgba)
    {
        var a = ((rgba >> 24) & 0xFF) / 255f;
        return new Vector4((rgba & 0xFF) / 255f * a, ((rgba >> 8) & 0xFF) / 255f * a, ((rgba >> 16) & 0xFF) / 255f * a, a);
    }

    protected override void ReleaseFilter(ulong filter)
    {
        var index = HandleIndex(filter);
        if ((uint)index >= (uint)_filterCount || !_filters[index].Live || _filters[index].Generation != HandleGeneration(filter))
            return;
        _filters[index].Live = false;
        _filters[index].Generation++;
        _freeFilters.Push(index);
    }

    // ── Shaders (gradients) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Must match the defines in UiGradient.vk.frag.</summary>
    private enum GradientFunction
    {
        Linear = 0,
        Radial = 1,
        Conic = 2,
        RepeatingLinear = 3,
        RepeatingRadial = 4,
        RepeatingConic = 5,
    }

    internal const int MaxGradientStops = 16;

    [InlineArray(MaxGradientStops)]
    private struct StopColors
    {
        private Vector4 _element;
    }

    [InlineArray(MaxGradientStops)]
    private struct StopPositions
    {
        private float _element;
    }

    /// <summary>std140 layout of the gradient uniform block (352 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = GradientUboSize)]
    private struct GradientUbo
    {
        [FieldOffset(0)] public int Function;
        [FieldOffset(4)] public int StopCount;
        [FieldOffset(8)] public Vector2 P;
        [FieldOffset(16)] public Vector2 V;
        [FieldOffset(32)] public StopColors Colors;
        [FieldOffset(288)] public StopPositions Positions; // vec4[4] in std140 = 16 tightly packed floats
    }

    internal const int GradientUboSize = 352;

    private struct ShaderSlot
    {
        public uint Generation;
        public bool Live;
        public GradientUbo Gradient;
    }

    private ShaderSlot[] _shaders = new ShaderSlot[16];
    private int _shaderCount;
    private readonly Stack<int> _freeShaders = new(8);

    protected override ulong CompileShader(RmlShaderKind kind, ReadOnlySpan<byte> name, RmlDictionary parameters)
    {
        var g = new GradientUbo();
        var repeating = parameters.FindUtf8("repeating"u8) is { IsNull: false } r && r.GetBool();
        switch (kind)
        {
            case RmlShaderKind.LinearGradient:
                {
                    g.Function = (int)(repeating ? GradientFunction.RepeatingLinear : GradientFunction.Linear);
                    var p0 = Vec2(parameters, "p0"u8, Vector2.Zero);
                    g.P = p0;
                    g.V = Vec2(parameters, "p1"u8, Vector2.Zero) - p0;
                    break;
                }
            case RmlShaderKind.RadialGradient:
                g.Function = (int)(repeating ? GradientFunction.RepeatingRadial : GradientFunction.Radial);
                g.P = Vec2(parameters, "center"u8, Vector2.Zero);
                g.V = Vector2.One / Vec2(parameters, "radius"u8, Vector2.One);
                break;
            case RmlShaderKind.ConicGradient:
                {
                    g.Function = (int)(repeating ? GradientFunction.RepeatingConic : GradientFunction.Conic);
                    g.P = Vec2(parameters, "center"u8, Vector2.Zero);
                    var angle = Number(parameters, "angle"u8, 0f);
                    g.V = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    break;
                }
            default:
                Log.Warning($"[UI] Unsupported shader '{RmlCore.Decode(name)}'.");
                return 0;
        }

        var list = parameters.FindUtf8("color_stop_list"u8);
        Span<RmlColorStop> stops = stackalloc RmlColorStop[MaxGradientStops];
        var count = list.IsNull ? 0 : Math.Min(list.GetColorStops(stops), MaxGradientStops);
        if (count == 0)
            return 0;
        g.StopCount = count;
        for (var i = 0; i < count; i++)
        {
            var c = stops[i].ColorRgba; // already premultiplied
            g.Colors[i] = new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);
            g.Positions[i] = stops[i].Position;
        }

        int index;
        if (_freeShaders.Count > 0)
        {
            index = _freeShaders.Pop();
        }
        else
        {
            if (_shaderCount == _shaders.Length)
                Array.Resize(ref _shaders, _shaders.Length * 2);
            index = _shaderCount++;
        }

        ref var slot = ref _shaders[index];
        slot.Generation++;
        slot.Live = true;
        slot.Gradient = g;
        return MakeHandle(index, slot.Generation);
    }

    private static Vector2 Vec2(RmlDictionary parameters, ReadOnlySpan<byte> key, Vector2 fallback)
    {
        var v = parameters.FindUtf8(key);
        if (v.IsNull)
            return fallback;
        var f = v.GetVector4(new Vector4(fallback, 0, 0));
        return new Vector2(f.X, f.Y);
    }

    protected override void ReleaseShader(ulong shader)
    {
        var index = HandleIndex(shader);
        if ((uint)index >= (uint)_shaderCount || !_shaders[index].Live || _shaders[index].Generation != HandleGeneration(shader))
            return;
        _shaders[index].Live = false;
        _shaders[index].Generation++;
        _freeShaders.Push(index);
    }
}

/// <summary>
/// An image a <see cref="UiEngineTexture"/> source reports each time it is drawn. A zero <see cref="View"/> means there
/// is nothing to show. <see cref="Generation"/> identifies the image's incarnation: a source changes it whenever the
/// image is recreated (even if a new view reuses the old handle value, or no frame drew it in between), which makes the
/// UI write a fresh descriptor set instead of binding one that points at a destroyed view.
/// </summary>
internal readonly record struct UiTextureView(
    ImageView View, Sampler Sampler, ImageLayout Layout, uint Width, uint Height, ulong Generation = 0)
{
    /// <summary>Whether a descriptor set written for (<paramref name="boundView"/>, layout, generation) no longer matches.</summary>
    public bool NeedsRebind(ImageView boundView, ImageLayout boundLayout, ulong boundGeneration) =>
        View.Handle != boundView.Handle || Layout != boundLayout || Generation != boundGeneration;
}

/// <summary>An engine texture, render target or image source published to documents as <c>engine://name</c>.</summary>
internal sealed class UiEngineTexture
{
    private readonly GpuTexture? _texture;
    private readonly RenderTarget? _target;
    private readonly int _attachment;
    private readonly Sampler _targetSampler;
    private readonly Func<UiTextureView>? _source;

    public UiEngineTexture(GpuTexture texture, uint flags)
    {
        _texture = texture;
        Flags = flags;
    }

    public UiEngineTexture(RenderTarget target, int attachment, Sampler sampler, uint flags)
    {
        _target = target;
        _attachment = attachment;
        _targetSampler = sampler;
        Flags = flags;
    }

    public UiEngineTexture(Func<UiTextureView> source, uint flags)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        Flags = flags;
    }

    public uint Flags { get; }

    public bool IsRegistered { get; set; } = true;

    /// <summary>A source's image can be absent and appear later; textures and render targets always have one.</summary>
    public bool IsSource => _source is not null;

    /// <summary>The current image (view, sampler, layout, size, generation); false when there is nothing to show.</summary>
    public bool TryGetImage(out UiTextureView image)
    {
        if (_source is not null)
        {
            image = _source();
            return image.View.Handle != 0;
        }

        if (_texture is not null)
        {
            image = new UiTextureView(_texture.View, _texture.Sampler, ImageLayout.ShaderReadOnlyOptimal, _texture.Width, _texture.Height);
            return true;
        }

        var view = _target!.GetColor(_attachment).View;
        image = new UiTextureView(view, _targetSampler, ImageLayout.ShaderReadOnlyOptimal, _target.Extent.Width, _target.Extent.Height);
        return view.Handle != 0;
    }

    /// <summary>The image to sample, its sampler and the layout it is in; false when there is nothing to show.</summary>
    public bool TryGetView(out ImageView view, out Sampler sampler, out ImageLayout layout)
    {
        var found = TryGetImage(out var image);
        (view, sampler, layout) = (image.View, image.Sampler, image.Layout);
        return found;
    }
}
