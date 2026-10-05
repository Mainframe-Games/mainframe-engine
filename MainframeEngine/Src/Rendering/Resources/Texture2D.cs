using System.Globalization;
using System.Text.Json;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>How a texture's 8-bit values are interpreted when it is imported.</summary>
public enum TextureImportColorSpace : byte
{
    /// <summary>By usage: sRGB for colour slots (albedo, emission), linear for data slots (normal maps).</summary>
    Auto,

    /// <summary>Always sRGB-encoded colour (<c>R8G8B8A8_SRGB</c>).</summary>
    Srgb,

    /// <summary>Always linear data (<c>R8G8B8A8_UNORM</c>).</summary>
    Linear,
}

/// <summary>Texture minification/magnification filter.</summary>
public enum TextureFilter : byte
{
    Linear,
    Nearest,
}

/// <summary>Texture addressing outside 0..1.</summary>
public enum TextureWrap : byte
{
    Repeat,
    Clamp,
    Mirror,
}

/// <summary>
/// Import settings of an image file, stored in its <c>.meta</c> sidecar (<c>"importer": "texture"</c>):
/// <code>{ "colorSpace": "auto", "mipmaps": true, "filter": "linear", "wrap": "repeat", "anisotropy": 8 }</code>
/// Missing keys take the defaults; unknown values are reported and ignored.
/// </summary>
public sealed record TextureImportSettings
{
    public const string ImporterName = "texture";

    public static TextureImportSettings Default { get; } = new();

    public TextureImportColorSpace ColorSpace { get; init; } = TextureImportColorSpace.Auto;

    /// <summary>Generate a full mip chain on the GPU (blits) when uploading.</summary>
    public bool Mipmaps { get; init; } = true;

    public TextureFilter Filter { get; init; } = TextureFilter.Linear;

    public TextureWrap Wrap { get; init; } = TextureWrap.Repeat;

    /// <summary>Maximum anisotropic filtering (1 = off); clamped to the device limit.</summary>
    public int Anisotropy { get; init; } = 8;

    /// <summary>SVG only: rasterisation scale (Godot's <c>svg/scale</c>).</summary>
    public float SvgScale { get; init; } = 1f;

    /// <summary>
    /// Godot's <c>process/fix_alpha_border</c>: nearly transparent pixels take the colour of their nearest opaque neighbour
    /// (<see cref="ImageOps.FixAlphaEdges"/>), so filtering does not bleed dark edges.
    /// </summary>
    public bool FixAlphaBorder { get; init; }

