namespace MainframeEngine;

/// <summary>
/// Layers of RGBA8 images of one size sampled as one texture (Godot's <c>Texture2DArray</c>): terrain splat layers,
/// atlases of variants. On the GPU it is one image with a <c>2D_ARRAY</c> view (<see cref="GpuTexture.Create2DArray"/>),
/// in the colour space, mip chain and sampler of its <see cref="ImportSettings"/>; shaders sample it with
/// <c>Texture2DArray.Sample(s, float3(uv, layer))</c>.
/// </summary>
/// <remarks>
/// Built in code (<see cref="FromImages(IReadOnlyList{Texture2D}, TextureImportSettings?)"/>, the constructor) and not
/// saved with scenes: its pixels live only in memory. <see cref="SetLayerPixels"/> replaces a layer and bumps
/// <see cref="Version"/>, which re-uploads the whole array.
/// </remarks>
[EditorIcon("photo")]
public sealed class Texture2DArray : Resource
{
    private byte[] _pixels = [];
    private int _version = 1;

    /// <summary>An empty array (no layers); use the other constructor or <see cref="FromImages(IReadOnlyList{Texture2D}, TextureImportSettings?)"/>.</summary>
    public Texture2DArray()
    {
    }

    /// <summary>
    /// An array from tightly packed RGBA8 layers, one after the other (<paramref name="rgba"/> is
    /// <paramref name="width"/> × <paramref name="height"/> × 4 × <paramref name="layers"/> bytes; copied).
    /// </summary>
    public Texture2DArray(int width, int height, int layers, ReadOnlySpan<byte> rgba, TextureImportSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layers);
        var expected = (long)width * height * 4 * layers;
        if (rgba.Length != expected)
            throw new ArgumentException($"Expected {expected} bytes of RGBA8 pixels for {width}×{height}×{layers}, got {rgba.Length}.", nameof(rgba));
        Width = width;
        Height = height;
        Layers = layers;
        _pixels = rgba.ToArray();
        ImportSettings = settings ?? TextureImportSettings.Default;
    }

    /// <summary>
    /// An array of the decoded pixels of <paramref name="images"/>, in order; every image must have the size of the first.
    /// </summary>
    public static Texture2DArray FromImages(IReadOnlyList<Texture2D> images, TextureImportSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            throw new ArgumentException("A texture array needs at least one image.", nameof(images));
        var layers = new byte[images.Count][];
        int width = 0, height = 0;
        for (var i = 0; i < images.Count; i++)
        {
            var (rgba, w, h) = (images[i] ?? throw new ArgumentException($"Image {i} is null.", nameof(images))).DecodePixels();
            if (i == 0)
                (width, height) = (w, h);
            else if (w != width || h != height)
                throw new ArgumentException($"Image {i} ({images[i]}) is {w}×{h}; the array's layers are {width}×{height}.", nameof(images));
            layers[i] = rgba;
        }

        return FromImages(width, height, layers, settings);
    }

    /// <summary>An array of RGBA8 layers of <paramref name="width"/> × <paramref name="height"/> pixels (each copied).</summary>
    public static Texture2DArray FromImages(int width, int height, IReadOnlyList<byte[]> layers, TextureImportSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0)
            throw new ArgumentException("A texture array needs at least one layer.", nameof(layers));
        var layerBytes = width * height * 4;
        var pixels = new byte[(long)layerBytes * layers.Count];
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i] is not { } layer || layer.Length != layerBytes)
                throw new ArgumentException($"Layer {i} has {layers[i]?.Length ?? 0} bytes; {width}×{height} RGBA8 is {layerBytes}.", nameof(layers));
            layer.CopyTo(pixels, (long)i * layerBytes);
        }

        return new Texture2DArray(width, height, layers.Count, pixels, settings);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Number of layers (0 for an empty array).</summary>
    public int Layers { get; }

    /// <summary>Colour space (<see cref="TextureImportColorSpace.Auto"/>: sRGB for colour use), mipmaps and sampler.</summary>
    public TextureImportSettings ImportSettings
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == field) return;
            field = value;
            Touch();
        }
    } = TextureImportSettings.Default;

    /// <summary>Changes whenever the pixels or settings change (renderers re-upload).</summary>
    public int Version => _version;

    /// <summary>Every layer's RGBA8 pixels, one after the other.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>The RGBA8 pixels of layer <paramref name="layer"/>.</summary>
    public ReadOnlySpan<byte> GetLayerPixels(int layer)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)layer, (uint)Layers, nameof(layer));
        var size = Width * Height * 4;
        return _pixels.AsSpan(layer * size, size);
    }

    /// <summary>Replaces layer <paramref name="layer"/>'s pixels (same size, RGBA8).</summary>
    public void SetLayerPixels(int layer, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)layer, (uint)Layers, nameof(layer));
        var size = Width * Height * 4;
        if (rgba.Length != size)
            throw new ArgumentException($"Expected {size} bytes of RGBA8 pixels ({Width}×{Height}), got {rgba.Length}.", nameof(rgba));
        rgba.CopyTo(_pixels.AsSpan(layer * size, size));
        Touch();
    }

    public override string ToString() => $"Texture2DArray {Width}×{Height}×{Layers}";

    private void Touch()
    {
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// A <see cref="Texture2DArray"/> on the GPU in one colour space, re-uploaded when its version changes (the shape of
/// <see cref="TextureGpu"/>). Owned by whatever binds it (the terrain material).
/// </summary>
internal sealed class Texture2DArrayGpu(IVulkanContext ctx, Texture2DArray texture, TextureColorSpace colorSpace) : IDisposable
{
    private int _uploadedVersion;

    public Texture2DArray Texture { get; } = texture;
    public TextureColorSpace ColorSpace { get; } = colorSpace;
    public GpuTexture? Gpu { get; private set; }

    /// <summary>Bumped on every re-upload (descriptor sets that hold the view are rewritten).</summary>
    public int Generation { get; private set; }

    /// <summary>Uploads the array if it changed. Returns false when it has no layers.</summary>
    public bool Update()
    {
        var version = Texture.Version;
        if (version == _uploadedVersion)
            return Gpu is not null;
        _uploadedVersion = version;
        Generation++;
        Gpu?.Dispose();
        Gpu = null;
        if (Texture.Layers == 0)
            return false;
        var settings = Texture.ImportSettings;
        Gpu = GpuTexture.Create2DArray(ctx, (uint)Texture.Width, (uint)Texture.Height, (uint)Texture.Layers, Texture.Pixels, ColorSpace,
            settings.ToSampling(), settings.Mipmaps);
        return true;
    }

    public void Dispose()
    {
        Gpu?.Dispose();
        Gpu = null;
    }
}
