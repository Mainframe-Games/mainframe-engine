using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine.Trees;

/// <summary>
/// The on-disk cache of the tree bakes (ADR 0172: cluster atlases and impostors), next to the pipeline cache
/// (<c>~/Library/Caches/MainframeEngine/tree-bakes</c> on macOS; <see cref="DirectoryVariable"/> overrides it, "off"
/// disables it). The bakes are deterministic, so a file named by the SHA-256 of every input (the generator's output, the
/// materials, the textures' files or pixels, the options, <see cref="Version"/>) is the bake itself: the second load of
/// a forest reads its atlases instead of rasterising them. Entries are zlib-compressed; a missing, short or corrupt file
/// is a miss, and write failures only log.
/// </summary>
internal static class TreeBakeCache
{
    public const string DirectoryVariable = "MAINFRAME_TREE_BAKE_CACHE_DIR";

    /// <summary>Bumped whenever a bake's output for the same inputs changes.</summary>
    public const int Version = 1;

    private static readonly byte[] Magic = "MFTB"u8.ToArray();
    private static readonly Lazy<string?> Folder = new(ResolveFolder);

    /// <summary>The cache folder, or null when disabled.</summary>
    public static string? Directory => Folder.Value;

    private static string? ResolveFolder()
    {
        var variable = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (variable is not null)
            return variable.Length == 0 || variable.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(variable);
        var root = PipelineCache.ResolveDirectory(Environment.GetEnvironmentVariable(PipelineCache.DirectoryVariable));
        return root is null ? null : Path.Combine(root, "tree-bakes");
    }

    /// <summary>A key hasher seeded with the cache version and <paramref name="kind"/>.</summary>
    public static IncrementalHash Hasher(string kind)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, $"{kind}|{Version}|");
        return hash;
    }

    public static void Add(IncrementalHash hash, string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

    public static void Add<T>(IncrementalHash hash, ReadOnlySpan<T> values) where T : unmanaged
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, values.Length);
        hash.AppendData(length);
        hash.AppendData(MemoryMarshal.AsBytes(values));
    }

    /// <summary>A texture's identity: its file (path, size, time) and import settings, or its pixels.</summary>
    public static void Add(IncrementalHash hash, Texture2D? texture)
    {
        if (texture is null)
        {
            Add(hash, "texture:none|");
            return;
        }

        Add(hash, $"texture|{texture.ImportSettings}|");
        if (texture.SourceFilePath is { } path && File.Exists(path))
        {
            var info = new FileInfo(path);
            Add(hash, $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|");
            return;
        }

        var (rgba, width, height) = texture.DecodePixels();
        Add(hash, $"{width}x{height}|");
        Add<byte>(hash, rgba);
    }

    public static string Name(IncrementalHash hash, string kind) => $"{kind}-{Convert.ToHexStringLower(hash.GetHashAndReset())}.bin";

    /// <summary>Reads entry <paramref name="name"/> (null on a miss).</summary>
    public static BinaryReader? TryRead(string name)
    {
        if (Directory is not { } folder)
            return null;
        var path = Path.Combine(folder, name);
        try
        {
            if (!File.Exists(path))
                return null;
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (file.Read(header) != 8 || !header[..4].SequenceEqual(Magic) ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version)
                return null;
            using var zlib = new ZLibStream(file, CompressionMode.Decompress);
            var data = new MemoryStream();
            zlib.CopyTo(data);
            data.Position = 0;
            return new BinaryReader(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"[Trees] Ignoring the bake cache entry '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>Writes entry <paramref name="name"/> (atomically: a temporary file, then a rename).</summary>
    public static void Write(string name, Action<BinaryWriter> write)
    {
        if (Directory is not { } folder)
            return;
        var path = Path.Combine(folder, name);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            using (var file = File.Create(temporary))
            {
                file.Write(Magic);
                Span<byte> version = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(version, Version);
                file.Write(version);
                using var zlib = new ZLibStream(file, CompressionLevel.Fastest);
                using var writer = new BinaryWriter(zlib);
                write(writer);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Trees] Cannot write the bake cache entry '{path}': {e.Message}");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void WriteBytes(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    public static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var data = reader.ReadBytes(length);
        return data.Length == length ? data : throw new InvalidDataException("A bake cache entry is truncated.");
    }
}