    /// <summary>Reads the settings of <paramref name="meta"/> (defaults when null or absent).</summary>
    public static TextureImportSettings FromMeta(AssetMeta? meta, string? where = null)
    {
        var settings = Default;
        if (meta?.Settings is not { } values)
            return settings;

        foreach (var (key, value) in values)
        {
            try
            {
                settings = key switch
                {
                    "colorSpace" => settings with { ColorSpace = ParseEnum<TextureImportColorSpace>(value) },
                    "mipmaps" => settings with { Mipmaps = value.GetBoolean() },
                    "filter" => settings with { Filter = ParseEnum<TextureFilter>(value) },
                    "wrap" => settings with { Wrap = ParseEnum<TextureWrap>(value) },
                    "anisotropy" => settings with { Anisotropy = Math.Clamp(value.GetInt32(), 1, 16) },
                    "svgScale" => settings with { SvgScale = Math.Clamp(value.GetSingle(), 0.001f, 1000f) },
                    "fixAlphaBorder" => settings with { FixAlphaBorder = value.GetBoolean() },
                    _ => Unknown(settings, key, where),
                };
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
            {
                Log.Warning($"[Import] {where ?? "texture"}: invalid setting '{key}' ({value}): {e.Message}; using the default.");
            }
        }

        return settings;
    }

    /// <summary>The settings as a <c>.meta</c> "settings" object.</summary>
    public Dictionary<string, JsonElement> ToMetaSettings() => new(StringComparer.Ordinal)
    {
        ["colorSpace"] = JsonSerializer.SerializeToElement(Lower(ColorSpace), AssetJsonContext.Default.String),
        ["mipmaps"] = JsonSerializer.SerializeToElement(Mipmaps, AssetJsonContext.Default.Boolean),
        ["filter"] = JsonSerializer.SerializeToElement(Lower(Filter), AssetJsonContext.Default.String),
        ["wrap"] = JsonSerializer.SerializeToElement(Lower(Wrap), AssetJsonContext.Default.String),
        ["anisotropy"] = JsonSerializer.SerializeToElement(Anisotropy, AssetJsonContext.Default.Int32),
        ["svgScale"] = JsonSerializer.SerializeToElement(SvgScale, AssetJsonContext.Default.Single),
        ["fixAlphaBorder"] = JsonSerializer.SerializeToElement(FixAlphaBorder, AssetJsonContext.Default.Boolean),
    };

    /// <summary>The colour space for a usage: <paramref name="colorUsage"/> is true for albedo/emission slots.</summary>
    public TextureColorSpace ResolveColorSpace(bool colorUsage) => ColorSpace switch
    {
        TextureImportColorSpace.Srgb => TextureColorSpace.Srgb,
        TextureImportColorSpace.Linear => TextureColorSpace.Linear,
        _ => colorUsage ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
    };

    /// <summary>Sampler parameters for these settings.</summary>
    public TextureSampling ToSampling() => new(
        Filter == TextureFilter.Nearest ? Silk.NET.Vulkan.Filter.Nearest : Silk.NET.Vulkan.Filter.Linear,
        Wrap switch
        {
            TextureWrap.Clamp => Silk.NET.Vulkan.SamplerAddressMode.ClampToEdge,
            TextureWrap.Mirror => Silk.NET.Vulkan.SamplerAddressMode.MirroredRepeat,
            _ => Silk.NET.Vulkan.SamplerAddressMode.Repeat,
        })
    {
        MaxAnisotropy = Anisotropy,
    };

    private static TextureImportSettings Unknown(TextureImportSettings settings, string key, string? where)
    {
        Log.Warning($"[Import] {where ?? "texture"}: unknown setting '{key}'; ignored.");
        return settings;
    }

    internal static T ParseEnum<T>(JsonElement value) where T : struct, Enum =>
        Enum.TryParse<T>(value.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"expected one of {string.Join(", ", Enum.GetNames<T>()).ToLowerInvariant()}");

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}

/// <summary>
/// A 2D image used by materials and sprites (Godot's <c>Texture2D</c>). Image files (PNG, JPEG, TGA, BMP) load
/// through <see cref="ResourceLoader"/> with the import settings of their <c>.meta</c> sidecar; scenes reference
/// them by UID like any external resource. Pixels are decoded on the CPU when first needed (StbImageSharp) and
/// uploaded by the render server — once per colour space a usage asks for (sRGB albedo, UNORM normal map) — with
/// GPU-generated mipmaps.
/// </summary>
/// <remarks>
/// Textures created in code (<see cref="FromPixels"/>, <see cref="FromEncoded"/>) are not saved with scenes; only
/// file-backed (external) textures persist, as references.
/// </remarks>
[EditorIcon("photo")]
public sealed class Texture2D : Resource
{
    private string? _filePath;     // absolute path of an image file
    private byte[]? _encoded;      // PNG/JPEG/... bytes (embedded model textures)
    private byte[]? _pixels;       // decoded RGBA8 (code-created textures keep theirs)
    private int _width, _height;
    private bool _sizeKnown;
    private TextureImportSettings _settings = TextureImportSettings.Default;
    private int _version = 1;

