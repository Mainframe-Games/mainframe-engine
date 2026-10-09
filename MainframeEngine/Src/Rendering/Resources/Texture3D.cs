using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>The texel format of a <see cref="Texture3D"/>.</summary>
public enum Texture3DFormat : byte
{
    /// <summary><c>R8G8B8A8_UNORM</c>: 4 bytes per texel, values in [0, 1].</summary>
    Rgba8,

    /// <summary><c>R16G16B16A16_SFLOAT</c>: 8 bytes per texel (half floats), any range; colour LUTs, probe data.</summary>
    Rgba16F,
}

/// <summary>
/// A volume of RGBA texels sampled with three coordinates (Godot's <c>Texture3D</c>/<c>ImageTexture3D</c>): colour-grading
/// lookup tables (<see cref="PostProcessSettings.AdjustmentColorCorrection"/>, imported from <c>.cube</c> files by
/// <see cref="CubeLutImporter"/>) and, later, light-probe volumes (G8e.1). On the GPU it is one <c>VK_IMAGE_TYPE_3D</c>
/// image (<see cref="GpuTexture.Create3D"/>), sampled as stored (no colour-space decode) with trilinear or nearest
/// filtering and clamp-to-edge addressing, no mips.
/// </summary>
/// <remarks>
/// Built in code (the constructor, <see cref="FromRgba"/>) or imported; not saved with scenes: a scene refers to an
/// imported one by path. <see cref="SetTexels"/> replaces the data and bumps <see cref="Version"/>, which re-uploads it.
/// </remarks>
[EditorIcon("cube")]
public sealed class Texture3D : Resource
{
    private byte[] _texels = [];
    private int _version = 1;

    /// <summary>An empty volume (0 × 0 × 0); use the other constructor or <see cref="FromRgba"/>.</summary>
    public Texture3D()
    {
    }

    /// <summary>
    /// A volume of <paramref name="width"/> × <paramref name="height"/> × <paramref name="depth"/> texels of
    /// <paramref name="format"/>, tightly packed: x fastest, then y, then z (<paramref name="texels"/> is copied).
    /// </summary>
    public Texture3D(int width, int height, int depth, Texture3DFormat format, ReadOnlySpan<byte> texels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown 3D texture format.");
        var expected = (long)width * height * depth * BytesPerTexel(format);
        if (texels.Length != expected)
            throw new ArgumentException($"Expected {expected} bytes for {width}×{height}×{depth} {format}, got {texels.Length}.", nameof(texels));
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        _texels = texels.ToArray();
    }

    /// <summary>
    /// A <see cref="Texture3DFormat.Rgba16F"/> volume from RGBA floats (4 per texel, x fastest, then y, then z), stored as
    /// half floats.
    /// </summary>
    public static Texture3D FromRgba(int width, int height, int depth, ReadOnlySpan<float> rgba)
    {
        var count = (long)width * height * depth * 4;
        if (width <= 0 || height <= 0 || depth <= 0 || rgba.Length != count)
            throw new ArgumentException($"Expected {count} floats for {width}×{height}×{depth} RGBA, got {rgba.Length}.", nameof(rgba));
        var bytes = new byte[count * 2];
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        for (var i = 0; i < rgba.Length; i++)
            halves[i] = (Half)rgba[i];
        return new Texture3D(width, height, depth, Texture3DFormat.Rgba16F, bytes);
    }

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public Texture3DFormat Format { get; }

    /// <summary>Trilinear filtering (the default; a LUT needs it) or nearest (exact texel reads).</summary>
    public bool Filter
    {
        get;
        set
        {
            if (value == field) return;
            field = value;
            Touch();
        }
    } = true;

    /// <summary>True when the volume has texels (the parameterless constructor makes an empty one).</summary>
    public bool IsEmpty => _texels.Length == 0;

    /// <summary>Changes whenever the texels or the filter change (renderers re-upload).</summary>
    public int Version => _version;

    /// <summary>The packed texels (<see cref="BytesPerTexel"/> bytes each).</summary>
    public ReadOnlySpan<byte> Texels => _texels;

    /// <summary>The Vulkan format of <paramref name="format"/>.</summary>
    public static Format VulkanFormat(Texture3DFormat format) =>
        format == Texture3DFormat.Rgba16F ? Silk.NET.Vulkan.Format.R16G16B16A16Sfloat : Silk.NET.Vulkan.Format.R8G8B8A8Unorm;

    /// <summary>Bytes per texel of <paramref name="format"/>.</summary>
    public static int BytesPerTexel(Texture3DFormat format) => format == Texture3DFormat.Rgba16F ? 8 : 4;

    /// <summary>Texel (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) as floats (tests, CPU reference lookups).</summary>
    public System.Numerics.Vector4 GetTexel(int x, int y, int z)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Width, nameof(x));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)z, (uint)Depth, nameof(z));
        var index = ((long)z * Height + y) * Width + x;
        if (Format == Texture3DFormat.Rgba16F)
        {
            var h = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(_texels.AsSpan((int)(index * 8), 8));
            return new System.Numerics.Vector4((float)h[0], (float)h[1], (float)h[2], (float)h[3]);
        }

        var b = _texels.AsSpan((int)(index * 4), 4);
        return new System.Numerics.Vector4(b[0], b[1], b[2], b[3]) / 255f;
    }

    /// <summary>Replaces every texel (same size and format).</summary>
    public void SetTexels(ReadOnlySpan<byte> texels)
    {
        if (texels.Length != _texels.Length)
            throw new ArgumentException($"Expected {_texels.Length} bytes, got {texels.Length}.", nameof(texels));
        texels.CopyTo(_texels);
        Touch();
    }

    public override string ToString() => $"Texture3D {Width}×{Height}×{Depth} {Format}";

    private void Touch()
    {
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// A <see cref="Texture3D"/> on the GPU, re-uploaded when its version changes (the shape of <see cref="Texture2DArrayGpu"/>).
/// Owned by whatever binds it; descriptor sets that hold its view are rewritten when <see cref="Generation"/> moves.
/// </summary>
internal sealed class Texture3DGpu(IVulkanContext ctx, Texture3D texture) : IDisposable
{
    private int _uploadedVersion;

    public Texture3D Texture { get; } = texture;
    public GpuTexture? Gpu { get; private set; }

    /// <summary>Bumped on every re-upload.</summary>
    public int Generation { get; private set; }

    /// <summary>Uploads the volume if it changed (mid-frame uploads are submitted at once). False when it is empty.</summary>
    public bool Update()
    {
        var version = Texture.Version;
        if (version == _uploadedVersion)
            return Gpu is not null;
        _uploadedVersion = version;
        Generation++;
        Gpu?.Dispose();
        Gpu = null;
        if (Texture.IsEmpty)
            return false;
        var sampling = Texture.Filter ? TextureSampling.LinearClamp : TextureSampling.NearestClamp;
        Gpu = GpuTexture.Create3D(ctx, (uint)Texture.Width, (uint)Texture.Height, (uint)Texture.Depth,
            Texture3D.VulkanFormat(Texture.Format), Texture.Texels, sampling);
        return true;
    }

    public void Dispose()
    {
        Gpu?.Dispose();
        Gpu = null;
    }
}
