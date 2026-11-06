using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MainframeEngine;

/// <summary>
/// AES-256-CTR over pack entries (G19 phase 3, ADR 0239): the keystream block <c>i</c> of an entry is
/// AES(key, nonce ‖ big-endian u32 i), so any byte range decrypts on its own (audio streams seek). XOR is its own
/// inverse, so <see cref="Apply"/> encrypts and decrypts. One instance per stream or thread: it reuses its buffers and
/// its native AES context, and allocates nothing per call.
/// </summary>
internal sealed class PackCipher : IDisposable
{
    public const int KeySize = 32;

    private const int BlockSize = 16;

    // Keystream blocks made per AES call (4 KB): a read of a few KB is one call.
    private const int Batch = 256;

    /// <summary>The nonce of the header's key check: its first keystream block is stored, so a wrong key fails at open.</summary>
    private static ReadOnlySpan<byte> KeyCheckNonce => "MFPKKEYCHECK"u8;

    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counters = new byte[Batch * BlockSize];
    private readonly byte[] _keystream = new byte[Batch * BlockSize];

    public PackCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"A pack key is {KeySize} bytes, not {key.Length}.", nameof(key));
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _ecb = _aes.CreateEncryptor();
    }

    /// <summary>XORs <paramref name="data"/>, which starts <paramref name="offset"/> bytes into the stream of <paramref name="nonce"/>, with the keystream.</summary>
    public void Apply(ReadOnlySpan<byte> nonce, long offset, Span<byte> data)
    {
        if (nonce.Length != PackFormat.NonceSize)
            throw new ArgumentException($"A pack nonce is {PackFormat.NonceSize} bytes.", nameof(nonce));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        while (!data.IsEmpty)
        {
            var block = offset / BlockSize;
            var skip = (int)(offset % BlockSize);
            var blocks = (int)Math.Min(Batch, (skip + (long)data.Length + BlockSize - 1) / BlockSize);
            if (block + blocks > uint.MaxValue + 1L)
                throw new ArgumentOutOfRangeException(nameof(offset), "A pack entry is limited to 64 GB (a 32-bit block counter).");
            for (var i = 0; i < blocks; i++)
            {
                var counter = _counters.AsSpan(i * BlockSize, BlockSize);
                nonce.CopyTo(counter);
                BinaryPrimitives.WriteUInt32BigEndian(counter[PackFormat.NonceSize..], (uint)(block + i));
            }

            _ecb.TransformBlock(_counters, 0, blocks * BlockSize, _keystream, 0);
            var count = Math.Min(data.Length, blocks * BlockSize - skip);
            var stream = _keystream.AsSpan(skip, count);
            var target = data[..count];
            for (var i = 0; i < count; i++)
                target[i] ^= stream[i];
            data = data[count..];
            offset += count;
        }
    }

    /// <summary>The 16 bytes a pack written with this key stores to recognise it.</summary>
    public byte[] KeyCheck()
    {
        var check = new byte[PackFormat.KeyCheckSize];
        Apply(KeyCheckNonce, 0, check);
        return check;
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}

/// <summary>Why a pack's key failed: none was given for an encrypted pack, or it is not the one it was written with.</summary>
public enum PackKeyProblem
{
    /// <summary><c>PackKeyMissing</c>: the pack is encrypted and no key is registered.</summary>
    Missing,

    /// <summary><c>PackKeyMismatch</c>: the key does not match the pack's key check.</summary>
    Mismatch,
}

/// <summary>An encrypted pack opened without its key (<see cref="Problem"/>); thrown at open, before any read.</summary>
public sealed class PackKeyException(PackKeyProblem problem, string message) : IOException(message)
{
    public PackKeyProblem Problem { get; } = problem;

    /// <summary>The stable code logs and tools print: <c>PackKeyMissing</c> or <c>PackKeyMismatch</c>.</summary>
    public string Code => Problem == PackKeyProblem.Missing ? "PackKeyMissing" : "PackKeyMismatch";
}

/// <summary>
/// The key the running game's packs were encrypted with (G19 phase 3, ADR 0240). Exports compile it into the game
/// assembly: <c>mf-export</c> publishes with <c>-p:MainframePackKey</c>, and the generator emits a module initializer that
/// calls <see cref="Register"/> with XOR-masked constants. Builds without it (the editor, Play, <c>dotnet run</c>) register
/// nothing and read loose files. The key is never written to a file beside the game.
/// </summary>
public static class PackKeys
{
    private static byte[]? _key;

    /// <summary>Whether a key is registered.</summary>
    public static bool HasKey => Volatile.Read(ref _key) is not null;

    /// <summary>Registers the key <paramref name="masked"/> XOR <paramref name="mask"/> (generated code calls it).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void Register(ReadOnlySpan<byte> masked, ReadOnlySpan<byte> mask)
    {
        if (masked.Length != PackCipher.KeySize || mask.Length != PackCipher.KeySize)
            throw new ArgumentException($"A pack key is {PackCipher.KeySize} bytes.");
        var key = new byte[PackCipher.KeySize];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(masked[i] ^ mask[i]);
        Volatile.Write(ref _key, key);
    }

    /// <summary>The registered key, or empty.</summary>
    internal static ReadOnlySpan<byte> Current => Volatile.Read(ref _key);

    /// <summary>Forgets the key (tests).</summary>
    internal static void Clear() => Volatile.Write(ref _key, null);

    /// <summary>A new random 32-byte key as base64 (what <c>project.mfproj</c> stores).</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(PackCipher.KeySize));

    /// <summary>Parses a base64 pack key; false unless it is exactly 32 bytes.</summary>
    public static bool TryParse(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var buffer = new byte[PackCipher.KeySize + 3];
        if (!Convert.TryFromBase64String(text.Trim(), buffer, out var written) || written != PackCipher.KeySize)
            return false;
        key = buffer[..PackCipher.KeySize];
        return true;
    }
}
