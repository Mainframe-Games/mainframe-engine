using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine.UI.Rml;

/// <summary>How a clip-mask geometry combines with the current mask (<c>Rml::ClipMaskOperation</c>).</summary>
public enum RmlClipMaskOperation
{
    /// <summary>Replace the mask with the geometry.</summary>
    Set = 0,

    /// <summary>Replace the mask with everything outside the geometry.</summary>
    SetInverse = 1,

    /// <summary>Intersect the mask with the geometry.</summary>
    Intersect = 2,
}

/// <summary>How a layer is composited (<c>Rml::BlendMode</c>).</summary>
public enum RmlBlendMode
{
    Blend = 0,
    Replace = 1,
}

/// <summary>The filters RmlUi 6.3 compiles (<c>filter</c>, <c>backdrop-filter</c>, <c>mask-image</c>).</summary>
public enum RmlFilterKind
{
    Unknown,
    Opacity,
    Blur,
    DropShadow,
    Brightness,
    Contrast,
    Invert,
    Grayscale,
    Sepia,
    HueRotate,
    Saturate,
}

/// <summary>The shaders RmlUi 6.3 compiles (decorators).</summary>
public enum RmlShaderKind
{
    Unknown,
    LinearGradient,
    RadialGradient,
    ConicGradient,

    /// <summary>The <c>shader</c> decorator: a game-defined shader (by its "value" parameter).</summary>
    Custom,
}

/// <summary>
/// RmlUi's render interface, implemented in C#: the engine's <see cref="VulkanUiRenderer"/>, or a test double.
/// Handles (geometry, texture, layer, filter, shader) are non-zero <see cref="ulong"/>s chosen by the implementation;
/// 0 means failure / none. Coordinates are context pixels, origin top-left, y down; colours and textures are
/// premultiplied alpha. All methods run on the RmlUi thread during <see cref="RmlContext.Render"/> (and releases at
/// any RmlUi call); exceptions are caught and logged at the boundary.
/// </summary>
/// <remarks>
/// The first eight members are RmlUi's required "basic" set. The rest (transforms, clip masks, layers, filters,
/// shaders) have RmlUi's defaults — features are simply skipped — so a minimal renderer overrides only what it supports.
/// One render interface can serve many contexts (they then share textures, e.g. font atlases).
/// </remarks>
public abstract unsafe class RmlRenderInterface : IDisposable
{
    private readonly RmlRenderInterfaceHandle _handle = new();
    private GCHandle _self;

    /// <summary>The native interface (created on first use).</summary>
    internal nint Handle
    {
        get
        {
            if (_handle.IsInvalid && !_handle.IsClosed)
                Create();
            return _handle.Value;
        }
    }

    private void Create()
    {
        RmlCore.EnsureLibrary();
        _self = GCHandle.Alloc(this);
        var callbacks = new RmlNative.RenderCallbacks
        {
            StructSize = RmlNative.SizeOf<RmlNative.RenderCallbacks>(),
            UserData = GCHandle.ToIntPtr(_self),
            CompileGeometry = &CbCompileGeometry,
            RenderGeometry = &CbRenderGeometry,
            ReleaseGeometry = &CbReleaseGeometry,
            LoadTexture = &CbLoadTexture,
            GenerateTexture = &CbGenerateTexture,
            ReleaseTexture = &CbReleaseTexture,
            EnableScissorRegion = &CbEnableScissor,
            SetScissorRegion = &CbSetScissor,
            SetTransform = &CbSetTransform,
            EnableClipMask = &CbEnableClipMask,
            RenderToClipMask = &CbRenderToClipMask,
            PushLayer = &CbPushLayer,
            CompositeLayers = &CbCompositeLayers,
            PopLayer = &CbPopLayer,
            SaveLayerAsTexture = &CbSaveLayerAsTexture,
            SaveLayerAsMaskImage = &CbSaveLayerAsMaskImage,
            CompileFilter = &CbCompileFilter,
            ReleaseFilter = &CbReleaseFilter,
            CompileShader = &CbCompileShader,
            RenderShader = &CbRenderShader,
            ReleaseShader = &CbReleaseShader,
        };
        var native = RmlNative.RenderInterfaceCreate(&callbacks);
        if (native == 0)
        {
            _self.Free();
            throw new RmlException("mfrmlui_render_interface_create", RmlNative.ErrorFailed);
        }

        _handle.Attach(native);
    }

