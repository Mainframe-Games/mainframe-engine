using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Shelf packing of decal textures into one atlas (G6.5): textures sorted by height (then width, then input order) fill
/// rows left to right. The atlas is the smallest power-of-two square, from 256 up to <see cref="MaxSize"/>, that holds
/// them; when even that is too small, every texture is halved (<see cref="Layout.Downscale"/>) until they fit.
/// </summary>
internal static class DecalAtlasPacker
{
    /// <summary>The largest atlas side.</summary>
    public const int MaxSize = 4096;

    /// <summary>Texels of edge-extended border around every tile, so mips and filtering do not bleed between tiles.</summary>
    public const int Padding = 4;

    /// <summary>A packed atlas: its side, each texture's tile (without padding) and the downscale every texture got.</summary>
    public readonly record struct Layout(int Size, (int X, int Y, int Width, int Height)[] Tiles, int Downscale);

    public static Layout Pack(ReadOnlySpan<(int Width, int Height)> sizes, int maxSize = MaxSize, int padding = Padding)
    {
        var order = new int[sizes.Length];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        var widths = new int[sizes.Length];
        var heights = new int[sizes.Length];
        for (var downscale = 1; ; downscale *= 2)
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                widths[i] = Math.Max(1, sizes[i].Width / downscale);
                heights[i] = Math.Max(1, sizes[i].Height / downscale);
            }

            Array.Sort(order, (a, b) => heights[b] != heights[a] ? heights[b].CompareTo(heights[a])
                : widths[b] != widths[a] ? widths[b].CompareTo(widths[a]) : a.CompareTo(b));
            for (var size = 256; size <= maxSize; size *= 2)
                if (TryPack(order, widths, heights, size, padding) is { } tiles)
                    return new Layout(size, tiles, downscale);
            if (widths.All(w => w <= 1) && heights.All(h => h <= 1))
                throw new InvalidOperationException($"{sizes.Length} decal textures do not fit a {maxSize}² atlas.");
        }
    }

    private static (int X, int Y, int Width, int Height)[]? TryPack(int[] order, int[] widths, int[] heights, int size, int padding)
    {
        var tiles = new (int X, int Y, int Width, int Height)[order.Length];
        int x = 0, y = 0, row = 0;
        foreach (var i in order)
        {
            int w = widths[i] + 2 * padding, h = heights[i] + 2 * padding;
            if (w > size)
                return null;
            if (x + w > size)
            {
                y += row;
                x = 0;
                row = 0;
            }

            if (y + h > size)
                return null;
            tiles[i] = (x + padding, y + padding, widths[i], heights[i]);
            x += w;
            row = Math.Max(row, h);
        }

        return tiles;
    }

    /// <summary>
    /// The atlas's RGBA8 pixels: every source (RGBA8, its own size) box-downscaled by the layout's factor into its tile,
    /// its edge texels extended over the padding.
    /// </summary>
    public static byte[] Compose(in Layout layout, IReadOnlyList<(byte[] Rgba, int Width, int Height)> sources, int padding = Padding)
    {
        var size = layout.Size;
        var atlas = new byte[size * size * 4];
        for (var t = 0; t < sources.Count; t++)
        {
            var (rgba, sw, sh) = sources[t];
            var (tx, ty, tw, th) = layout.Tiles[t];
            var d = layout.Downscale;
            for (var py = -padding; py < th + padding; py++)
            {
                var cy = Math.Clamp(py, 0, th - 1);
                for (var px = -padding; px < tw + padding; px++)
                {
                    var cx = Math.Clamp(px, 0, tw - 1);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (var oy = 0; oy < d; oy++)
                    {
                        var sy = Math.Min(cy * d + oy, sh - 1);
                        for (var ox = 0; ox < d; ox++)
                        {
                            var sx = Math.Min(cx * d + ox, sw - 1);
                            var s = (sy * sw + sx) * 4;
                            r += rgba[s];
                            g += rgba[s + 1];
                            b += rgba[s + 2];
                            a += rgba[s + 3];
                            n++;
                        }
                    }

                    var o = ((ty + py) * size + tx + px) * 4;
                    atlas[o] = (byte)(r / n);
                    atlas[o + 1] = (byte)(g / n);
                    atlas[o + 2] = (byte)(b / n);
                    atlas[o + 3] = (byte)(a / n);
                }
            }
        }

        return atlas;
    }

    /// <summary>A tile's rectangle in atlas UV: xy = its top-left corner, zw = its size.</summary>
    public static Vector4 UvRect(in Layout layout, int tile)
    {
        var (x, y, w, h) = layout.Tiles[tile];
        float inv = 1f / layout.Size;
        return new Vector4(x * inv, y * inv, w * inv, h * inv);
    }
}