    /// <summary>The import settings (colour space, mipmaps, sampler). Changing them re-uploads the texture.</summary>
    public TextureImportSettings ImportSettings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _settings) return;
            _settings = value;
            Touch();
        }
    }

    /// <summary>Changes whenever the pixels or settings change.</summary>
    public int Version => _version;

    /// <summary>Width in pixels (reads the image header on first use).</summary>
    public int Width
    {
        get
        {
            EnsureSize();
            return _width;
        }
    }

    /// <summary>Height in pixels (reads the image header on first use).</summary>
    public int Height
    {
        get
        {
            EnsureSize();
            return _height;
        }
    }

    /// <summary>A texture backed by an image file (path resolved by <see cref="AssetDatabase.Current"/>).</summary>
    public static Texture2D FromFile(string path, TextureImportSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = AssetDatabase.Current.ToAbsolutePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Image not found: '{path}'.", fullPath);
        return new Texture2D { _filePath = fullPath, _settings = settings ?? TextureImportSettings.Default };
    }

    /// <summary>A texture from encoded image bytes (PNG, JPEG, ...), e.g. embedded in a model file.</summary>
    public static Texture2D FromEncoded(byte[] encoded, TextureImportSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return new Texture2D { _encoded = encoded, _settings = settings ?? TextureImportSettings.Default };
    }

    /// <summary>A texture from tightly packed RGBA8 pixels (copied).</summary>
    public static Texture2D FromPixels(int width, int height, ReadOnlySpan<byte> rgba, TextureImportSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8 pixels, got {rgba.Length}.", nameof(rgba));
        return new Texture2D
        {
            _pixels = rgba.ToArray(),
            _width = width,
            _height = height,
            _sizeKnown = true,
            _settings = settings ?? TextureImportSettings.Default,
        };
    }

    /// <summary>Re-reads a file-backed texture (after the file changed on disk); users re-upload it.</summary>
    /// <summary>
    /// Replaces the pixels of a code-created texture (Godot's <c>ImageTexture.update</c>): same size, RGBA8. Renderers
    /// re-upload it on the next frame (the <see cref="Version"/> changes).
    /// </summary>
    public void SetPixels(ReadOnlySpan<byte> rgba)
    {
        if (_pixels is null)
            throw new InvalidOperationException("Only textures created with FromPixels can be updated.");
        if (rgba.Length != _pixels.Length)
            throw new ArgumentException($"Expected {_pixels.Length} bytes of RGBA8 pixels ({_width}×{_height}), got {rgba.Length}.", nameof(rgba));
        rgba.CopyTo(_pixels);
        Touch();
    }

    public void Reload()
    {
        if (_filePath is null)
            return;
        _sizeKnown = false;
        Touch();
    }

    /// <summary>
    /// Decodes the RGBA8 pixels. File-backed and encoded textures decode on every call (the CPU copy is not
    /// kept once uploaded); code-created ones return their pixels.
    /// </summary>
    public (byte[] Rgba, int Width, int Height) DecodePixels()
    {
        if (_pixels is not null)
            return (_pixels, _width, _height);

        var bytes = _encoded ?? File.ReadAllBytes(_filePath ?? throw new InvalidOperationException("The texture has no source."));
        byte[] data;
        if (IsSvg)
        {
            (data, _width, _height) = Svg.Rasterize(bytes, _settings.SvgScale);
        }
        else
        {
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha)
                        ?? throw new InvalidDataException($"Could not decode '{ResourcePath ?? _filePath ?? "embedded image"}'.");
            (data, _width, _height) = (image.Data, image.Width, image.Height);
        }

        if (_settings.FixAlphaBorder)
            ImageOps.FixAlphaEdges(data, _width, _height);
        _sizeKnown = true;
        return (data, _width, _height);
    }

    // SVG sources are rasterised by mfsvg (ADR 0112); everything else is decoded by StbImageSharp.
    private bool IsSvg => _filePath is not null && _filePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                          _encoded is { Length: > 4 } e && LooksLikeSvg(e);

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        return head.Contains("<svg", StringComparison.Ordinal);
    }

    private void EnsureSize()
    {
        if (_sizeKnown)
            return;
        try
        {
            ImageInfo? info;
            if (IsSvg)
            {
                (_width, _height) = Svg.PixelSize(_encoded ?? File.ReadAllBytes(_filePath!), _settings.SvgScale);
                info = null;
            }
            else if (_encoded is not null)
            {
                using var stream = new MemoryStream(_encoded, writable: false);
                info = ImageInfo.FromStream(stream);
            }
            else if (_filePath is not null)
            {
                using var stream = File.OpenRead(_filePath);
                info = ImageInfo.FromStream(stream);
            }
            else
            {
                info = null;
            }

            if (info is { } i)
            {
                _width = i.Width;
                _height = i.Height;
            }
        }
        catch (IOException e)
        {
            Log.Warning($"[Texture] Cannot read the size of '{ResourcePath ?? _filePath}': {e.Message}");
        }

        _sizeKnown = true;
    }

    private void Touch()
    {
        _version++;
        EmitChanged();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"Texture2D ({ResourcePath ?? (_filePath is not null ? Path.GetFileName(_filePath) : "memory")})");
}