    /// <summary>True once disposed.</summary>
    public bool IsDisposed => _handle.IsClosed;

    // ── Required ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores geometry for repeated rendering. The spans stay valid and unchanged until
    /// <see cref="ReleaseGeometry"/> for the returned handle, but implementations normally copy them.
    /// </summary>
    protected abstract ulong CompileGeometry(ReadOnlySpan<RmlVertex> vertices, ReadOnlySpan<int> indices);

    /// <summary>Draws compiled geometry translated by <paramref name="translation"/>; <paramref name="texture"/> 0 = untextured.</summary>
    protected abstract void RenderGeometry(ulong geometry, Vector2 translation, ulong texture);

    protected abstract void ReleaseGeometry(ulong geometry);

    /// <summary>Loads an image (<paramref name="source"/> already joined with the document path); writes its size.</summary>
    protected abstract ulong LoadTexture(string source, out int width, out int height);

    /// <summary>A texture from premultiplied RGBA8 pixels (font atlases, generated images); the span lives for the call.</summary>
    protected abstract ulong GenerateTexture(ReadOnlySpan<byte> rgba, int width, int height);

    protected abstract void ReleaseTexture(ulong texture);

    /// <summary>
    /// Decodes and uploads the image RmlUi will ask for as <paramref name="source"/> (joined with the document path, as
    /// <see cref="LoadTexture"/> receives it) ahead of time, so its first layout does not decode on the main thread
    /// (ADR 0140). False when the interface does not preload, the source is already loaded or preloaded, or it cannot be
    /// read (the lazy load then reports it).
    /// </summary>
    public virtual bool PreloadTexture(string source) => false;

    /// <summary>Called by <see cref="RmlCore.ReleaseTextures"/> before RmlUi releases this interface's textures.</summary>
    protected internal virtual void OnReleasingTextures()
    {
    }

    protected abstract void EnableScissorRegion(bool enable);

    /// <summary>Scissor in context pixels, regardless of any transform.</summary>
    protected abstract void SetScissorRegion(int x, int y, int width, int height);

    // ── Optional (RmlUi's defaults: the feature is skipped) ──────────────────────────────────────────────────

    /// <summary>Transform for subsequent geometry (null = identity); column-major like RmlUi's <c>Matrix4f</c>.</summary>
    protected virtual void SetTransform(in Matrix4x4? transform)
    {
    }

    protected virtual void EnableClipMask(bool enable)
    {
    }

    protected virtual void RenderToClipMask(RmlClipMaskOperation operation, ulong geometry, Vector2 translation)
    {
    }

    /// <summary>Pushes a new transparent layer and returns its handle (0 is the base layer).</summary>
    protected virtual ulong PushLayer() => 0;

    /// <summary>Composites <paramref name="source"/> onto <paramref name="destination"/>, applying <paramref name="filters"/> in order.</summary>
    protected virtual void CompositeLayers(ulong source, ulong destination, RmlBlendMode blendMode, ReadOnlySpan<ulong> filters)
    {
    }

    protected virtual void PopLayer()
    {
    }

    /// <summary>Copies the top layer within the current scissor into a new texture.</summary>
    protected virtual ulong SaveLayerAsTexture() => 0;

    /// <summary>Turns the top layer into a mask filter (<c>mask-image</c>).</summary>
    protected virtual ulong SaveLayerAsMaskImage() => 0;

    /// <summary>Compiles a filter; read <paramref name="parameters"/> inside the call only.</summary>
    protected virtual ulong CompileFilter(RmlFilterKind kind, ReadOnlySpan<byte> name, RmlDictionary parameters) => 0;