/// <summary>
/// The decal textures of every world on the GPU (G6.5): one linear RGBA8 atlas with mips at set 0 binding 9. Albedo and
/// emission keep their sRGB encoding (the shader decodes them), normal and ORM maps are linear. Rebuilt on the render
/// thread when a decal shows a texture that is not in it (or that changed), never otherwise; the textures it held but no
/// decal shows any more are dropped at that rebuild.
/// </summary>
internal sealed class DecalAtlas : IDisposable
{
    private readonly IVulkanContext _ctx;
    private readonly Dictionary<Texture2D, (int Version, Vector4 Rect)> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<Texture2D> _wanted = [];
    private GpuTexture? _texture;

    public DecalAtlas(IVulkanContext ctx) => _ctx = ctx;

    /// <summary>Bumped on every rebuild (frame sets rebind binding 9).</summary>
    public long Id { get; private set; }

    /// <summary>Rebuilds so far (the render test checks a static scene rebuilds once).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>The atlas image and sampler, or null before the first decal texture.</summary>
    public DescriptorImageInfo? Descriptor => _texture?.Descriptor;

    /// <summary>True when <paramref name="texture"/> (null: nothing to pack) is in the atlas at its current version.</summary>
    public bool Holds(Texture2D? texture) =>
        texture is null || texture.Viewport is not null || (_entries.TryGetValue(texture, out var e) && e.Version == texture.Version);

    /// <summary>The texture's tile in atlas UV (xy corner, zw size), or zero when it is not in the atlas.</summary>
    public Vector4 RectOf(Texture2D? texture) =>
        texture is not null && _entries.TryGetValue(texture, out var e) ? e.Rect : Vector4.Zero;

    /// <summary>
    /// Rebuilds the atlas with the textures of <paramref name="decals"/> (call when one is not <see cref="Holds"/>) and the
    /// ones it held (other worlds' decals share it); when they no longer fit at full size, with <paramref name="decals"/>'
    /// alone.
    /// </summary>
    public void Rebuild(IReadOnlyList<Decal> decals)
    {
        if (!Build(decals, keepHeld: true))
            Build(decals, keepHeld: false);
    }

    // False when keeping the held textures forced a downscale (the caller retries without them).
    private bool Build(IReadOnlyList<Decal> decals, bool keepHeld)
    {
        _wanted.Clear();
        if (keepHeld)
            foreach (var held in _entries.Keys)
                Want(held);
        var heldCount = _wanted.Count;
        foreach (var decal in decals)
        {
            Want(decal.TextureAlbedo);
            Want(decal.TextureNormal);
            Want(decal.TextureOrm);
            Want(decal.TextureEmission);
        }

        var sources = new List<(byte[] Rgba, int Width, int Height)>(_wanted.Count);
        var textures = new List<Texture2D>(_wanted.Count);
        foreach (var texture in _wanted)
        {
            try
            {
                sources.Add(texture.DecodePixelsReadOnly());
                textures.Add(texture);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
            {
                Log.Warning($"[Decal] texture '{texture.ResourcePath ?? texture.ResourceName}' cannot be read for the decal atlas: {e.Message}");
            }
        }

        var sizes = new (int, int)[sources.Count];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = (sources[i].Width, sources[i].Height);
        var layout = sources.Count == 0 ? default : DecalAtlasPacker.Pack(sizes);
        if (layout.Downscale > 1 && keepHeld && heldCount > 0)
            return false;

        _entries.Clear();
        _texture?.Dispose(); // deletion-queued
        _texture = null;
        Id++;
        Rebuilds++;
        if (sources.Count == 0)
            return true;
        if (layout.Downscale > 1)
            Log.Warning($"[Decal] {sources.Count} decal textures do not fit a {DecalAtlasPacker.MaxSize}² atlas at full size; they are drawn at 1/{layout.Downscale}.");
        var pixels = DecalAtlasPacker.Compose(layout, sources);
        _texture = GpuTexture.Create2D(_ctx, (uint)layout.Size, (uint)layout.Size, pixels, TextureColorSpace.Linear,
            TextureSampling.LinearClamp, generateMips: true);
        for (var i = 0; i < textures.Count; i++)
            _entries[textures[i]] = (textures[i].Version, DecalAtlasPacker.UvRect(layout, i));
        Log.Debug($"[Decal] atlas rebuilt: {textures.Count} textures in {layout.Size}².");
        return true;
    }

    private void Want(Texture2D? texture)
    {
        if (texture is null || texture.Viewport is not null || _wanted.Contains(texture))
            return;
        _wanted.Add(texture);
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _entries.Clear();
    }
}