    protected virtual void ReleaseFilter(ulong filter)
    {
    }

    /// <summary>Compiles a shader (gradients, custom); read <paramref name="parameters"/> inside the call only.</summary>
    protected virtual ulong CompileShader(RmlShaderKind kind, ReadOnlySpan<byte> name, RmlDictionary parameters) => 0;

    protected virtual void RenderShader(ulong shader, ulong geometry, Vector2 translation, ulong texture)
    {
    }

    protected virtual void ReleaseShader(ulong shader)
    {
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Destroys the native interface. Every context using it must be destroyed first; when RmlUi is still initialised
    /// its textures and geometry are released through this object before the call returns.
    /// </summary>
    public void Dispose()
    {
        if (_handle.IsClosed)
            return;
        if (!_handle.IsInvalid)
        {
            var status = RmlNative.RenderInterfaceDestroy(_handle.DangerousGetHandle());
            if (status < 0)
                throw new RmlException("mfrmlui_render_interface_destroy", status);
            _handle.SetHandleAsInvalid();
            RmlCore.Untrack(_handle);
        }

        _handle.Dispose();
        if (_self.IsAllocated)
            _self.Free();
        OnDisposed();
    }

    /// <summary>Called once after the native interface is gone (free GPU objects here).</summary>
    protected virtual void OnDisposed()
    {
    }

    internal static RmlFilterKind ParseFilter(ReadOnlySpan<byte> name) => name switch
    {
        _ when name.SequenceEqual("opacity"u8) => RmlFilterKind.Opacity,
        _ when name.SequenceEqual("blur"u8) => RmlFilterKind.Blur,
        _ when name.SequenceEqual("drop-shadow"u8) => RmlFilterKind.DropShadow,
        _ when name.SequenceEqual("brightness"u8) => RmlFilterKind.Brightness,
        _ when name.SequenceEqual("contrast"u8) => RmlFilterKind.Contrast,
        _ when name.SequenceEqual("invert"u8) => RmlFilterKind.Invert,
        _ when name.SequenceEqual("grayscale"u8) => RmlFilterKind.Grayscale,
        _ when name.SequenceEqual("sepia"u8) => RmlFilterKind.Sepia,
        _ when name.SequenceEqual("hue-rotate"u8) => RmlFilterKind.HueRotate,
        _ when name.SequenceEqual("saturate"u8) => RmlFilterKind.Saturate,
        _ => RmlFilterKind.Unknown,
    };

    internal static RmlShaderKind ParseShader(ReadOnlySpan<byte> name) => name switch
    {
        _ when name.SequenceEqual("linear-gradient"u8) => RmlShaderKind.LinearGradient,
        _ when name.SequenceEqual("radial-gradient"u8) => RmlShaderKind.RadialGradient,
        _ when name.SequenceEqual("conic-gradient"u8) => RmlShaderKind.ConicGradient,
        _ when name.SequenceEqual("shader"u8) => RmlShaderKind.Custom,
        _ => RmlShaderKind.Unknown,
    };

    // ── Native callbacks ─────────────────────────────────────────────────────────────────────────────────────

    private static RmlRenderInterface Self(nint user) => Unsafe.As<RmlRenderInterface>(GCHandle.FromIntPtr(user).Target!);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbCompileGeometry(nint user, RmlVertex* vertices, int numVertices, int* indices, int numIndices)
    {
        try
        {
            if (numVertices <= 0 || numIndices <= 0 || vertices is null || indices is null)
                return 0;
            return Self(user).CompileGeometry(new ReadOnlySpan<RmlVertex>(vertices, numVertices), new ReadOnlySpan<int>(indices, numIndices));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(CompileGeometry));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbRenderGeometry(nint user, ulong geometry, float tx, float ty, ulong texture)
    {
        try
        {
            Self(user).RenderGeometry(geometry, new Vector2(tx, ty), texture);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(RenderGeometry));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbReleaseGeometry(nint user, ulong geometry)
    {
        try
        {
            Self(user).ReleaseGeometry(geometry);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(ReleaseGeometry));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbLoadTexture(nint user, byte* source, int* width, int* height)
    {
        try
        {
            var handle = Self(user).LoadTexture(RmlUtf8.ToString(source) ?? "", out var w, out var h);
            *width = handle == 0 ? 0 : w;
            *height = handle == 0 ? 0 : h;
            return handle;
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(LoadTexture));
            *width = 0;
            *height = 0;
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbGenerateTexture(nint user, byte* rgba, int numBytes, int width, int height)
    {
        try
        {
            if (rgba is null || width <= 0 || height <= 0 || numBytes != width * height * 4)
                return 0;
            return Self(user).GenerateTexture(new ReadOnlySpan<byte>(rgba, numBytes), width, height);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(GenerateTexture));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbReleaseTexture(nint user, ulong texture)
    {
        try
        {
            Self(user).ReleaseTexture(texture);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(ReleaseTexture));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbEnableScissor(nint user, int enable)
    {
        try
        {
            Self(user).EnableScissorRegion(enable != 0);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(EnableScissorRegion));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbSetScissor(nint user, int x, int y, int width, int height)
    {
        try
        {
            Self(user).SetScissorRegion(x, y, width, height);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(SetScissorRegion));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbSetTransform(nint user, float* matrix)
    {
        try
        {
            // RmlUi's Matrix4f is column-major (column vectors); System.Numerics is row-major with row vectors, so the
            // same 16 floats read row by row are exactly the transpose — which is what row-vector math needs.
            Matrix4x4? m = matrix is null ? null : Unsafe.ReadUnaligned<Matrix4x4>(matrix);
            Self(user).SetTransform(m);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(SetTransform));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbEnableClipMask(nint user, int enable)
    {
        try
        {
            Self(user).EnableClipMask(enable != 0);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(EnableClipMask));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbRenderToClipMask(nint user, int operation, ulong geometry, float tx, float ty)
    {
        try
        {
            Self(user).RenderToClipMask((RmlClipMaskOperation)operation, geometry, new Vector2(tx, ty));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(RenderToClipMask));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbPushLayer(nint user)
    {
        try
        {
            return Self(user).PushLayer();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(PushLayer));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbCompositeLayers(nint user, ulong source, ulong destination, int blendMode, ulong* filters, int numFilters)
    {
        try
        {
            var span = filters is null || numFilters <= 0 ? default : new ReadOnlySpan<ulong>(filters, numFilters);
            Self(user).CompositeLayers(source, destination, (RmlBlendMode)blendMode, span);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(CompositeLayers));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbPopLayer(nint user)
    {
        try
        {
            Self(user).PopLayer();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(PopLayer));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbSaveLayerAsTexture(nint user)
    {
        try
        {
            return Self(user).SaveLayerAsTexture();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(SaveLayerAsTexture));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbSaveLayerAsMaskImage(nint user)
    {
        try
        {
            return Self(user).SaveLayerAsMaskImage();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(SaveLayerAsMaskImage));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbCompileFilter(nint user, byte* name, nint parameters)
    {
        try
        {
            var n = RmlUtf8.Span(name);
            return Self(user).CompileFilter(ParseFilter(n), n, new RmlDictionary(parameters));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(CompileFilter));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbReleaseFilter(nint user, ulong filter)
    {
        try
        {
            Self(user).ReleaseFilter(filter);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(ReleaseFilter));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong CbCompileShader(nint user, byte* name, nint parameters)
    {
        try
        {
            var n = RmlUtf8.Span(name);
            return Self(user).CompileShader(ParseShader(n), n, new RmlDictionary(parameters));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(CompileShader));
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbRenderShader(nint user, ulong shader, ulong geometry, float tx, float ty, ulong texture)
    {
        try
        {
            Self(user).RenderShader(shader, geometry, new Vector2(tx, ty), texture);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(RenderShader));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CbReleaseShader(nint user, ulong shader)
    {
        try
        {
            Self(user).ReleaseShader(shader);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, nameof(ReleaseShader));
        }
    }
}
